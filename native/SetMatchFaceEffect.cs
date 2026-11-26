using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AIPhotoStudio
{
    // This is a regular editable radial mask with erase strokes. Existing recipe readers,
    // lock checks, exports and the local mask editor all use the same coverage as the preview.
    internal sealed class SetMatchFaceEffect
    {
        public double x {get;set;} public double y {get;set;}
        public double rx {get;set;} public double ry {get;set;}
        public double[][] cuts {get;set;}
        internal SetMatchRegions.Area Footprint()
        {
            return new SetMatchRegions.Area{Role="skin",X=x,Y=y,Rx=rx,Ry=ry};
        }
        internal LocalEdits.GradientGeometry Geometry()
        {
            return new LocalEdits.GradientGeometry{x=x,y=y,dx=rx,dy=ry,feather=.38};
        }
        internal LocalEdits.Stroke[] Strokes()
        {
            return cuts.Select(c=>new LocalEdits.Stroke{kind="maskErase",radius=c[0],strength=1,
                points=new[]{new LocalEdits.Mark{x=c[1],y=c[2]},new LocalEdits.Mark{x=c[3],y=c[4]}}}).ToArray();
        }
        internal SetMatchFaceEffect ProtectOpposite(SetMatchRegions.Area selected,SetMatchRegions.Area other,int w,int h)
        {
            if(other==null||cuts.Length>=160)return null;
            double dx=(other.X-selected.X)*w,dy=(other.Y-selected.Y)*h,distance=Math.Sqrt(dx*dx+dy*dy);
            double edge=Math.Min(w,h),radius=Math.Min(.45*edge,1.35*distance);
            if(distance<3||radius<.005*edge)return null;
            dx/=distance;dy/=distance;
            // Place the soft edge across the line joining the two cheek patches, including on a tilted face.
            // The protected side uses a normal editable erase stroke on the original feature-safe radial mask.
            double cx=other.X*w+dx*(radius-.67*distance),cy=other.Y*h+dy*(radius-.67*distance);
            double span=1.3*Math.Sqrt(Math.Pow(rx*w,2)+Math.Pow(ry*h,2));
            Func<double,double> clamp=v=>Math.Max(0,Math.Min(1,v));
            var cut=new[]{radius/edge,clamp((cx-dy*span)/w),clamp((cy+dx*span)/h),
                clamp((cx+dy*span)/w),clamp((cy-dx*span)/h)};
            return new SetMatchFaceEffect{x=x,y=y,rx=rx,ry=ry,cuts=cuts.Concat(new[]{cut}).ToArray()};
        }
        internal static SetMatchFaceEffect Read(object raw,SetMatchRegions.Area cheek)
        {
            var fields=raw as IDictionary<string,object>;
            if(fields==null||fields.Count!=5||new[]{"x","y","rx","ry","cuts"}.Any(k=>!fields.ContainsKey(k)))
                throw new ArgumentException("面部调色范围不完整。");
            var area=StudioBridge.Json().ConvertToType<SetMatchFaceEffect>(raw);
            if(new[]{area.x,area.y,area.rx,area.ry}.Any(v=>Double.IsNaN(v)||Double.IsInfinity(v))||
                area.rx<.004||area.ry<.004||area.rx>.5||area.ry>.5||area.x-area.rx<0||area.x+area.rx>1||
                area.y-area.ry<0||area.y+area.ry>1||Math.Pow((cheek.X-area.x)/area.rx,2)+Math.Pow((cheek.Y-area.y)/area.ry,2)>1||
                area.cuts==null||area.cuts.Length>160||area.cuts.Any(c=>c==null||c.Length!=5||c.Any(v=>Double.IsNaN(v)||Double.IsInfinity(v))||c[0]<.005||c[0]>.45||
                    c.Skip(1).Any(v=>v<0||v>1)))throw new ArgumentException("面颊取样与保护范围不匹配。");
            return area;
        }
        internal string Overlay(int w,int h,Func<double,double,double> weight)
        {
            const int size=76;
            var bytes=new byte[size*size*4];
            for(int j=0;j<size;j++)for(int i=0;i<size;i++){
                double u=x+((i+.5)/size*2-1)*rx,v=y+((j+.5)/size*2-1)*ry;
                int at=(j*size+i)*4;
                bytes[at]=102;bytes[at+1]=166;bytes[at+2]=255;bytes[at+3]=(byte)Math.Round(140*weight(u*w-.5,v*h-.5));
            }
            var frame=BitmapSource.Create(size,size,96,96,PixelFormats.Bgra32,null,bytes,size*4);
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(frame));
            using(var output=new MemoryStream()){encoder.Save(output);return "data:image/png;base64,"+Convert.ToBase64String(output.ToArray());}
        }
        internal BitmapSource MarkedDetail(BitmapSource frame,LocalEdits.MaskLayer actual)
        {
            int w=frame.PixelWidth,h=frame.PixelHeight;
            int side=Math.Min(Math.Min(w,h),Math.Max(42,(int)Math.Ceiling(2.35*Math.Max(rx*w,ry*h))));
            var box=new System.Windows.Int32Rect(Math.Max(0,Math.Min(w-side,(int)Math.Round(x*w-side*.5))),
                Math.Max(0,Math.Min(h-side,(int)Math.Round(y*h-side*.5))),side,side);
            int stride=box.Width*4;
            var rgb=new FormatConvertedBitmap(frame,PixelFormats.Bgra32,null,0);
            var bytes=new byte[stride*box.Height];rgb.CopyPixels(box,bytes,stride,0);
            var weight=actual==null?null:LocalEdits.MatchCoverage(actual.geometry,actual.strokes,frame.PixelWidth,frame.PixelHeight);
            for(int j=0;j<box.Height;j++)for(int i=0;i<box.Width;i++){
                int at=j*stride+i*4;double a=weight==null?0:.58*weight(box.X+i,box.Y+j);
                bytes[at]=(byte)Math.Round(bytes[at]*(1-a)+102*a);bytes[at+1]=(byte)Math.Round(bytes[at+1]*(1-a)+166*a);
                bytes[at+2]=(byte)Math.Round(bytes[at+2]*(1-a)+255*a);
            }
            var result=BitmapSource.Create(box.Width,box.Height,96,96,PixelFormats.Bgra32,null,bytes,stride);result.Freeze();return result;
        }
    }

    public static partial class LocalEdits
    {
        internal static Func<double,double,double> MatchCoverage(GradientGeometry geometry,Stroke[] strokes,int width,int height)
        {
            var gradient=new GradientField("radial",geometry,width,height);
            var borders=strokes.Length==0?null:new Grid(width,height,strokes,CancellationToken.None,null,null,true);
            return (x,y)=>GradientWeight(gradient,borders,x,y);
        }
    }
}
