using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AIPhotoStudio
{
    internal sealed class FrameGeometry
    {
        internal const string Group = "构图";
        internal static readonly string[] Keys = new[]{ "frame_turn", "frame_mirror", "frame_angle", "frame_ratio", "frame_trim_x", "frame_trim_y", "frame_pan_x", "frame_pan_y" }.Concat(FramePerspective.Keys).ToArray();
        private static readonly int[] RatioW = { 0, 16, 9, 4, 1 };
        private static readonly int[] RatioH = { 0, 9, 16, 5, 1 };
        internal int Width, Height, SourceWidth, SourceHeight, Turn;
        internal double Cos, Sin, CenterX, CenterY;
        internal bool Orthogonal;
        private bool mirror;
        private FramePerspective perspective;

        internal static bool IsKey(string key) { return Array.IndexOf(Keys,key)>=0; }
        internal static bool IsPerspectiveKey(string key) { return Array.IndexOf(FramePerspective.Keys,key)>=0; }
        internal static bool HasPerspective(IDictionary<string,double> values) { return FramePerspective.Active(values); }
        internal static void CheckPerspective(IDictionary<string,double> values)
        {
            if(HasPerspective(values))new FramePerspective(1,1,values);
        }
        private static double Get(IDictionary<string,double> values,string key)
        {
            double value;
            return values!=null && values.TryGetValue(key,out value) ? value : 0;
        }
        internal static bool Active(IDictionary<string,double> values)
        {
            foreach(string key in Keys) if(Get(values,key)!=0)return true;
            return false;
        }
        internal static Dictionary<string,double> SourceValues(IDictionary<string,double> values)
        {
            var result=values==null?new Dictionary<string,double>():new Dictionary<string,double>(values);
            foreach(string key in Keys)result.Remove(key);
            return result;
        }
        internal static Dictionary<string,double> NeutralValues(IDictionary<string,double> values,bool framed)
        {
            var result=CameraRawModes.Neutral(values);
            if(framed)foreach(string key in Keys)if(Get(values,key)!=0)result[key]=Get(values,key);
            return result;
        }

        // Coordinates measure edges from the upper left of the EXIF-oriented source.
        // Exact quadrants avoid trig roundoff during integer crops and quarter turns.
        internal static FrameGeometry Resolve(int w,int h,IDictionary<string,double> values)
        {
            int sourceW=w,sourceH=h;
            var page=HasPerspective(values)?new FramePerspective(w,h,values):null;
            if(page!=null){w=page.Width;h=page.Height;}
            double turnValue=Get(values,"frame_turn"),ratioValue=Get(values,"frame_ratio");
            if(!(turnValue>=0&&turnValue<=3&&turnValue==Math.Truncate(turnValue)) ||
                !(ratioValue>=0&&ratioValue<RatioW.Length&&ratioValue==Math.Truncate(ratioValue)))
                throw new ArgumentException("旋转方向或裁切比例编号无效。");
            int turn=(int)turnValue,ratio=(int)ratioValue;
            double angle=Get(values,"frame_angle"), rad=angle*Math.PI/180;
            double u=Math.Cos(rad),v=Math.Sin(rad);
            double c=new[]{u,-v,-u,v}[turn], s=new[]{v,u,-v,-u}[turn];
            double ac=Math.Abs(c),ass=Math.Abs(s);
            double rw=ratio==0?(turn%2==0?w:h):RatioW[ratio];
            double rh=ratio==0?(turn%2==0?h:w):RatioH[ratio];
            double fit=Math.Min(w/(ac*rw+ass*rh),h/(ass*rw+ac*rh));
            double sx=1-Get(values,"frame_trim_x")/100, sy=ratio==0?1-Get(values,"frame_trim_y")/100:sx;
            int ow,oh;
            if(ratio>0 && fit*sx>=1){
                int units=(int)Math.Floor(fit*sx+1e-9);
                ow=units*RatioW[ratio];oh=units*RatioH[ratio];
            }else{
                ow=Math.Max(1,(int)Math.Floor(rw*fit*sx+1e-9));
                oh=Math.Max(1,(int)Math.Floor(rh*fit*sy+1e-9));
            }
            double bw=ac*ow+ass*oh,bh=ass*ow+ac*oh;
            double left=Math.Max(0,w-bw)*(Get(values,"frame_pan_x")+100)/200;
            double top=Math.Max(0,h-bh)*(Get(values,"frame_pan_y")+100)/200;
            if(angle==0){left=Math.Floor(left+.5);top=Math.Floor(top+.5);}
            return new FrameGeometry{SourceWidth=sourceW,SourceHeight=sourceH,perspective=page,Width=ow,Height=oh,Turn=turn,
                Cos=c,Sin=s,Orthogonal=angle==0,mirror=Get(values,"frame_mirror")==1,CenterX=left+bw/2,CenterY=top+bh/2};
        }

        // Only active compositions need a full-resolution measurement before selecting
        // a decode size. Reuse a bounded, file-identity checked result while editing.
        private sealed class Measurement { internal string Path; internal long Size,Write,Created; internal int W,H; }
        private static readonly List<Measurement> Sizes=new List<Measurement>();
        internal static FrameGeometry ForFile(string path,IDictionary<string,double> values)
        {
            var file=new FileInfo(ImagingInput.FullPath(path));
            var request=new Measurement{Path=file.FullName,Size=file.Length,Write=file.LastWriteTimeUtc.Ticks,Created=file.CreationTimeUtc.Ticks};
            lock(Sizes){
                var hit=Sizes.Find(e=>String.Equals(e.Path,request.Path,StringComparison.OrdinalIgnoreCase)&&e.Size==request.Size&&e.Write==request.Write&&e.Created==request.Created);
                if(hit!=null)return Resolve(hit.W,hit.H,values);
            }
            var meta=Imaging.Inspect(file.FullName);
            request.W=Convert.ToInt32(meta["width"]);request.H=Convert.ToInt32(meta["height"]);
            lock(Sizes){Sizes.Insert(0,request);if(Sizes.Count>8)Sizes.RemoveAt(Sizes.Count-1);}
            return Resolve(request.W,request.H,values);
        }

        internal int DecodeEdge(int outputEdge)
        {
            if(outputEdge==0)return 0;
            double scale=Math.Min(1,(double)outputEdge/Math.Max(Width,Height));
            return Math.Min(65535,Math.Max(1,(int)Math.Ceiling(Math.Max(SourceWidth,SourceHeight)*scale)));
        }
        internal BitmapSource Apply(BitmapSource input,int maxEdge,CancellationToken token)
        {
            var result=ApplyCrop(input,maxEdge,token);
            return mirror?Imaging.HorizontalMirror(result):result;
        }
        private BitmapSource ApplyCrop(BitmapSource input,int maxEdge,CancellationToken token)
        {
            int ow,oh;Imaging.TargetSize(Width,Height,maxEdge,out ow,out oh);
            if(perspective!=null)return perspective.Sample(this,input.PixelWidth,input.PixelHeight,ow,oh,new Int32Rect(0,0,ow,oh),input,null,token);
            if(Orthogonal){
                double bw=Turn%2==0?Width:Height,bh=Turn%2==0?Height:Width;
                double sx=(double)input.PixelWidth/SourceWidth,sy=(double)input.PixelHeight/SourceHeight;
                int left=Math.Min(input.PixelWidth-1,(int)Math.Floor((CenterX-bw/2)*sx+.5));
                int top=Math.Min(input.PixelHeight-1,(int)Math.Floor((CenterY-bh/2)*sy+.5));
                int end=Math.Min(input.PixelWidth,Math.Max(left+1,(int)Math.Floor((CenterX+bw/2)*sx+.5)));
                int bottom=Math.Min(input.PixelHeight,Math.Max(top+1,(int)Math.Floor((CenterY+bh/2)*sy+.5)));
                var crop=new CroppedBitmap(input,new Int32Rect(left,top,end-left,bottom-top));crop.Freeze();
                return Imaging.SizeOutput(Imaging.QuarterTurn(crop,Turn),ow,oh);
            }
            return Resample(input.PixelWidth,input.PixelHeight,ow,oh,new Int32Rect(0,0,ow,oh),
                delegate(Int32Rect r){return input;},token);
        }

        internal BitmapSource ReadViewport(Int32Rect requested,Func<Int32Rect,BitmapSource> read,CancellationToken token)
        {
            Imaging.CheckViewport(requested,Width,Height);
            if(mirror)requested.X=Width-requested.X-requested.Width;
            var result=ReadCropViewport(requested,read,token);
            return mirror?Imaging.HorizontalMirror(result):result;
        }
        private BitmapSource ReadCropViewport(Int32Rect requested,Func<Int32Rect,BitmapSource> read,CancellationToken token)
        {
            if(perspective!=null)return perspective.Sample(this,SourceWidth,SourceHeight,Width,Height,requested,null,read,token);
            if(!Orthogonal)return Resample(SourceWidth,SourceHeight,Width,Height,requested,read,token);
            int w=Turn%2==0?Width:Height,h=Turn%2==0?Height:Width;
            var area=Imaging.StoredRect(requested,w,h,new[]{1,6,3,8}[Turn]);
            area.X+=(int)Math.Floor(CenterX-w/2.0+.5);
            area.Y+=(int)Math.Floor(CenterY-h/2.0+.5);
            return Imaging.QuarterTurn(read(area),Turn);
        }

        private BitmapSource Resample(int iw,int ih,int ow,int oh,Int32Rect requested,Func<Int32Rect,BitmapSource> read,CancellationToken token)
        {
            // Keep the exact same block origins and floating-point operation order as full-size export.
            const int tile=96;
            int firstX=requested.X/tile*tile, firstY=requested.Y/tile*tile;
            int stopX=Math.Min(ow,(requested.X+requested.Width+tile-1)/tile*tile);
            int stopY=Math.Min(oh,(requested.Y+requested.Height+tile-1)/tile*tile);
            double scaleX=(double)iw/SourceWidth, scaleY=(double)ih/SourceHeight;
            double ax=Cos*Width/ow*scaleX,bx=Sin*Height/oh*scaleX,ay=-Sin*Width/ow*scaleY,by=Cos*Height/oh*scaleY;
            double x0=(CenterX-Cos*Width/2-Sin*Height/2)*scaleX+(ax+bx)/2-.5;
            double y0=(CenterY+Sin*Width/2-Cos*Height/2)*scaleY+(ay+by)/2-.5;
            var area=new Int32Rect(0,0,iw,ih);
            if(firstX!=0||firstY!=0||stopX!=ow||stopY!=oh)
            {
                double sx=x0+ax*firstX+bx*firstY,sy=y0+ay*firstX+by*firstY;
                double dx=stopX-firstX-1,dy=stopY-firstY-1;
                // Bilinear support plus one guard pixel for rounding at an exact integer boundary.
                int left=Bound((int)Math.Floor(sx+Math.Min(0,ax*dx)+Math.Min(0,bx*dy))-1,iw);
                int top=Bound((int)Math.Floor(sy+Math.Min(0,ay*dx)+Math.Min(0,by*dy))-1,ih);
                int right=Bound((int)Math.Floor(sx+Math.Max(0,ax*dx)+Math.Max(0,bx*dy))+2,iw);
                int bottom=Bound((int)Math.Floor(sy+Math.Max(0,ay*dx)+Math.Max(0,by*dy))+2,ih);
                area=new Int32Rect(left,top,right-left+1,bottom-top+1);
            }
            var input=read(area);
            var result=new WriteableBitmap(stopX-firstX,stopY-firstY,input.DpiX,input.DpiY,PixelFormats.Rgba64,null);
            var pixels=new ushort[tile*tile*4];var source=new ushort[0];
            for(int y=firstY;y<stopY;y+=tile)for(int x=firstX;x<stopX;x+=tile){
                token.ThrowIfCancellationRequested();
                int tw=Math.Min(tile,ow-x),th=Math.Min(tile,oh-y);
                double px=x0+ax*x+bx*y,py=y0+ay*x+by*y;
                int left=Bound((int)Math.Floor(px+Math.Min(0,ax*(tw-1))+Math.Min(0,bx*(th-1))),iw);
                int top=Bound((int)Math.Floor(py+Math.Min(0,ay*(tw-1))+Math.Min(0,by*(th-1))),ih);
                int end=Bound((int)Math.Floor(px+Math.Max(0,ax*(tw-1))+Math.Max(0,bx*(th-1)))+1,iw);
                int bottom=Bound((int)Math.Floor(py+Math.Max(0,ay*(tw-1))+Math.Max(0,by*(th-1)))+1,ih);
                int sw=end-left+1,sh=bottom-top+1,length=checked(sw*sh*4);
                if(source.Length<length)source=new ushort[length];
                input.CopyPixels(new Int32Rect(left-area.X,top-area.Y,sw,sh),source,sw*8,0);
                for(int j=0;j<th;j++)for(int i=0;i<tw;i++){
                    double rx=Math.Max(0,Math.Min(sw-1,px+ax*i+bx*j-left)), ry=Math.Max(0,Math.Min(sh-1,py+ay*i+by*j-top));
                    int ix=(int)rx,iy=(int)ry,a=(iy*sw+ix)*4,b=ix+1<sw?a+4:a;
                    int d=iy+1<sh?a+sw*4:a,e=iy+1<sh?b+sw*4:b;
                    double fx=rx-ix,fy=ry-iy;
                    double wa=(1-fx)*(1-fy)*source[a+3],wb=fx*(1-fy)*source[b+3],wd=(1-fx)*fy*source[d+3],we=fx*fy*source[e+3];
                    double alpha=wa+wb+wd+we;int o=(j*tw+i)*4;
                    pixels[o+3]=(ushort)(alpha+.5);
                    for(int k=0;k<3;k++)pixels[o+k]=alpha<=0?(ushort)0:(ushort)((wa*source[a+k]+wb*source[b+k]+wd*source[d+k]+we*source[e+k])/alpha+.5);
                }
                result.WritePixels(new Int32Rect(x-firstX,y-firstY,tw,th),pixels,tw*8,0);
            }
            result.Freeze();
            if(requested.Width==result.PixelWidth&&requested.Height==result.PixelHeight)return result;
            var exact=new CroppedBitmap(result,new Int32Rect(requested.X-firstX,requested.Y-firstY,requested.Width,requested.Height));
            exact.Freeze();return exact;
        }
        private static int Bound(int value,int size) { return Math.Max(0,Math.Min(size-1,value)); }
    }
    public static partial class Imaging
    {
        internal static BitmapSource QuarterTurn(BitmapSource input,int turn) { return Oriented(input,new[]{1,6,3,8}[turn]); }
        internal static BitmapSource HorizontalMirror(BitmapSource input) { return Oriented(input,2); }
        internal static BitmapSource SizeOutput(BitmapSource source,int width,int height)
        {
            if(source.PixelWidth==width&&source.PixelHeight==height)return source;
            var image=ScaledRgba(source,width,height,true);image.Freeze();return image;
        }
    }
}
