using System;
using System.Threading;

namespace AIPhotoStudio
{
    internal static partial class SpotHealing
    {
        private static double PupilRedness(Color3 c)
        {
            double companion=Math.Max(c.G,c.B),excess=c.R-companion;
            if(c.R<.20||excess<.08||c.R<1.60*companion+.035||
                c.G-c.B>.38*excess+.018||Math.Min(c.G,c.B)>.75)return 0;
            return Math.Min(1,(excess-.06)/.17);
        }

        private static void CorrectEye(Surface surface,Spot spot,Func<int,int,double> protect,CancellationToken token)
        {
            var rect=spot.Bounds;int w=rect.Width,h=rect.Height;
            double radius=Math.Max(1,spot.Radius),alpha;
            Color3 center;
            if(!surface.OriginalAt(spot.X,spot.Y,out center,out alpha)||alpha<.25)return;
            double weakest=Math.Min(center.R,Math.Min(center.G,center.B));
            bool catchlight=weakest>.57&&Math.Max(center.R,Math.Max(center.G,center.B))-weakest<.22;
            if(!catchlight&&PupilRedness(center)==0)return;
            var candidates=new byte[checked(w*h)];int seed=-1;
            double closest=catchlight?Math.Max(2,radius*.48):.1;closest*=closest;
            for(int y=0;y<h;y++)
            {
                token.ThrowIfCancellationRequested();
                for(int x=0;x<w;x++)
                {
                    int gx=rect.X+x,gy=rect.Y+y;
                    double dx=gx-spot.X,dy=gy-spot.Y,d2=dx*dx+dy*dy;
                    if(d2>=radius*radius)continue;
                    Color3 color;
                    if(!surface.OriginalAt(gx,gy,out color,out alpha)||alpha<.25||PupilRedness(color)==0)continue;
                    int i=y*w+x;candidates[i]=(byte)Math.Round((color.R-Math.Max(color.G,color.B))*255);
                    if(d2<=closest){closest=d2;seed=i;}
                }
            }
            if(seed<0)return;
            // Only the connected red pupil under this click is eligible. A neutral catchlight
            // is a possible starting hole, never a pixel to repaint.
            var queue=new int[candidates.Length];int read=0,end=1;
            // A flash pupil can touch warm skin in a small photo. Grow only through
            // red excess comparable to the tapped pupil, especially near a white glint.
            int minimum=Math.Max(1,(int)Math.Round(candidates[seed]*.64));
            queue[0]=seed;candidates[seed]=0;
            int left=w,right=-1,top=h,bottom=-1;
            while(read<end)
            {
                if((read&1023)==0)token.ThrowIfCancellationRequested();
                int i=queue[read++],x=i%w,y=i/w;
                left=Math.Min(left,x);right=Math.Max(right,x);top=Math.Min(top,y);bottom=Math.Max(bottom,y);
                for(int ny=Math.Max(0,y-1);ny<=Math.Min(h-1,y+1);ny++)
                    for(int nx=Math.Max(0,x-1);nx<=Math.Min(w-1,x+1);nx++)
                    {
                        int j=ny*w+nx;
                        if(candidates[j]>=minimum){candidates[j]=0;queue[end++]=j;}
                    }
            }
            int spanX=right-left+1,spanY=bottom-top+1;
            if(Math.Min(spanX,spanY)*3<Math.Max(spanX,spanY)||end<.18*spanX*spanY)return;
            if(catchlight)
            {
                // A reflection on glasses can be next to warm skin. Look for a pupil
                // surrounding the white hole on multiple sides at the glint's own scale.
                double ring=Math.Min(radius,1+1.45*Math.Sqrt(closest));
                var sectors=new bool[8];int seen=0;
                for(int j=0;j<end;j++)
                {
                    int i=queue[j],dx=rect.X+i%w-spot.X,dy=rect.Y+i/w-spot.Y;
                    if(dx*dx+dy*dy>ring*ring)continue;
                    int sector=(int)Math.Floor((Math.Atan2(dy,dx)+Math.PI)*4/Math.PI)%8;
                    if(!sectors[sector]){sectors[sector]=true;seen++;}
                }
                if(seen<6)return;
            }
            for(int j=0;j<end;j++)
            {
                if((j&1023)==0)token.ThrowIfCancellationRequested();
                int i=queue[j],x=rect.X+i%w,y=rect.Y+i/w;Color3 color;
                if(!surface.OriginalAt(x,y,out color,out alpha))continue;
                double dx=x-spot.X,dy=y-spot.Y;
                double amount=spot.Strength*PupilRedness(color)*Clamp((1-Math.Sqrt(dx*dx+dy*dy)/radius)*5,0,1);
                if(protect!=null)amount*=1-protect(x,y);
                if(amount>0)surface.Blend(x,y,new Color3((color.G+color.B)*.5,color.G,color.B),amount);
            }
        }
    }
}
