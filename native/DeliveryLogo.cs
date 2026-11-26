using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AIPhotoStudio
{
    internal sealed class DeliveryLogoRef
    {
        public string name,hash;
        public int width,height;

        internal static string ValidHash(string hash)
        {
            if(hash==null || hash.Length!=64 || hash.Any(c=>!(c>='0'&&c<='9' || c>='a'&&c<='f')))throw new ArgumentException("PNG 标志的内容编号无效。");
            return hash;
        }
        internal static DeliveryLogoRef Read(Dictionary<string,object> f)
        {
            var logo=new DeliveryLogoRef{name=StudioBridge.TextOf(f,"name"),hash=ValidHash(StudioBridge.TextOf(f,"hash")),
                width=DeliveryPreset.Integer(f,"width",1,DeliveryLogo.MaxEdge),height=DeliveryPreset.Integer(f,"height",1,DeliveryLogo.MaxEdge)};
            if(String.IsNullOrWhiteSpace(logo.name)||logo.name.Length>255||logo.name.IndexOfAny(Path.GetInvalidFileNameChars())>=0||logo.name.Any(Char.IsControl))
                throw new ArgumentException("PNG 标志名称无效。");
            return logo;
        }
    }

    internal sealed class DeliveryLogo
    {
        internal const int MaxBytes=8*1024*1024,MaxEdge=4096,MaxPixels=4*1024*1024;
        internal readonly byte[] Bytes;
        internal readonly DeliveryLogoRef Info;
        internal readonly string Thumbnail;

        private DeliveryLogo(byte[] bytes,string name,CancellationToken cancel)
        {
            Bytes=bytes;
            var pixels=Imaging.DecodeDeliveryLogo(bytes,cancel);
            Info=new DeliveryLogoRef{name=name,hash=Digest(bytes),width=pixels.PixelWidth,height=pixels.PixelHeight};
            Thumbnail=Imaging.DeliveryLogoThumbnail(pixels);
        }

        internal static DeliveryLogo Import(string path,CancellationToken cancel)
        {
            if(!String.Equals(Path.GetExtension(path),".png",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("请选择 PNG 标志文件。");
            byte[] bytes;
            using(var file=WorkspacePaths.OpenRead(WorkspacePaths.Full(path),FileShare.Read))
            {
                if(file.Length>MaxBytes || file.Length<45)throw new InvalidDataException("PNG 标志须为有效图像，文件上限为 8 MiB。");
                bytes=new byte[(int)file.Length];int read=0;
                while(read<bytes.Length){cancel.ThrowIfCancellationRequested();int n=file.Read(bytes,read,bytes.Length-read);if(n==0)throw new EndOfStreamException();read+=n;}
            }
            return new DeliveryLogo(ImageChunks(bytes),Path.GetFileName(path),cancel);
        }

        internal static string Digest(byte[] bytes){using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","").ToLowerInvariant();}
        private static long Big(byte[] v,int i){return ((long)v[i]<<24)|((long)v[i+1]<<16)|((long)v[i+2]<<8)|v[i+3];}

        // Only pass image, transparency, color and EXIF orientation chunks to WIC. Compressed text is never expanded.
        private static byte[] ImageChunks(byte[] src)
        {
            if(src.Length<45 || Big(src,0)!=0x89504e47 || Big(src,4)!=0x0d0a1a0a || Big(src,8)!=13 || Encoding.ASCII.GetString(src,12,4)!="IHDR")
                throw new InvalidDataException("标志文件没有有效的 PNG 文件头。");
            long w=Big(src,16),h=Big(src,20);
            if(w<1||h<1||w>MaxEdge||h>MaxEdge||w*h>MaxPixels)throw new InvalidDataException("PNG 标志长边上限为 4096 像素，总像素上限为 4,194,304。");
            using(var output=new MemoryStream())
            {
                output.Write(src,0,8);bool foundIcc=false;
                for(int at=8,n=0;at<=src.Length-12;n++)
                {
                    long length=Big(src,at);
                    if(n>65536||length>src.Length-at-12)throw new InvalidDataException("PNG 标志的数据块无效。");
                    int size=(int)length;string name=Encoding.ASCII.GetString(src,at+4,4);
                    if(name=="acTL"||name=="fcTL"||name=="fdAT")throw new InvalidDataException("图形水印支持单帧 PNG，请先导出静态标志。");
                    if(name=="IHDR" && at!=8)throw new InvalidDataException("PNG 标志有重复的文件头。");
                    bool keep=name=="IHDR"||name=="PLTE"||name=="tRNS"||name=="IDAT"||name=="IEND"||name=="iCCP"||name=="sRGB"||name=="gAMA"||name=="cHRM"||name=="eXIf";
                    if(!keep && (src[at+4]&32)==0)throw new InvalidDataException("PNG 标志包含无法处理的关键数据块。");
                    if(name=="iCCP"){if(foundIcc)throw new InvalidDataException("PNG 标志有重复的 ICC 配置。");foundIcc=true;CheckIcc(src,at+8,size);}
                    if(name=="eXIf"&&size>128*1024)throw new InvalidDataException("PNG 标志的方向元数据过大。");
                    if(keep)output.Write(src,at,size+12);
                    at+=size+12;
                    if(name=="IEND" && size==0 && at==src.Length)return output.ToArray();
                }
                throw new InvalidDataException("PNG 标志没有完整的结束数据块。");
            }
        }

        private static void CheckIcc(byte[] data,int start,int size)
        {
            int end=start+size,zero=Array.IndexOf(data,(byte)0,start,Math.Min(size,80));
            if(zero<=start || zero+8>=end || data[zero+1]!=0)throw new InvalidDataException("PNG 标志的 ICC 配置无效。");
            // PNG uses a zlib envelope around the DEFLATE stream. Bound the expanded profile before passing it to WIC.
            using(var source=new MemoryStream(data,zero+4,end-(zero+4)-4,false))
            using(var inflater=new DeflateStream(source,CompressionMode.Decompress))
            {
                var buffer=new byte[8192];int total=0,count;
                while((count=inflater.Read(buffer,0,buffer.Length))>0){total+=count;if(total>4*1024*1024)throw new InvalidDataException("PNG 标志的 ICC 配置超过 4 MiB。");}
                if(total<128)throw new InvalidDataException("PNG 标志的 ICC 配置不完整。");
            }
        }
    }

    public static partial class Imaging
    {
        internal static BitmapSource DecodeDeliveryLogo(byte[] bytes,CancellationToken cancel)
        {
            using(var stream=new MemoryStream(bytes,false))
            {
                var input=ImagingInput.Read(stream,"PNG");int w=input.Frame.PixelWidth,h=input.Frame.PixelHeight;
                if(w>DeliveryLogo.MaxEdge||h>DeliveryLogo.MaxEdge||(long)w*h>DeliveryLogo.MaxPixels)throw new InvalidDataException("PNG 标志的解码尺寸过大。");
                var reader=new SurfaceReader(input,w,h);var image=new WriteableBitmap(w,h,96,96,PixelFormats.Rgba64,null);
                int rows=Math.Min(32,h);var rgba=new ushort[w*rows*4];bool visible=false;
                for(int y=0;y<h;y+=rows){
                    cancel.ThrowIfCancellationRequested();var rect=new Int32Rect(0,y,w,Math.Min(rows,h-y));reader.Read(rect,rgba);
                    if(!visible)for(int i=3;i<w*rect.Height*4;i+=4)if(rgba[i]!=0){visible=true;break;}
                    image.WritePixels(rect,rgba,w*8,0);
                }
                if(!visible)throw new InvalidDataException("PNG 标志全部透明，请选择包含可见像素的图像。");
                image.Freeze();return Oriented(image,input.Orientation);
            }
        }

        internal static string DeliveryLogoThumbnail(BitmapSource logo)
        {
            int w,h;TargetSize(logo.PixelWidth,logo.PixelHeight,128,out w,out h);
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(AsFormat(SizeOutput(logo,w,h),PixelFormats.Bgra32)));
            using(var stream=new MemoryStream()){encoder.Save(stream);return "data:image/png;base64,"+Convert.ToBase64String(stream.GetBuffer(),0,(int)stream.Length);}
        }
    }
}
