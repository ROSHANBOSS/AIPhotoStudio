using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AIPhotoStudio
{
    public static class Portrait
    {
        public const string DetectorVersion = "windows-faceanalysis-gray8-v1";
        private static readonly object CacheGate = new object();
        private static readonly Dictionary<string,List<Dictionary<string,double>>> Cache = new Dictionary<string,List<Dictionary<string,double>>>();
        private static readonly Queue<string> Order = new Queue<string>();

        // Windows PowerShell 5.1 provides the installed .NET Framework WinRT projection.
        // The program is constant. Its only input is two dimensions and a Gray8 raster
        // on an anonymous local pipe. It never opens a file or connects to a service.
        private const string DetectorScript = @"
$ErrorActionPreference='Stop'
$ProgressPreference='SilentlyContinue'
try {
    Add-Type -AssemblyName System.Runtime.WindowsRuntime
    $detectorType=[Windows.Media.FaceAnalysis.FaceDetector,Windows.Media.FaceAnalysis,ContentType=WindowsRuntime]
    $faceType=[Windows.Media.FaceAnalysis.DetectedFace,Windows.Media.FaceAnalysis,ContentType=WindowsRuntime]
    $bitmapType=[Windows.Graphics.Imaging.SoftwareBitmap,Windows.Graphics.Imaging,ContentType=WindowsRuntime]
    $pixelType=[Windows.Graphics.Imaging.BitmapPixelFormat,Windows.Graphics.Imaging,ContentType=WindowsRuntime]
    $gray=$pixelType::Gray8
    if (-not $detectorType::IsSupported -or -not $detectorType::IsBitmapPixelFormatSupported($gray)) { throw 'Windows FaceDetector Gray8 is unavailable.' }
    $asTask=@([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object { $_.Name -eq 'AsTask' -and $_.IsGenericMethodDefinition -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation' + [char]96 + '1' })[0]
    function Wait-Result($operation,[Type]$resultType) {
        $task=$asTask.MakeGenericMethod($resultType).Invoke($null,(,$operation))
        $task.GetAwaiter().GetResult()
    }
    $reader=[System.IO.BinaryReader]::new([Console]::OpenStandardInput())
    $w=$reader.ReadInt32()
    $h=$reader.ReadInt32()
    if ($w -lt 1 -or $h -lt 1 -or $w -gt 1024 -or $h -gt 1024) { throw 'Invalid raster dimensions.' }
    [byte[]]$pixels=$reader.ReadBytes($w*$h)
    if ($pixels.Length -ne $w*$h) { throw 'Incomplete Gray8 raster.' }
    $buffer=[System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions]::AsBuffer($pixels)
    $bitmap=$bitmapType::new($gray,$w,$h)
    $bitmap.CopyFromBuffer($buffer)
    $faceDetector=Wait-Result ($detectorType::CreateAsync()) $detectorType
    $listType=[System.Collections.Generic.IList[int]].GetGenericTypeDefinition().MakeGenericType($faceType)
    $faces=@(Wait-Result ($faceDetector.DetectFacesAsync($bitmap)) $listType)
    foreach ($face in $faces) {
        $box=$face.FaceBox
        [Console]::Out.WriteLine(('{0},{1},{2},{3}' -f $box.X,$box.Y,$box.Width,$box.Height))
    }
    $bitmap.Dispose()
    [Console]::Out.WriteLine('OK')
} catch {
    [Console]::Out.WriteLine('ERROR '+[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($_.Exception.GetBaseException().Message)))
    exit 2
}";

        public static Task<List<Dictionary<string,double>>> AnalyzeAsync(BitmapSource image)
        {
            if (image == null) throw new ArgumentNullException("image");
            int width=image.PixelWidth,height=image.PixelHeight;
            if(width<1 || height<1) throw new ArgumentException("照片尺寸无效。");
            double scale=Math.Min(1,1024.0/Math.Max(width,height));
            int w=Math.Max(1,(int)Math.Round(width*scale)), h=Math.Max(1,(int)Math.Round(height*scale));
            if(w>=4) w-=w%4;
            // Copy on the calling thread. No BitmapFrame, decoder, Freezable or
            // Dispatcher is captured by the worker. Row strips bound the extra memory.
            byte[] pixels=GrayPreview(image,w,h);
            return Task.Run(delegate { return Detect(w,h,pixels); });
        }

        private static byte[] GrayPreview(BitmapSource image,int w,int h)
        {
            int sw=image.PixelWidth,sh=image.PixelHeight;
            PixelFormat format=image.Format;
            int stride=checked((sw*format.BitsPerPixel+7)/8);
            var rows=new byte[checked(stride*2)];
            var preview=new byte[checked(w*h)];
            bool direct=format==PixelFormats.Gray8 || format==PixelFormats.Gray16 || format==PixelFormats.Bgr24 ||
                format==PixelFormats.Rgb24 || format==PixelFormats.Bgr32 || format==PixelFormats.Bgra32 || format==PixelFormats.Pbgra32 ||
                format==PixelFormats.Rgb48 || format==PixelFormats.Rgba64 || format==PixelFormats.Prgba64;
            int pixelBytes=(format.BitsPerPixel+7)/8;
            for(int y=0;y<h;y++)
            {
                double fy=Math.Max(0,Math.Min(sh-1,(y+.5)*sh/h-.5)); int y0=(int)fy, rowCount=y0+1<sh ? 2 : 1;
                image.CopyPixels(new Int32Rect(0,y0,sw,rowCount),rows,stride,0);
                byte[] source=rows; int pitch=stride, bytes=pixelBytes; PixelFormat sourceFormat=format;
                if(!direct)
                {
                    // The conversion only sees independent managed rows, never the original decoder.
                    var strip=BitmapSource.Create(sw,rowCount,96,96,format,image.Palette,rows,stride);
                    var converted=new FormatConvertedBitmap(strip,PixelFormats.Bgra32,null,0);
                    source=new byte[checked(sw*4*rowCount)]; pitch=sw*4; bytes=4; sourceFormat=PixelFormats.Bgra32;
                    converted.CopyPixels(source,pitch,0);
                }
                int second=rowCount==2 ? pitch : 0; double by=fy-y0;
                for(int x=0;x<w;x++)
                {
                    double fx=Math.Max(0,Math.Min(sw-1,(x+.5)*sw/w-.5)); int ix=(int)fx;
                    int p0=ix*bytes,p1=Math.Min(ix+1,sw-1)*bytes; double bx=fx-ix;
                    double top=GrayAt(source,p0,sourceFormat)*(1-bx)+GrayAt(source,p1,sourceFormat)*bx;
                    double bottom=GrayAt(source,second+p0,sourceFormat)*(1-bx)+GrayAt(source,second+p1,sourceFormat)*bx;
                    preview[y*w+x]=(byte)Math.Round(Math.Max(0,Math.Min(255,top*(1-by)+bottom*by)));
                }
            }
            return preview;
        }

        private static double GrayAt(byte[] row,int offset,PixelFormat format)
        {
            if(format==PixelFormats.Gray8) return row[offset];
            if(format==PixelFormats.Gray16) return (row[offset]+256.0*row[offset+1])/257;
            double r,g,b,a=1;
            if(format==PixelFormats.Rgb48 || format==PixelFormats.Rgba64 || format==PixelFormats.Prgba64)
            {
                r=(row[offset]+256.0*row[offset+1])/257; g=(row[offset+2]+256.0*row[offset+3])/257; b=(row[offset+4]+256.0*row[offset+5])/257;
                if(format!=PixelFormats.Rgb48) a=(row[offset+6]+256.0*row[offset+7])/65535;
            }
            else if(format==PixelFormats.Rgb24) {r=row[offset];g=row[offset+1];b=row[offset+2];}
            else
            {
                b=row[offset];g=row[offset+1];r=row[offset+2];
                if(format==PixelFormats.Bgra32 || format==PixelFormats.Pbgra32) a=row[offset+3]/255.0;
            }
            double luma=.299*r+.587*g+.114*b;
            return luma*(format==PixelFormats.Pbgra32 || format==PixelFormats.Prgba64 ? 1 : a)+127*(1-a);
        }
        private static List<Dictionary<string,double>> Copies(List<Dictionary<string,double>> boxes)
        {
            return boxes.Select(b => new Dictionary<string,double>(b,StringComparer.Ordinal)).ToList();
        }
        private static List<Dictionary<string,double>> Detect(int w,int h,byte[] pixels)
        {
            string signature;
            using (var sha=SHA256.Create()) signature=DetectorVersion+"/"+Environment.OSVersion.Version.Build+"/"+w+"/"+h+"/"+Convert.ToBase64String(sha.ComputeHash(pixels));
            lock (CacheGate)
            {
                List<Dictionary<string,double>> cached;
                if (Cache.TryGetValue(signature,out cached)) return Copies(cached);
            }
            string executable=Path.Combine(Environment.SystemDirectory,@"WindowsPowerShell\v1.0\powershell.exe");
            if (!File.Exists(executable)) throw new NotSupportedException("此 Windows 安装缺少系统人脸检测所需的本地运行环境。");
            var start=new ProcessStartInfo {
                FileName=executable, Arguments="-NoLogo -NoProfile -NonInteractive -EncodedCommand "+Convert.ToBase64String(Encoding.Unicode.GetBytes(DetectorScript)),
                UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,
                RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true
            };
            List<string> lines=new List<string>();
            var errors=new StringBuilder();
            using (var process=new Process { StartInfo=start })
            {
                process.OutputDataReceived+=delegate(object sender,DataReceivedEventArgs e) { if (e.Data!=null) lock(lines) if(lines.Count<1024) lines.Add(e.Data); };
                process.ErrorDataReceived+=delegate(object sender,DataReceivedEventArgs e) { if(e.Data!=null) lock(errors) if(errors.Length<640) errors.Append(e.Data); };
                process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine();
                var writer=Task.Run(delegate
                {
                    using (var pipe=new BinaryWriter(process.StandardInput.BaseStream))
                    { pipe.Write(w); pipe.Write(h); pipe.Write(pixels); }
                });
                if (!process.WaitForExit(30000))
                {
                    process.Kill(); process.WaitForExit();
                    try { writer.GetAwaiter().GetResult(); } catch(IOException) { }
                    throw new TimeoutException("Windows 人脸检测超时。");
                }
                process.WaitForExit();
                try { writer.GetAwaiter().GetResult(); } catch(IOException) { if(process.ExitCode==0) throw; }
                if (process.ExitCode!=0 || !lines.Contains("OK"))
                {
                    // EncodedCommand writes PowerShell diagnostics in CLIXML; omit that
                    // envelope and retain the short native exception written to stderr.
                    string detail=errors.ToString();
                    if (detail.Contains("CLIXML")) detail="";
                    string encoded=lines.FirstOrDefault(s=>s.StartsWith("ERROR ",StringComparison.Ordinal));
                    if(encoded!=null) detail=Encoding.UTF8.GetString(Convert.FromBase64String(encoded.Substring(6)));
                    throw new NotSupportedException("当前 Windows 人脸检测组件未能完成识别。"+detail);
                }
            }
            var result=new List<Dictionary<string,double>>();
            foreach(string line in lines)
            {
                if(line=="OK") continue;
                string[] p=line.Split(',');
                int x,y,bw,bh;
                if(p.Length!=4 || !Int32.TryParse(p[0],out x)||!Int32.TryParse(p[1],out y)||!Int32.TryParse(p[2],out bw)||!Int32.TryParse(p[3],out bh) ||
                    x<0 || y<0 || bw<=0 || bh<=0 || (long)x+bw>w || (long)y+bh>h) throw new InvalidDataException("系统返回了无效人脸检测框。");
                result.Add(new Dictionary<string,double> { {"x",(double)x/w},{"y",(double)y/h},{"width",(double)bw/w},{"height",(double)bh/h} });
            }
            result=result.OrderByDescending(f=>f["width"]*f["height"]).ToList();
            lock(CacheGate)
            {
                if(!Cache.ContainsKey(signature))
                {
                    if(Order.Count==24) Cache.Remove(Order.Dequeue());
                    Cache.Add(signature,result); Order.Enqueue(signature);
                }
            }
            return Copies(result);
        }

        public static Dictionary<string,double> FacePatch(IDictionary<string,double> box)
        {
            if(box==null || box.Count!=4 || !box.ContainsKey("x") || !box.ContainsKey("y") || !box.ContainsKey("width") || !box.ContainsKey("height"))
                throw new ArgumentException("请选择系统返回的人脸区域。");
            var patch=new Dictionary<string,double> {{"portrait_face_x",box["x"]},{"portrait_face_y",box["y"]},{"portrait_face_w",box["width"]},{"portrait_face_h",box["height"]}};
            if(!PortraitEdits.HasFace(patch)) throw new ArgumentException("检测框超出照片范围。");
            return patch;
        }
    }
}
