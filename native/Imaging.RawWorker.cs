#if CAMERA_RAW_WORKER
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace AIPhotoStudio
{
    internal static class CameraRawWorker
    {
        // libraw_types.h, official x64 0.22.3 build. scripts/raw-abi.py independently regenerates these layouts.
        private const int Sizes=8, Id=192, Lens=632, Maker=1928, Params=5232, UnpackParams=5536;
        private const int Color=5592, Other=192680, Data=193768;
        private const string Dll="libraw.dll";
        private const string DllDigest="2c4df36d9011267fd3ffd5c23e80d69d5f42c3d503f03ac02448f5c8a7fe0fa2";
        private const long MaxPixels=80000000;
        private static readonly JavaScriptSerializer Json=new JavaScriptSerializer{MaxJsonLength=32768,RecursionLimit=8};
        [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] private static extern IntPtr libraw_init(uint flags);
        [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] private static extern int libraw_unpack_thumb(IntPtr p);
        [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] private static extern IntPtr libraw_dcraw_make_mem_thumb(IntPtr p,out int error);
        [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] private static extern void libraw_close(IntPtr p);
        [DllImport(Dll,ExactSpelling=true,CharSet=CharSet.Unicode,CallingConvention=CallingConvention.Cdecl)] private static extern int libraw_open_wfile(IntPtr p,string file);
        [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] private static extern int libraw_versionNumber();
        [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] private static extern IntPtr libraw_get_iparams(IntPtr p);
        [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] private static extern IntPtr libraw_get_lensinfo(IntPtr p);
        [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] private static extern IntPtr libraw_get_imgother(IntPtr p);
        [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] private static extern int libraw_get_raw_width(IntPtr p);
        [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] private static extern int libraw_get_color_maximum(IntPtr p);
        [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] private static extern IntPtr libraw_unpack_function_name(IntPtr p);
        [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] private static extern int libraw_COLOR(IntPtr p,int row,int col);
        [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] private static extern float libraw_get_cam_mul(IntPtr p,int channel);
        [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] private static extern float libraw_get_pre_mul(IntPtr p,int channel);
        [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] private static extern float libraw_get_rgb_cam(IntPtr p,int output,int camera);
        [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] private static extern int libraw_unpack(IntPtr p);
        [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] private static extern int libraw_dcraw_process(IntPtr p);
        [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] private static extern IntPtr libraw_dcraw_make_mem_image(IntPtr p,out int code);
        [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] private static extern void libraw_dcraw_clear_mem(IntPtr image);
        [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] private static extern void libraw_set_demosaic(IntPtr p,int value);
        [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] private static extern void libraw_set_output_bps(IntPtr p,int value);
        [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] private static extern void libraw_set_output_color(IntPtr p,int value);
        [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] private static extern void libraw_set_no_auto_bright(IntPtr p,int value);
        [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] private static extern void libraw_set_adjust_maximum_thr(IntPtr p,float value);
        [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] private static extern void libraw_set_gamma(IntPtr p,int index,float value);
        [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] private static extern void libraw_set_highlight(IntPtr p,int value);
        [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] private static extern IntPtr libraw_strerror(int code);
        private static int Int(IntPtr p,int offset){return Marshal.ReadInt32(p,offset);}
        private static int Short(IntPtr p,int offset){return (ushort)Marshal.ReadInt16(p,offset);}
        private static string Text(IntPtr p,int offset,int length)
        {
            byte[] s=new byte[length];Marshal.Copy(IntPtr.Add(p,offset),s,0,length);int n=Array.IndexOf(s,(byte)0);
            return Encoding.ASCII.GetString(s,0,n<0?s.Length:n).Trim();
        }
        private static string Hex(byte[] value){return BitConverter.ToString(value).Replace("-","").ToLowerInvariant();}
        private static void Check(int code,string step)
        {
            if(code!=0)throw new InvalidDataException(step+"失败："+Marshal.PtrToStringAnsi(libraw_strerror(code)));
        }
        private static void Dimensions(int w,int h)
        {
            if(w<2||h<2||w>65535||h>65535||(long)w*h>MaxPixels)throw new NotSupportedException("相机 RAW 感光尺寸超过本版范围。");
        }
        private static int Orientation(int flip)
        {
            int[] exif={1,2,4,3,5,8,6,7};
            if(flip<0||flip>7)throw new InvalidDataException("相机方向字段无效。");
            return exif[flip];
        }
        private static int ModeField(IntPtr p,string ext)
        {
            switch(ext)
            {
                case "NEF":case "NRW":return Short(p,Maker+168+88);
                case "CR3":case "CR2":case "CRW":return (short)Short(p,Maker+116);
                case "ARW":case "SR2":case "SRF":return Short(p,Maker+1664+150);
                case "RW2":return Short(p,Maker+2092);
                case "ORF":return Int(p,Maker+1200+440);
                case "DNG":return Int(p,Id+332);
                default:return 0;
            }
        }
        private static int ModeSelector(IntPtr p,string ext)
        {
            switch(ext)
            {
                case "ARW":case "SR2":case "SRF":return Short(p,Maker+1664+160);
                case "RW2":return Int(p,Maker+2092+36);
                case "ORF":return Int(p,Maker+1200+444);
                default:return 0;
            }
        }
        private static byte[] ReadAt(Stream file,long position,int length)
        {
            file.Position=position;var bytes=new byte[length];int at=0;
            while(at<length){int n=file.Read(bytes,at,length-at);if(n==0)throw new InvalidDataException("RAW 容器头不完整。");at+=n;}
            return bytes;
        }
        private static uint Big(byte[] b,int at){return ((uint)b[at]<<24)|((uint)b[at+1]<<16)|((uint)b[at+2]<<8)|b[at+3];}
        private static readonly string[] CrxPath={"moov","trak","mdia","minf","stbl","stsd","CRAW","CMP1"};
        private static void CrxFields(Stream file,long start,long end,int depth,int w,int h,List<string> modes,ref int budget)
        {
            for(long at=start;at+8<=end;)
            {
                if(--budget<0)throw new InvalidDataException("CR3 轨道目录过大。");
                byte[] header=ReadAt(file,at,8);long size=Big(header,0),payload=at+8;
                if(size==1){byte[] wide=ReadAt(file,payload,8);if(Big(wide,0)!=0)throw new NotSupportedException("CR3 大型箱体超过当前范围。");size=Big(wide,4);payload+=8;}
                if(size==0)size=end-at;
                if(size<payload-at||size>end-at)throw new InvalidDataException("CR3 箱体边界无效。");
                long next=at+size;
                if(Encoding.ASCII.GetString(header,4,4)==CrxPath[depth])
                {
                    if(depth==CrxPath.Length-1)
                    {
                        if(next-payload<40)throw new InvalidDataException("CR3 感光轨参数不完整。");
                        var body=ReadAt(file,payload,40);
                        if(Big(body,8)==w&&Big(body,12)==h)modes.Add(BitConverter.ToString(body,24,4).Replace("-","").ToLowerInvariant());
                    }
                    else
                    {
                        long child=payload+(depth==5?8:depth==6?82:0);
                        if(child<=next)CrxFields(file,child,next,depth+1,w,h,modes,ref budget);
                    }
                }
                at=next;
            }
        }
        private static void Container(string path,string ext,Dictionary<string,object> m)
        {
            using(var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
            {
                if(file.Length<1024||file.Length>2147483647)throw new NotSupportedException("相机 RAW 文件大小超出可处理范围。");
                var h=ReadAt(file,0,16);
                bool tiff=(h[0]==73&&h[1]==73&&h[2]==42&&h[3]==0)||(h[0]==77&&h[1]==77&&h[2]==0&&h[3]==42);
                bool cr2=tiff&&h[8]==67&&h[9]==82&&h[10]==2&&h[11]==0;
                bool ok=ext=="CR3"?Encoding.ASCII.GetString(h,4,8)=="ftypcrx ":
                    ext=="CRW"?Encoding.ASCII.GetString(h,6,8)=="HEAPCCDR":
                    ext=="RAF"?Encoding.ASCII.GetString(h,0,15)=="FUJIFILMCCD-RAW":
                    ext=="RW2"?Hex(ReadAt(file,0,24))=="494955001800000088e774d8f8251d4d947a6e77822b5d6a":
                    ext=="ORF"?(Encoding.ASCII.GetString(h,0,4)=="IIRO"||Encoding.ASCII.GetString(h,0,4)=="MMOR"):
                    ext=="CR2"?cr2:tiff&&!cr2;
                if(!ok)throw new NotSupportedException("文件头与相机 RAW 扩展名不匹配。");
                if(ext=="CR3")
                {
                    var modes=new List<string>();int budget=4096;
                    CrxFields(file,0,file.Length,0,(int)m["sensorWidth"],(int)m["sensorHeight"],modes,ref budget);
                    if(modes.Count!=(int)m["frameCount"]||modes.Count==0||
                        modes.Any(x=>x.Length!=8||Convert.ToInt32(x.Substring(0,2),16)!=(int)m["rawHeaderBits"]||x[2]!='4'||
                            (x[4]!='0'&&x[4]!='3')||x[5]<'0'||x[5]>'3'))
                        throw new NotSupportedException("Canon CR3 感光轨的编码和位深组合尚未验证。");
                    m["primaryTrackEncoding"]=modes[0];
                    m["trackEncodings"]=String.Join(",",modes);
                }
                m["fileBytes"]=file.Length;
            }
        }
        private static Dictionary<string,object> Metadata(IntPtr p,string ext,int selected)
        {
            int w=Short(p,Sizes+6),h=Short(p,Sizes+4),rw=Short(p,Sizes+2),rh=Short(p,Sizes);
            Dimensions(w,h);Dimensions(rw,rh);
            if(libraw_get_iparams(p)!=IntPtr.Add(p,Id)||libraw_get_lensinfo(p)!=IntPtr.Add(p,Lens)||
                libraw_get_imgother(p)!=IntPtr.Add(p,Other)||libraw_get_raw_width(p)!=rw||libraw_get_color_maximum(p)!=Int(p,Color+147496))
                throw new InvalidDataException("LibRaw 结构版本不匹配。");
            int frames=Int(p,Id+328),colors=Int(p,Id+340),flip=Int(p,Sizes+32);
            uint filters=unchecked((uint)Int(p,Id+344));
            if((Int(p,Id+332)!=0)!=(ext=="DNG")||filters==0||colors<3||colors>4||frames<1||frames>4)
                throw new NotSupportedException("文件未报告该机型所需的彩色感光阵列。");
            string make=Text(p,Id+196,64),model=Text(p,Id+260,64),decoder=Marshal.PtrToStringAnsi(libraw_unpack_function_name(p));
            string channelNames=Text(p,Id+420,5);var cfa=new StringBuilder();int repeat=filters==9?6:2;
            for(int y=0;y<repeat;y++)for(int x=0;x<repeat;x++)
            {
                int index=libraw_COLOR(p,y,x);if(index>=0&&index<channelNames.Length)cfa.Append(channelNames[index]);
                else cfa.Append('?');
            }
            var wb=new float[4];var matrix=new float[12];var black=new int[4];
            for(int i=0;i<4;i++)
            {
                wb[i]=libraw_get_cam_mul(p,i);black[i]=Int(p,Color+131072+i*4);
                for(int j=0;j<3;j++)matrix[j*4+i]=libraw_get_rgb_cam(p,j,i);
            }
            int bits=Int(p,Color+187076),white=libraw_get_color_maximum(p),orientation=Orientation(flip);
            string date="";long timestamp=Marshal.ReadInt64(p,Other+16);
            if(timestamp>0&&timestamp<4102444800L)date=new DateTime(1970,1,1).AddSeconds(timestamp).ToString("yyyy-MM-dd HH:mm:ss",CultureInfo.InvariantCulture);
            int cw=w,ch=h,left=Short(p,Sizes+10),top=Short(p,Sizes+8),cx=Short(p,Sizes+166),cy=Short(p,Sizes+168);
            double baseline=0;
            if(ext=="DNG")
            {
                cw=Short(p,Sizes+170);ch=Short(p,Sizes+172);
                if(cw<2||ch<2||cx<left||cy<top||cx-left+cw>w||cy-top+ch>h)
                    throw new InvalidDataException("DNG 建议裁切超出有效感光区。");
                var exposure=new float[1];Marshal.Copy(IntPtr.Add(p,Color+148648+32920),exposure,0,1);
                baseline=exposure[0];
                if(!(baseline>=-5&&baseline<=5))throw new NotSupportedException("DNG 基线曝光超出已验证范围。");
            }
            var meta=new Dictionary<string,object>{
                {"format",ext},{"pixelFormat","CFA → RGB16"},{"bitsPerPixel",bits},{"bitsPerSample",bits},{"rawHeaderBits",bits},
                {"cameraManufacturer",make},{"cameraModel",model},{"cameraFirmware",Text(p,Maker+2856+66,128)},
                {"originalManufacturer",Text(p,Id+4,64)},{"originalCameraModel",Text(p,Id+68,64)},
                {"cameraSoftware",Text(p,Id+132,64)},{"dateTaken",date},{"hasColorProfile",false},
                {"colorSpace","Camera matrix → linear sRGB → 16-bit sRGB working surface"},{"decoder","LibRaw 0.22.3"},
                {"unpackFunction",decoder},{"sensorWidth",rw},{"sensorHeight",rh},{"activeWidth",w},{"activeHeight",h},{"storedWidth",cw},{"storedHeight",ch},
                {"width",orientation>=5?ch:cw},{"height",orientation>=5?cw:ch},{"orientation",orientation},{"originalFlip",flip},
                {"topMargin",top},{"leftMargin",left},{"frameCount",frames},{"selectedRawFrame",selected},
                {"cfaFilters",filters.ToString("x8",CultureInfo.InvariantCulture)},{"cfaPattern",cfa.ToString()},{"rawColors",colors},
                {"asShotMultipliers",wb},{"whiteBalanceSource",wb[0]>0&&wb[1]>0&&wb[2]>0?"as-shot":"camera daylight calibration"},
                {"hasCameraMatrix",matrix.All(v=>!Single.IsNaN(v)&&!Single.IsInfinity(v)&&Math.Abs(v)<32)&&
                    Math.Abs(matrix[0]*matrix[5]*matrix[10]+matrix[1]*matrix[6]*matrix[8]+matrix[2]*matrix[4]*matrix[9]-
                        matrix[2]*matrix[5]*matrix[8]-matrix[1]*matrix[4]*matrix[10]-matrix[0]*matrix[6]*matrix[9])>.00001},
                {"cameraToSrgbMatrix",matrix},{"blackLevel",Int(p,Color+147488)},{"channelBlackLevel",black},{"whiteLevel",white},{"levelStage","file header"},
                {"insetLeft",cx},{"insetTop",cy},{"insetWidth",Short(p,Sizes+170)},{"insetHeight",Short(p,Sizes+172)},
                {"manufacturerMode",ModeField(p,ext)},{"selector",ModeSelector(p,ext)}
            };
            if(ext=="DNG") {meta["baselineExposure"]=baseline;meta["colorSpace"]="DNG D65 ColorMatrix2 + AsShotNeutral → sRGB";}
            if(ext=="ORF")meta["makerValidBits"]=Short(p,Maker+1200+6);
            return meta;
        }
        private static void Header(Stream output,Dictionary<string,object> metadata)
        {
            byte[] bytes=Encoding.UTF8.GetBytes(Json.Serialize(metadata));
            if(bytes.Length>32768)throw new InvalidDataException("相机元数据过长。");
            var length=BitConverter.GetBytes(bytes.Length);output.Write(length,0,4);output.Write(bytes,0,bytes.Length);
        }
        private static void BlackPattern(IntPtr p,Dictionary<string,object> metadata)
        {
            int rows=Int(p,Color+131072+16),cols=Int(p,Color+131072+20);
            if(rows<0||cols<0||(long)rows*cols>4096)throw new InvalidDataException("黑电平重复周期无效。");
            metadata["blackRepeatRows"]=rows;metadata["blackRepeatCols"]=cols;
            int n=Math.Min(rows*cols,128);var values=new int[n];
            for(int i=0;i<n;i++)values[i]=Int(p,Color+131072+24+i*4);
            metadata["blackRepeatValues"]=values;
            if(n!=rows*cols)metadata["blackRepeatValuesTruncated"]=true;
        }
        private static IntPtr Plane(IntPtr p)
        {
            IntPtr pick=Marshal.ReadIntPtr(p,Data+8);
            if(pick==IntPtr.Zero)throw new InvalidDataException("解码器没有返回未去马赛克的传感器平面。");
            return pick;
        }
        private static void Measure(IntPtr p,Dictionary<string,object> metadata)
        {
            int w=(int)metadata["sensorWidth"],h=(int)metadata["sensorHeight"],pitch=Int(p,Data+512+16);
            if(pitch==0)pitch=w*2;
            if(pitch<w*2||pitch>w*2+16384)throw new InvalidDataException("感光行距无效。");
            IntPtr source=Plane(p);var data=new byte[w*2];var codes=new bool[65536];int min=65535,max=0,count=0;
            using(var sha=SHA256.Create())
            {
                for(int y=0;y<h;y++)
                {
                    Marshal.Copy(IntPtr.Add(source,checked(y*pitch)),data,0,data.Length);
                    sha.TransformBlock(data,0,data.Length,data,0);
                    for(int j=0;j<data.Length;j+=2)
                    {
                        int v=data[j]|data[j+1]<<8;if(codes[v])continue;codes[v]=true;count++;
                        min=Math.Min(min,v);max=Math.Max(max,v);
                    }
                }
                sha.TransformFinalBlock(data,0,0);metadata["sensorSha256"]=Hex(sha.Hash);
            }
            metadata["sensorSamples"]=(long)w*h;metadata["distinctSensorCodes"]=count;metadata["minSensorCode"]=min;metadata["maxSensorCode"]=max;
        }
        private static void WritePixels(IntPtr image,int stride,int left,int top,int sourceWidth,int sourceHeight,int width,int height,double gain,Stream output)
        {
            var table=new ushort[65536];
            for(int i=0;i<table.Length;i++)
            {
                double x=Math.Min(1,i*gain/(8*65535.0));
                table[i]=(ushort)Math.Round(65535*(x<=.0031308?12.92*x:1.055*Math.Pow(x,1/2.4)-.055));
            }
            IntPtr pixels=IntPtr.Add(image,checked(16+(top*stride+left)*6));
            var row=new short[sourceWidth*3];var packed=new ushort[width*4];var bytes=new byte[width*8];
            if(width==sourceWidth&&height==sourceHeight)
            {
                for(int y=0;y<height;y++)
                {
                    Marshal.Copy(IntPtr.Add(pixels,checked(y*stride*6)),row,0,row.Length);
                    for(int x=0;x<width;x++)
                    {
                        int src=x*3,dst=x*4;packed[dst]=table[(ushort)row[src]];packed[dst+1]=table[(ushort)row[src+1]];
                        packed[dst+2]=table[(ushort)row[src+2]];packed[dst+3]=65535;
                    }
                    Buffer.BlockCopy(packed,0,bytes,0,bytes.Length);output.Write(bytes,0,bytes.Length);
                }
                return;
            }
            var sums=new long[width*3];var bounds=new int[width+1];
            for(int x=0;x<=width;x++)bounds[x]=(int)((long)x*sourceWidth/width);
            for(int y=0;y<height;y++)
            {
                int first=(int)((long)y*sourceHeight/height),end=(int)((long)(y+1)*sourceHeight/height);
                Array.Clear(sums,0,sums.Length);
                for(int sy=first;sy<end;sy++)
                {
                    Marshal.Copy(IntPtr.Add(pixels,checked(sy*stride*6)),row,0,row.Length);
                    for(int x=0;x<width;x++)
                    {
                        int j=x*3;
                        for(int i=bounds[x]*3;i<bounds[x+1]*3;i+=3)
                        {sums[j]+=(ushort)row[i];sums[j+1]+=(ushort)row[i+1];sums[j+2]+=(ushort)row[i+2];}
                    }
                }
                for(int x=0;x<width;x++)
                {
                    long n=(long)(end-first)*(bounds[x+1]-bounds[x]);int j=x*4,k=x*3;
                    packed[j]=table[(int)((sums[k]+n/2)/n)];packed[j+1]=table[(int)((sums[k+1]+n/2)/n)];
                    packed[j+2]=table[(int)((sums[k+2]+n/2)/n)];packed[j+3]=65535;
                }
                Buffer.BlockCopy(packed,0,bytes,0,bytes.Length);output.Write(bytes,0,bytes.Length);
            }
        }
        private static void Run(string command,string path,int edge,int selected,Stream output)
        {
            string ext=Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
            long length=new FileInfo(path).Length;
            if(length<1024||length>Int32.MaxValue)throw new NotSupportedException("相机 RAW 文件大小超出可处理范围。");
            using(var file=File.OpenRead(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,Dll)))
            using(var sha=SHA256.Create())if(Hex(sha.ComputeHash(file))!=DllDigest)throw new InvalidDataException("LibRaw 校验失败。");
            if(!Environment.Is64BitProcess||libraw_versionNumber()!=0x001603)throw new InvalidDataException("需要已验证的 LibRaw 0.22.3 x64。");
            IntPtr p=libraw_init(0),image=IntPtr.Zero;
            if(p==IntPtr.Zero)throw new OutOfMemoryException();
            try
            {
                Marshal.WriteInt32(p,UnpackParams,0);Marshal.WriteInt32(p,UnpackParams+4,0);Marshal.WriteInt32(p,UnpackParams+20,768);
                Marshal.WriteInt32(p,UnpackParams+12,selected);
                Check(libraw_open_wfile(p,path),"识别相机 RAW");
                var m=Metadata(p,ext,selected);Container(path,ext,m);BlackPattern(p,m);
                CameraRawModes.Verify(m);
                if(command=="poster")
                {
                    int count=0;
                    if(selected==0 && (bool)m["cameraModeVerified"] && (int)m["orientation"]==1 && libraw_unpack_thumb(p)==0)
                    {
                        int error;image=libraw_dcraw_make_mem_thumb(p,out error);
                        if(error==0&&image!=IntPtr.Zero&&Int(image,0)==1)
                        {
                            int bytes=Int(image,12);
                            if(bytes>=64&&bytes<=8*1024*1024)count=bytes;
                        }
                    }
                    m["posterBytes"]=count;Header(output,m);
                    if(count>0){var jpg=new byte[count];Marshal.Copy(IntPtr.Add(image,16),jpg,0,count);output.Write(jpg,0,count);}
                    return;
                }
                if(command=="inspect"&&(bool)m["cameraModeVerified"]){Header(output,m);return;}
                var sensorClock=System.Diagnostics.Stopwatch.StartNew();
                Check(libraw_unpack(p),"读取完整感光数据");Plane(p);
                m["sensorPlaneVerified"]=true;
                m["blackLevel"]=Int(p,Color+147488);m["whiteLevel"]=libraw_get_color_maximum(p);m["levelStage"]="after sensor unpack";
                var updatedBlack=new int[4];for(int i=0;i<4;i++)updatedBlack[i]=Int(p,Color+131072+i*4);m["channelBlackLevel"]=updatedBlack;
                BlackPattern(p,m);
                if(command=="inspect"){Header(output,m);return;}
                if(command=="measure"){Measure(p,m);Header(output,m);return;}
                // Use the as-shot WB from the file, preserve its unclipped channel scale and leave rotation to the common renderer.
                Marshal.WriteInt32(p,Params+152,(string)m["whiteBalanceSource"]=="as-shot"?1:0);Marshal.WriteInt32(p,Params+212,0);
                libraw_set_demosaic(p,3);libraw_set_highlight(p,1);libraw_set_output_color(p,1);libraw_set_no_auto_bright(p,1);
                libraw_set_adjust_maximum_thr(p,0);libraw_set_output_bps(p,16);libraw_set_gamma(p,0,1);libraw_set_gamma(p,1,1);
                Check(libraw_dcraw_process(p),"去马赛克与色彩转换");
                double min=1;
                for(int i=0;i<(int)m["rawColors"];i++)
                {
                    double multiplier=libraw_get_pre_mul(p,i);
                    if(!(multiplier>0&&multiplier<=1.0001))throw new InvalidDataException("白平衡归一化超出范围。");
                    min=Math.Min(min,multiplier);
                }
                double gain=(ext=="DNG"?Math.Pow(2,(double)m["baselineExposure"]):1)/min;
                if(gain>8.0001)throw new NotSupportedException("照片白平衡需要超过三档高光余量。");
                int code;image=libraw_dcraw_make_mem_image(p,out code);Check(code,"生成整幅 16 位图像");
                if(image==IntPtr.Zero||Int(image,0)!=2||Short(image,8)!=3||Short(image,10)!=16)
                    throw new InvalidDataException("解码器未返回三通道 16 位位图。");
                int w=Short(image,6),h=Short(image,4);Dimensions(w,h);
                if(w!=(int)m["activeWidth"]||h!=(int)m["activeHeight"]||Int(image,12)!=(long)w*h*6)
                    throw new InvalidDataException("整幅输出与有效感光尺寸不一致。");
                libraw_close(p);p=IntPtr.Zero;
                int cw=(int)m["storedWidth"],ch=(int)m["storedHeight"];
                int left=ext=="DNG"?(int)m["insetLeft"]-(int)m["leftMargin"]:0,top=ext=="DNG"?(int)m["insetTop"]-(int)m["topMargin"]:0;
                double scale=edge==0?1:Math.Min(1,(double)edge/Math.Max(cw,ch));
                int ow=Math.Max(1,(int)Math.Round(scale*cw,MidpointRounding.AwayFromZero));
                int oh=Math.Max(1,(int)Math.Round(scale*ch,MidpointRounding.AwayFromZero));
                m["outputWidth"]=ow;m["outputHeight"]=oh;m["asShotDisplayGain"]=gain;m["sensorDevelopMs"]=sensorClock.ElapsedMilliseconds;
                bool detail=command=="previewDetail"&&(long)cw*ch>=24000000&&(long)cw*ch<=60000000&&ow<cw;
                if(command=="previewDetail")m["detailFollows"]=detail;
                Header(output,m);WritePixels(image,w,left,top,cw,ch,ow,oh,gain,output);
                if(detail){
                    output.Flush();
                    WritePixels(image,w,left,top,cw,ch,cw,ch,gain,output);
                }
            }
            finally{if(image!=IntPtr.Zero)libraw_dcraw_clear_mem(image);if(p!=IntPtr.Zero)libraw_close(p);}
        }
        [STAThread] private static int Main(string[] args)
        {
            try
            {
                int edge,selected=0;
                if((args.Length!=2&&args.Length!=3)||!Int32.TryParse(args[1],out edge)||edge<0||edge>65535||
                    (args.Length==3&&(!Int32.TryParse(args[2],out selected)||selected<0||selected>1))||
                    !(args[0]=="inspect"||args[0]=="measure"||args[0]=="render"||args[0]=="previewDetail"||args[0]=="poster"))return 2;
                // Framework Process.StandardInput uses the host's encoding, which can include
                // a UTF-8 or UTF-16 preamble even though the path payload is ASCII Base64.
                string input;
                using(var reader=new StreamReader(Console.OpenStandardInput(),Encoding.ASCII,true))input=reader.ReadLine();
                if(input==null||input.Length>44000)throw new InvalidDataException("相机文件路径无效。");
                string path=Encoding.UTF8.GetString(Convert.FromBase64String(input));
                if(!Path.IsPathRooted(path))throw new InvalidDataException("请选择绝对路径的照片文件。");
                using(var output=new BufferedStream(Console.OpenStandardOutput(),131072)){Run(args[0],path,edge,selected,output);output.Flush();}
                return 0;
            }
            catch(Exception error){Console.Error.WriteLine(Convert.ToBase64String(Encoding.UTF8.GetBytes(error.Message)));return 1;}
        }
    }
}
#endif
