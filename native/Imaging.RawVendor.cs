using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AIPhotoStudio
{
    internal static class VendorRaw
    {
        internal sealed class Result
        {
            internal Dictionary<string,object> Metadata;
            internal BitmapSource Surface;
            internal PackedCameraSurface Packed;
        }
        internal static bool Supports(string kind){return CameraRawModes.Find(kind)!=null;}
        internal static bool UsesWorker(string path,string kind,Stream input)
        {
            if(!String.Equals(kind,"DNG",StringComparison.OrdinalIgnoreCase))return Supports(kind);
            return RawInspection.Read(input,path).UsesLosslessCameraDng(input);
        }
        private static void ReadExact(Stream stream,byte[] data,int count)
        {
            int at=0;
            while(at<count)
            {
                int read=stream.Read(data,at,count-at);if(read==0)throw new EndOfStreamException();
                at+=read;
            }
        }
        private static void Stop(Process child)
        {
            try{if(!child.HasExited)child.Kill();}catch(InvalidOperationException){}catch(System.ComponentModel.Win32Exception){}
        }
        internal static Result Read(string path,string operation,int maxEdge,CancellationToken token,int selected=0,long viewportBudget=0,
            Action<Result> previewReady=null)
        {
            token.ThrowIfCancellationRequested();
            string dir=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"Raw"),exe=Path.Combine(dir,"CameraRawWorker.exe");
            if(!File.Exists(exe)||!File.Exists(Path.Combine(dir,"libraw.dll")))
                throw new NotSupportedException("本机缺少经过验证的相机 RAW 解码组件。");
            var options=new ProcessStartInfo(exe,operation+" "+maxEdge.ToString(CultureInfo.InvariantCulture)+" "+selected.ToString(CultureInfo.InvariantCulture))
            {
                WorkingDirectory=dir,UseShellExecute=false,CreateNoWindow=true,
                RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true
            };
            options.EnvironmentVariables["OMP_NUM_THREADS"]=Math.Min(6,Environment.ProcessorCount).ToString(CultureInfo.InvariantCulture);
            using(var child=new Process{StartInfo=options})
            {
                string message=null;int timeout=0;
                child.ErrorDataReceived+=(sender,eventArgs)=>{
                    if(eventArgs.Data==null||eventArgs.Data.Length>4096)return;
                    try{string decoded=Encoding.UTF8.GetString(Convert.FromBase64String(eventArgs.Data));if(decoded.Length>0)Interlocked.Exchange(ref message,decoded);}
                    catch(FormatException){}
                };
                child.Start();child.BeginErrorReadLine();
                using(var registration=token.Register(()=>Stop(child)))
                using(var timer=new Timer(state=>{Interlocked.Exchange(ref timeout,1);Stop(child);},null,operation=="inspect"?20000:180000,Timeout.Infinite))
                {
                    try
                    {
                        child.StandardInput.WriteLine(Convert.ToBase64String(Encoding.UTF8.GetBytes(path)));child.StandardInput.Close();
                        using(var stream=new BufferedStream(child.StandardOutput.BaseStream,131072))
                        {
                            var prefix=new byte[4];ReadExact(stream,prefix,4);
                            int n=BitConverter.ToInt32(prefix,0);
                            if(n<2||n>32768)throw new InvalidDataException("相机解码器元数据大小无效。");
                            var data=new byte[n];ReadExact(stream,data,n);
                            var json=new JavaScriptSerializer{MaxJsonLength=32768,RecursionLimit=8};
                            var outcome=new Result{Metadata=json.Deserialize<Dictionary<string,object>>(Encoding.UTF8.GetString(data))};
                            var meta=outcome.Metadata;CameraRawModes.Verify(meta);
                            if(operation=="poster")
                            {
                                int bytes=Convert.ToInt32(meta["posterBytes"]);
                                if(bytes<0||bytes>8*1024*1024)throw new InvalidDataException("相机预览大小无效。");
                                if(bytes>0)
                                {
                                    var dataJpeg=new byte[bytes];ReadExact(stream,dataJpeg,bytes);
                                    using(var jpeg=new MemoryStream(dataJpeg,false))
                                    {
                                        var header=new JpegBitmapDecoder(jpeg,BitmapCreateOptions.DelayCreation,BitmapCacheOption.None).Frames[0];
                                        int w=header.PixelWidth,h=header.PixelHeight;
                                        double aspect=Convert.ToDouble(meta["storedWidth"])/Convert.ToDouble(meta["storedHeight"]);
                                        if(w>0&&h>0&&(long)w*h<=80000000&&Math.Max(w,h)>=300&&Math.Abs((double)w/h/aspect-1)<.03)
                                        {
                                            jpeg.Position=0;
                                            var poster=new BitmapImage();poster.BeginInit();poster.CacheOption=BitmapCacheOption.OnLoad;
                                            poster.StreamSource=jpeg;poster.DecodePixelWidth=Math.Max(1,(int)Math.Round(w*Math.Min(1,768.0/Math.Max(w,h))));
                                            poster.EndInit();poster.Freeze();outcome.Surface=poster;
                                        }
                                    }
                                }
                            }
                            if(operation=="render"||operation=="previewDetail")
                            {
                                int w=Convert.ToInt32(meta["outputWidth"]),h=Convert.ToInt32(meta["outputHeight"]);
                                int sw=Convert.ToInt32(meta["storedWidth"]),sh=Convert.ToInt32(meta["storedHeight"]);
                                double scale=maxEdge==0?1:Math.Min(1,(double)maxEdge/Math.Max(sw,sh));
                                if(w!=Math.Max(1,(int)Math.Round(sw*scale,MidpointRounding.AwayFromZero))||
                                    h!=Math.Max(1,(int)Math.Round(sh*scale,MidpointRounding.AwayFromZero))||(long)w*h>80000000)
                                    throw new InvalidDataException("相机解码器未返回请求的图像尺寸。");
                                if(viewportBudget>0&&(long)w*h*8>viewportBudget)
                                {
                                    outcome.Packed=PackedCameraSurface.ReadRows(w,h,viewportBudget,(buffer,count)=>ReadExact(stream,buffer,count),token,out outcome.Surface);
                                }
                                else
                                {
                                    var surface=new WriteableBitmap(w,h,96,96,PixelFormats.Rgba64,null);
                                    int stride=w*8,rows=Math.Max(1,Math.Min(64,2097152/stride));var band=new byte[stride*rows];
                                    for(int y=0;y<h;y+=rows)
                                    {
                                        token.ThrowIfCancellationRequested();int take=Math.Min(rows,h-y);ReadExact(stream,band,take*stride);
                                        surface.WritePixels(new Int32Rect(0,y,w,take),band,stride,0);
                                    }
                                    surface.Freeze();outcome.Surface=surface;
                                }
                                if(previewReady!=null){
                                    previewReady(outcome);
                                    if(Object.Equals(meta["detailFollows"],true)){
                                        var detail=new Result{Metadata=new Dictionary<string,object>(meta)};
                                        detail.Metadata["outputWidth"]=sw;detail.Metadata["outputHeight"]=sh;
                                        detail.Packed=PackedCameraSurface.ReadRows(sw,sh,Imaging.CameraViewport.Budget,(buffer,count)=>ReadExact(stream,buffer,count),
                                            token,out detail.Surface,true);
                                        outcome=detail;
                                    }
                                }
                            }
                            if(stream.ReadByte()!=-1)throw new InvalidDataException("相机解码器返回了多余的数据。");
                            child.WaitForExit();token.ThrowIfCancellationRequested();
                            if(child.ExitCode!=0)throw new InvalidDataException("相机解码子进程未正常完成。");
                            return outcome;
                        }
                    }
                    catch(IOException)
                    {
                        if(!child.HasExited)Stop(child);child.WaitForExit();token.ThrowIfCancellationRequested();
                        if(Volatile.Read(ref timeout)!=0)throw new TimeoutException("此相机 RAW 解码超过本次时间预算。");
                        throw new NotSupportedException(message??"相机 RAW 完整解码失败。文件可能损坏或该模式未通过验证。");
                    }
                    finally{Stop(child);}
                }
            }
        }
    }
}
