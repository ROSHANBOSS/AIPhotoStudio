using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AIPhotoStudio
{
    // Maps the rectified page's unit square back onto its four oriented source corners.
    internal sealed class FramePerspective
    {
        internal static readonly string[] Keys={"frame_doc_tlx","frame_doc_tly","frame_doc_trx","frame_doc_try",
            "frame_doc_brx","frame_doc_bry","frame_doc_blx","frame_doc_bly","frame_doc_ratio"};
        internal static readonly string[] Labels={"左上角水平内移","左上角垂直内移","右上角水平内移","右上角垂直内移",
            "右下角水平内移","右下角垂直内移","左下角水平内移","左下角垂直内移","纸面比例"};
        private static readonly double[] Ratios={0,210.0/297,297.0/210,1,4.0/3,3.0/4};
        internal readonly int Width,Height;
        private readonly double a,b,c,d,e,f,g,h;

        private static double Get(IDictionary<string,double> values,string key)
        {
            double value;
            return values!=null&&values.TryGetValue(key,out value)?value:0;
        }
        internal static bool Active(IDictionary<string,double> values)
        {
            foreach(string key in Keys)if(Get(values,key)!=0)return true;
            return false;
        }
        private static double Length(Point p,Point q,int w,int h)
        {
            double x=(p.X-q.X)*w,y=(p.Y-q.Y)*h;
            return Math.Sqrt(x*x+y*y);
        }
        internal FramePerspective(int width,int height,IDictionary<string,double> values)
        {
            var v=new double[8];
            for(int i=0;i<v.Length;i++){
                v[i]=Get(values,Keys[i])/100;
                if(!(v[i]>=0&&v[i]<=1))throw new ArgumentException("纸面角点超出照片。");
            }
            double ratio=Get(values,Keys[8]);
            if(!(ratio>=0&&ratio<Ratios.Length&&ratio==Math.Truncate(ratio)))throw new ArgumentException("纸面比例无效。");
            var p=new[]{new Point(v[0],v[1]),new Point(1-v[2],v[3]),new Point(1-v[4],1-v[5]),new Point(v[6],1-v[7])};
            for(int i=0;i<4;i++){
                Vector first=p[(i+1)%4]-p[i],next=p[(i+2)%4]-p[(i+1)%4];
                if(first.X*next.Y-first.Y*next.X<=.0001)throw new ArgumentException("四角需要按左上、右上、右下、左下围成清楚的凸四边形。");
            }
            double dx1=p[1].X-p[2].X,dy1=p[1].Y-p[2].Y,dx2=p[3].X-p[2].X,dy2=p[3].Y-p[2].Y;
            double dx3=p[0].X-p[1].X+p[2].X-p[3].X,dy3=p[0].Y-p[1].Y+p[2].Y-p[3].Y;
            double det=dx1*dy2-dx2*dy1;
            g=(dx3*dy2-dx2*dy3)/det;h=(dx1*dy3-dx3*dy1)/det;
            c=p[0].X; f=p[0].Y;
            a=(1+g)*p[1].X-c;b=(1+h)*p[3].X-c;
            d=(1+g)*p[1].Y-f;e=(1+h)*p[3].Y-f;
            double low=Math.Min(1,Math.Min(1+g,Math.Min(1+h,1+g+h)));
            double high=Math.Max(1,Math.Max(1+g,Math.Max(1+h,1+g+h)));
            if(low<high*.002)throw new ArgumentException("纸面过于侧斜，请重新选择四角。");
            double w=(Length(p[0],p[1],width,height)+Length(p[3],p[2],width,height))/2;
            double ht=(Length(p[0],p[3],width,height)+Length(p[1],p[2],width,height))/2;
            double target=Ratios[(int)ratio];
            if(target>0){w=Math.Min(w,ht*target);ht=w/target;}
            Width=Math.Max(1,(int)Math.Floor(w+.5));Height=Math.Max(1,(int)Math.Floor(ht+.5));
        }

        private sealed class Grid
        {
            private readonly double ax,bx,cx,ay,by,cy,az,bz,cz;
            internal Grid(FramePerspective q,FrameGeometry crop,int ow,int oh,int iw,int ih)
            {
                double ux=crop.Cos*crop.Width/ow/q.Width,uy=crop.Sin*crop.Height/oh/q.Width;
                double vx=-crop.Sin*crop.Width/ow/q.Height,vy=crop.Cos*crop.Height/oh/q.Height;
                double u=crop.CenterX/q.Width+ux*(.5-ow/2.0)+uy*(.5-oh/2.0);
                double v=crop.CenterY/q.Height+vx*(.5-ow/2.0)+vy*(.5-oh/2.0);
                ax=(q.a*ux+q.b*vx)*iw;bx=(q.a*uy+q.b*vy)*iw;cx=(q.a*u+q.b*v+q.c)*iw;
                ay=(q.d*ux+q.e*vx)*ih;by=(q.d*uy+q.e*vy)*ih;cy=(q.d*u+q.e*v+q.f)*ih;
                az=q.g*ux+q.h*vx;bz=q.g*uy+q.h*vy;cz=q.g*u+q.h*v+1;
            }
            internal Point Pixel(int x,int y)
            {
                double z=az*x+bz*y+cz;
                return new Point((ax*x+bx*y+cx)/z-.5,(ay*x+by*y+cy)/z-.5);
            }
            internal Int32Rect Bounds(Int32Rect output,int iw,int ih)
            {
                int r=output.X+output.Width-1,b=output.Y+output.Height-1;
                var p=new[]{Pixel(output.X,output.Y),Pixel(r,output.Y),Pixel(r,b),Pixel(output.X,b)};
                double x0=p[0].X,y0=p[0].Y,x1=x0,y1=y0;
                foreach(Point z in p){x0=Math.Min(x0,z.X);x1=Math.Max(x1,z.X);y0=Math.Min(y0,z.Y);y1=Math.Max(y1,z.Y);}
                int l=Math.Max(0,Math.Min(iw-1,(int)Math.Floor(x0))),t=Math.Max(0,Math.Min(ih-1,(int)Math.Floor(y0)));
                int end=Math.Min(iw-1,Math.Max(l,(int)Math.Floor(x1)+1)),bottom=Math.Min(ih-1,Math.Max(t,(int)Math.Floor(y1)+1));
                return new Int32Rect(l,t,end-l+1,bottom-t+1);
            }
        }

        internal BitmapSource Sample(FrameGeometry crop,int iw,int ih,int ow,int oh,Int32Rect requested,
            BitmapSource input,Func<Int32Rect,BitmapSource> read,CancellationToken token)
        {
            var grid=new Grid(this,crop,ow,oh,iw,ih);
            var loaded=new Int32Rect(0,0,iw,ih);
            if(input==null){
                loaded=grid.Bounds(requested,iw,ih);
                if((long)loaded.Width*loaded.Height<=4194304)input=read(loaded);
            }
            const int step=128;
            var result=new WriteableBitmap(requested.Width,requested.Height,input==null?96:input.DpiX,input==null?96:input.DpiY,PixelFormats.Rgba64,null);
            var pixels=new ushort[step*step*4];var source=new ushort[0];
            var tiles=new Stack<Int32Rect>();
            for(int top=0;top<requested.Height;top+=step)for(int left=0;left<requested.Width;left+=step)
                tiles.Push(new Int32Rect(requested.X+left,requested.Y+top,Math.Min(step,requested.Width-left),Math.Min(step,requested.Height-top)));
            while(tiles.Count!=0){
                token.ThrowIfCancellationRequested();
                var tile=tiles.Pop();
                var src=grid.Bounds(tile,iw,ih);
                // A strongly foreshortened corner can cover millions of source pixels.
                // Split the output region until the per-tile RGBA64 workspace fits in 2 MiB.
                if((long)src.Width*src.Height>262144 && (tile.Width>1 || tile.Height>1)){
                    if(tile.Width>=tile.Height){
                        int half=tile.Width/2;
                        tiles.Push(new Int32Rect(tile.X,tile.Y,half,tile.Height));
                        tiles.Push(new Int32Rect(tile.X+half,tile.Y,tile.Width-half,tile.Height));
                    }else{
                        int half=tile.Height/2;
                        tiles.Push(new Int32Rect(tile.X,tile.Y,tile.Width,half));
                        tiles.Push(new Int32Rect(tile.X,tile.Y+half,tile.Width,tile.Height-half));
                    }
                    continue;
                }
                var bitmap=input??read(src);
                var rect=input==null?new Int32Rect(0,0,src.Width,src.Height):new Int32Rect(src.X-loaded.X,src.Y-loaded.Y,src.Width,src.Height);
                int length=checked(src.Width*src.Height*4);if(source.Length<length)source=new ushort[length];
                bitmap.CopyPixels(rect,source,src.Width*8,0);
                for(int j=0;j<tile.Height;j++)for(int i=0;i<tile.Width;i++){
                    var point=grid.Pixel(tile.X+i,tile.Y+j);
                    double x=Math.Max(0,Math.Min(src.Width-1,point.X-src.X)),y=Math.Max(0,Math.Min(src.Height-1,point.Y-src.Y));
                    int ix=(int)x,iy=(int)y,k0=4*(iy*src.Width+ix),k1=ix+1<src.Width?k0+4:k0;
                    int k2=iy+1<src.Height?k0+4*src.Width:k0,k3=iy+1<src.Height?k1+4*src.Width:k1;
                    double fx=x-ix,fy=y-iy;
                    double w0=(1-fx)*(1-fy)*source[k0+3],w1=fx*(1-fy)*source[k1+3],w2=(1-fx)*fy*source[k2+3],w3=fx*fy*source[k3+3];
                    double alpha=w0+w1+w2+w3;int pos=4*(j*tile.Width+i);
                    pixels[pos+3]=(ushort)(alpha+.5);
                    for(int k=0;k<3;k++)pixels[pos+k]=alpha<=0?(ushort)0:(ushort)((w0*source[k0+k]+w1*source[k1+k]+w2*source[k2+k]+w3*source[k3+k])/alpha+.5);
                }
                result.WritePixels(new Int32Rect(tile.X-requested.X,tile.Y-requested.Y,tile.Width,tile.Height),pixels,tile.Width*8,0);
            }
            result.Freeze();return result;
        }
    }
}
