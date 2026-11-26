using System;
using System.Linq;
using System.Windows;

namespace AIPhotoStudio
{
    // A small, face-relative signed-distance raster for each anatomical contour. Cached with the mesh.
    internal sealed class PortraitRegion
    {
        private readonly double left,top,dx,dy;
        private readonly int width,height;
        private readonly byte[] mask;
        internal PortraitRegion(FaceMesh face,int[] ring,double softness,double outset)
        {
            dx=face.Box.w/250;dy=dx*face.Aspect;
            Point[] points=ring.Select(i=>new Point(face.Points[i].X/dx,face.Points[i].Y/dy)).ToArray();
            double pad=Math.Ceiling(Math.Abs(outset)+softness)+2;
            left=Math.Floor(points.Min(p=>p.X))-pad;top=Math.Floor(points.Min(p=>p.Y))-pad;
            width=(int)Math.Ceiling(points.Max(p=>p.X)-left+pad)+1;height=(int)Math.Ceiling(points.Max(p=>p.Y)-top+pad)+1;
            // Reject degenerate coordinates from a malformed imported scene before allocating a raster.
            if(width<1||height<1||width>2000||height>2000)throw new System.IO.InvalidDataException("人脸特征轮廓异常。");
            mask=new byte[width*height];
            for(int y=0;y<height;y++)for(int x=0;x<width;x++){
                double px=x+left,py=y+top,nearest=Double.MaxValue;bool inside=false;
                Point a=points[points.Length-1];
                foreach(Point b in points){
                    double ex=b.X-a.X,ey=b.Y-a.Y,rx=px-a.X,ry=py-a.Y;
                    double len=ex*ex+ey*ey,t=len==0?0:Math.Min(1,Math.Max(0,(rx*ex+ry*ey)/len));
                    double sx=rx-ex*t,sy=ry-ey*t;
                    nearest=Math.Min(nearest,sx*sx+sy*sy);
                    if((a.Y>py)!=(b.Y>py)&&px<a.X+ex*(py-a.Y)/ey)inside=!inside;
                    a=b;
                }
                double signed=(inside?1:-1)*Math.Sqrt(nearest);
                mask[y*width+x]=(byte)Math.Round(255*Ease((signed+outset)/softness+.5));
            }
        }
        internal static double Ease(double x){x=Math.Min(1,Math.Max(0,x));return x*x*(3-2*x);}
        internal double At(double x,double y)
        {
            double px=x/dx-left,py=y/dy-top;
            if(px<0||py<0||px>=width-1||py>=height-1)return 0;
            int ix=(int)px,iy=(int)py,at=iy*width+ix;
            double ax=px-ix,ay=py-iy;
            return ((mask[at]*(1-ax)+mask[at+1]*ax)*(1-ay)+(mask[at+width]*(1-ax)+mask[at+width+1]*ax)*ay)/255;
        }
    }
    internal sealed class PortraitRegions
    {
        internal readonly PortraitRegion Outline,Guard,LeftEye,RightEye,LeftBrow,RightBrow,Lips,Mouth;
        internal PortraitRegions(FaceMesh face)
        {
            Outline=new PortraitRegion(face,FaceMesh.Oval,7,-3.5);
            Guard=new PortraitRegion(face,FaceMesh.Oval,17,8);
            LeftEye=new PortraitRegion(face,FaceMesh.LeftEye,1.8,-.6);RightEye=new PortraitRegion(face,FaceMesh.RightEye,1.8,-.6);
            LeftBrow=new PortraitRegion(face,FaceMesh.LeftBrow,4,1);RightBrow=new PortraitRegion(face,FaceMesh.RightBrow,4,1);
            Lips=new PortraitRegion(face,FaceMesh.Lips,3.5,.5);Mouth=new PortraitRegion(face,FaceMesh.Mouth,2.5,-1.2);
        }
    }
}
