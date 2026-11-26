using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AIPhotoStudio
{
    internal sealed class ExportMeasure
    {
        public int width, height, quality;
        public long bytes;
    }

    internal sealed class ExportSample
    {
        public int width,height,x,y;
        public string before,after;
    }

    public static partial class Imaging
    {
        // WIC can seek to backfill an earlier header. Measure the final length, including the ICC.
        private sealed class EncodedLength : Stream
        {
            private long position,length;
            public override bool CanRead { get { return false; } }
            public override bool CanWrite { get { return true; } }
            public override bool CanSeek { get { return true; } }
            public override long Length { get { return length; } }
            public override long Position { get { return position; } set { if(value<0)throw new IOException();position=value; } }
            public override long Seek(long offset,SeekOrigin origin)
            {
                Position=checked(offset+(origin==SeekOrigin.End?length:origin==SeekOrigin.Current?position:0));return position;
            }
            public override void SetLength(long value){if(value<0)throw new IOException();length=value;}
            public override void Write(byte[] buffer,int offset,int count){position=checked(position+count);length=Math.Max(length,position);}
            public override int Read(byte[] buffer,int offset,int count){throw new NotSupportedException();}
            public override void Flush(){}
        }

        private static BitmapFrame PrepareExportFrame(BitmapSource original,int edge,DeliveryMark mark,string colorSpace,
            ColorContext profile,bool jpeg,string matte,CancellationToken cancel)
        {
            int w,h;TargetSize(original.PixelWidth,original.PixelHeight,edge,out w,out h);
            var result=SizeOutput(original,w,h);
            if(mark!=null)result=StampOutput(result,mark,cancel);
            if(jpeg)result=CompositeMatte(result,matte,cancel);
            if(colorSpace!="srgb")result=ConvertOutput(result,profile,jpeg,cancel);
            return BitmapFrame.Create(result,null,null,new ReadOnlyCollection<ColorContext>(new[]{profile}));
        }

        private static void SaveFrame(BitmapEncoder encoder,BitmapFrame frame,Stream target,CancellationToken cancel)
        {
            cancel.ThrowIfCancellationRequested();
            encoder.Frames.Add(frame);
            try{encoder.Save(cancel.CanBeCanceled?(Stream)new ExportStream(target,cancel):target);}
            catch{cancel.ThrowIfCancellationRequested();throw;}
        }

        private static long JpegLength(BitmapFrame frame,int quality,CancellationToken cancel)
        {
            using(var counter=new EncodedLength()){
                SaveFrame(new JpegBitmapEncoder{QualityLevel=quality},frame,counter,cancel);
                return counter.Length;
            }
        }

        private static string SampleData(BitmapFrame frame,BitmapEncoder encoder,string mime,CancellationToken cancel)
        {
            using(var bytes=new MemoryStream()){
                SaveFrame(encoder,frame,bytes,cancel);
                return "data:image/"+mime+";base64,"+Convert.ToBase64String(bytes.GetBuffer(),0,(int)bytes.Length);
            }
        }

        internal sealed class JpegProof
        {
            internal BitmapSource Before;
            internal byte[] Encoded;
            internal string Overview;
            internal ExportSample Sample(double centerX,double centerY,CancellationToken cancel)
            {
                int w=Math.Min(144,Before.PixelWidth),h=Math.Min(80,Before.PixelHeight);
                int x=Math.Max(0,Math.Min(Before.PixelWidth-w,(int)Math.Round(centerX*(Before.PixelWidth-1))-w/2));
                int y=Math.Max(0,Math.Min(Before.PixelHeight-h,(int)Math.Round(centerY*(Before.PixelHeight-1))-h/2));
                var region=new System.Windows.Int32Rect(x,y,w,h);
                using(var stream=new MemoryStream(Encoded,false)){
                    var decoded=BitmapFrame.Create(stream,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnDemand);
                    return new ExportSample{width=w,height=h,x=x,y=y,
                        before=SampleData(BitmapFrame.Create(new CroppedBitmap(Before,region)),new PngBitmapEncoder(),"png",cancel),
                        after=SampleData(BitmapFrame.Create(new CroppedBitmap(decoded,region)),new PngBitmapEncoder(),"png",cancel)};
                }
            }
        }

        internal static BitmapSource ShareCanvas(string path,IDictionary<string,double> values,StudioBridge.ExportSettings settings,CancellationToken cancel)
        {
            var image=Render(path,values,0,cancel);
            return settings.Canvas==null?image:settings.Canvas.Apply(image,cancel);
        }

        internal static JpegProof PrepareProof(string path,IDictionary<string,double> values,StudioBridge.ExportSettings settings,CancellationToken cancel)
        {
            var original=ShareCanvas(path,values,settings,cancel);
            var frame=PrepareExportFrame(original,settings.Canvas==null?settings.LongEdge:0,null,"srgb",OutputProfile("srgb"),true,settings.Matte,cancel);
            using(var stream=new MemoryStream()){
                SaveFrame(new JpegBitmapEncoder{QualityLevel=settings.Quality},frame,stream,cancel);
                if(settings.MeasuredBytes>0 && stream.Length!=settings.MeasuredBytes)throw new IOException("试算已改变，请重新计算。");
                int w,h;TargetSize(frame.PixelWidth,frame.PixelHeight,320,out w,out h);
                var preview=BitmapFrame.Create(AsFormat(SizeOutput(frame,w,h),PixelFormats.Rgb24));
                frame.Freeze();
                return new JpegProof{Before=frame,Encoded=stream.ToArray(),Overview=SampleData(preview,new PngBitmapEncoder(),"png",cancel)};
            }
        }

        private static ExportMeasure FitQuality(BitmapFrame frame,int highest,int budget,CancellationToken cancel,out long fullBytes)
        {
            fullBytes=JpegLength(frame,highest,cancel);
            long bytes=fullBytes;int quality=highest;
            if(fullBytes>budget){
                if(highest==1)return null;
                bytes=JpegLength(frame,1,cancel);
                if(bytes>budget)return null;
                quality=1;int high=highest;
                while(high-quality>1){
                    int test=(quality+high)/2;long length=JpegLength(frame,test,cancel);
                    if(length<=budget){quality=test;bytes=length;}else high=test;
                }
            }
            return new ExportMeasure{width=frame.PixelWidth,height=frame.PixelHeight,quality=quality,bytes=bytes};
        }

        private static List<ExportMeasure> FitJpeg(BitmapSource original,int edge,int quality,int budget,int minEdge,bool alternatives,
            DeliveryMark mark,string colorSpace,ColorContext profile,string matte,CancellationToken cancel,out BitmapFrame selected)
        {
            var choices=new List<ExportMeasure>();
            int longest=Math.Max(original.PixelWidth,original.PixelHeight);
            int current=edge==0?longest:Math.Min(longest,edge);
            int requested=current,floor=minEdge==0?current:Math.Min(current,minEdge);
            selected=null;
            while(true){
                var frame=PrepareExportFrame(original,current,mark,colorSpace,profile,true,matte,cancel);
                long fullBytes;var choice=FitQuality(frame,quality,budget,cancel,out fullBytes);
                if(choice!=null){
                    choices.Add(choice);
                    if(!alternatives){selected=frame;break;}
                    if(choices.Count>=3 || choice.quality>=Math.Min(65,quality) && (current==requested || choices.Count>=2))break;
                }
                if(current<=floor)break;
                // Image complexity and JPEG headers vary. Each proposed size is actually encoded.
                double factor=Math.Min(.80,Math.Max(.25,.94*Math.Sqrt((double)budget/fullBytes)));
                current=Math.Max(floor,Math.Min(current-1,(int)Math.Floor(current*factor)));
            }
            return choices;
        }

        internal static List<ExportMeasure> JpegOptions(string path,IDictionary<string,double> values,StudioBridge.ExportSettings settings,CancellationToken cancel)
        {
            if(settings.Format!="jpeg" || settings.MaxBytes<=0)throw new ArgumentException("请设置 JPEG 体积上限。");
            var original=ShareCanvas(path,values,settings,cancel);BitmapFrame ignored;
            bool exact=settings.Canvas!=null;
            return FitJpeg(original,exact?0:settings.LongEdge,settings.Quality,settings.MaxBytes,exact?0:1,!exact,null,"srgb",OutputProfile("srgb"),settings.Matte,cancel,out ignored);
        }
    }
}
