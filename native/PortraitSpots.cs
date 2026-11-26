using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AIPhotoStudio
{
    internal sealed class PortraitSpot
    {
        public int face {get;set;}
        public double x {get;set;}
        public double y {get;set;}
        public double radius {get;set;}
        internal double Score;
        internal LocalEdits.Stroke Stroke()
        {
            return new LocalEdits.Stroke{kind="heal",strength=1,radius=radius,points=new[]{new LocalEdits.Mark{x=x,y=y}}};
        }
    }

    internal sealed class PortraitSpotArea
    {
        private readonly FaceMesh mesh;
        private readonly PortraitRegion[] protectedFeatures;
        private readonly Point nose;
        private readonly Vector across,down;
        private readonly double noseWidth;
        internal PortraitSpotArea(FaceMesh face)
        {
            mesh=face;
            protectedFeatures=new[]{FaceMesh.LeftBrow,FaceMesh.RightBrow,FaceMesh.LeftEye,FaceMesh.RightEye,FaceMesh.Lips}
                .Select(ring=>new PortraitRegion(face,ring,3,7)).ToArray();
            across=new Vector((face.Points[327].X-face.Points[98].X)*face.Aspect,face.Points[327].Y-face.Points[98].Y);
            noseWidth=across.Length;across.Normalize();down=new Vector(-across.Y,across.X);
            nose=new Point((face.Points[1].X+face.Points[2].X)*face.Aspect/2,(face.Points[1].Y+face.Points[2].Y)/2);
        }
        internal bool Contains(double x,double y)
        {
            if(mesh.Regions.Value.Outline.At(x,y)<.985)return false;
            foreach(var feature in protectedFeatures)if(feature.At(x,y)>.015)return false;
            Vector offset=new Point(x*mesh.Aspect,y)-nose;
            double a=Vector.Multiply(offset,across)/Math.Max(.00001,.72*noseWidth);
            double b=Vector.Multiply(offset,down)/Math.Max(.00001,.55*noseWidth);
            return a*a+b*b>1;
        }
    }

    internal static class PortraitSpots
    {
        internal const int Limit=96;
        private const int Directions=12;
        private static readonly double[] U=Enumerable.Range(0,Directions).Select(i=>Math.Cos(i*2*Math.PI/Directions)).ToArray();
        private static readonly double[] V=Enumerable.Range(0,Directions).Select(i=>Math.Sin(i*2*Math.PI/Directions)).ToArray();
        private static double Q(double value){return Math.Round(value*65535)/65535;}
        internal static bool Safe(PortraitScene scene,PortraitSpot spot,int width,int height)
        {
            if(spot.face<0||spot.face>=scene.Faces.Length||spot.radius<.0004||spot.radius>.07)return false;
            var face=scene.Faces[spot.face];double r=spot.radius*Math.Min(width,height);
            if(r>.068*Math.Min(face.Box.w*width,face.Box.h*height)||!face.SpotArea.Value.Contains(spot.x,spot.y))return false;
            for(int i=0;i<Directions;i++){
                double x=spot.x+r*U[i]/width,y=spot.y+r*V[i]/height;
                if(!face.SpotArea.Value.Contains(x,y))return false;
                foreach(var other in scene.Faces)
                    if(other!=face&&other.Regions.Value.Guard.At(x,y)>.01)return false;
            }
            return scene.Faces.All(other=>other==face||other.Regions.Value.Guard.At(spot.x,spot.y)<.01);
        }
        internal static BitmapSource Apply(BitmapSource image,PortraitScene scene,PortraitSpot[] draft,CancellationToken cancel)
        {
            if(scene==null || scene.Spots.Length==0 && (draft==null||draft.Length==0))return image;
            var spots=scene.Spots.Concat(draft??new PortraitSpot[0]).Where(s=>!PortraitKeep.Overlaps(scene,s)).Select(s=>s.Stroke()).ToArray();
            if(spots.Length==0)return image;
            return SpotHealing.Apply(image,spots,cancel);
        }
        private sealed class Plane
        {
            internal int W,H,X,Y;
            internal byte[] Bytes;
            internal int At(int x,int y){return ((Math.Max(0,Math.Min(H-1,y-Y))*W)+Math.Max(0,Math.Min(W-1,x-X)))*4;}
            internal double Lum(int at){return (.299*Bytes[at+2]+.587*Bytes[at+1]+.114*Bytes[at])/255;}
            internal double Red(int at){return (Bytes[at+2]-.6*Bytes[at+1]-.4*Bytes[at])/255;}
        }
        // A spot must contrast against most directions of its skin neighborhood. A hard lighting edge
        // changes only half the ring. Check both luminance and excess redness at several face-relative sizes.
        private static double Score(Plane p,int x,int y,double radius,double[] luma,double[] red)
        {
            int at=p.At(x,y);
            if(p.Bytes[at+3]<238)return 0;
            double center=p.Lum(at),color=p.Red(at);
            if(center<.07||center>.91)return 0;
            int inner=Math.Max(1,(int)Math.Round(.24*radius));
            double ll=0,rr=0;
            for(int i=0;i<4;i++){
                int n=p.At(x+(i==0?inner:i==1?-inner:0),y+(i==2?inner:i==3?-inner:0));ll+=p.Lum(n);rr+=p.Red(n);
            }
            center=.58*center+.105*ll;color=.58*color+.105*rr;
            for(int i=0;i<Directions;i++){
                int n=p.At(x+(int)Math.Round(U[i]*radius),y+(int)Math.Round(V[i]*radius));
                if(p.Bytes[n+3]<238)return 0;
                luma[i]=p.Lum(n);red[i]=p.Red(n);
            }
            Array.Sort(luma);Array.Sort(red);
            double dark=luma[2]-center,redness=color-red[9];
            double brightnessNoise=luma[9]-luma[2],colorNoise=red[9]-red[2];
            if(brightnessNoise>.17||colorNoise>.14)return 0;
            double darkSignal=dark-.040-.75*brightnessNoise;
            double redSignal=redness-.035-.78*colorNoise;
            return Math.Max(darkSignal,redSignal)+Math.Max(0,Math.Min(darkSignal,redSignal))*.36;
        }
        internal static PortraitSpot[] Find(BitmapSource image,PortraitScene scene,int index,CancellationToken cancel)
        {
            var face=scene.Faces[index];var b=face.Box;
            int width=image.PixelWidth,height=image.PixelHeight;double breadth=Math.Min(b.w*width,b.h*height);
            if(breadth<52)return new PortraitSpot[0];
            int x0=Math.Max(0,(int)(b.x*width)),y0=Math.Max(0,(int)(b.y*height));
            int x1=Math.Min(width,(int)Math.Ceiling((b.x+b.w)*width)),y1=Math.Min(height,(int)Math.Ceiling((b.y+b.h)*height));
            var plane=new Plane{W=x1-x0,H=y1-y0,X=x0,Y=y0};
            plane.Bytes=new byte[checked(plane.W*plane.H*4)];
            var converted=new FormatConvertedBitmap(image,PixelFormats.Bgra32,null,0);
            converted.CopyPixels(new Int32Rect(x0,y0,plane.W,plane.H),plane.Bytes,plane.W*4,0);
            int step=Math.Max(1,(int)(breadth/255));
            var radii=new[]{Math.Max(2.4,breadth*.014),Math.Max(3.1,breadth*.024),Math.Max(3.8,breadth*.034)};
            var hits=new List<PortraitSpot>();var luma=new double[Directions];var red=new double[Directions];
            for(int y=y0;y<y1;y+=step){
                cancel.ThrowIfCancellationRequested();
                for(int x=x0;x<x1;x+=step){
                    double nx=x/(width-1d),ny=y/(height-1d);
                    if(!face.SpotArea.Value.Contains(nx,ny))continue;
                    double best=.016,r=0;
                    foreach(double s in radii){
                        double score=Score(plane,x,y,s,luma,red);
                        if(score>best+.001){best=score;r=s;}
                    }
                    if(r==0)continue;
                    var spot=new PortraitSpot{face=index,x=Q(nx),y=Q(ny),radius=Q(r*1.28/Math.Min(width,height)),Score=best};
                    if(Safe(scene,spot,width,height)&&!PortraitKeep.Overlaps(scene,spot))hits.Add(spot);
                }
            }
            var result=new List<PortraitSpot>();
            foreach(var s in hits.OrderByDescending(h=>h.Score)){
                bool close=false;
                foreach(var existing in scene.Spots.Concat(result)){
                    double dx=(s.x-existing.x)*width,dy=(s.y-existing.y)*height;
                    double sep=Math.Max(breadth*.042,(existing.radius+s.radius)*Math.Min(width,height)*.95);
                    if(dx*dx+dy*dy<sep*sep){close=true;break;}
                }
                if(!close)result.Add(s);
                if(result.Count==12)break;
            }
            if(result.Count==0)return result.ToArray();
            // The same texture matcher used for the final repair can abstain. Remove suggestions
            // that did not produce a measurable repair on the analyzed image.
            var healed=SpotHealing.Apply(image,result.Select(s=>s.Stroke()).ToArray(),cancel);
            var checkedImage=new FormatConvertedBitmap(healed,PixelFormats.Bgra32,null,0);
            var checkedBytes=new byte[plane.Bytes.Length];
            checkedImage.CopyPixels(new Int32Rect(x0,y0,plane.W,plane.H),checkedBytes,plane.W*4,0);
            return result.Where(s=>{
                int cx=(int)Math.Round(s.x*(width-1)),cy=(int)Math.Round(s.y*(height-1)),extent=(int)Math.Ceiling(s.radius*Math.Min(width,height));
                int changed=0;
                for(int y=cy-extent;y<=cy+extent;y++)for(int x=cx-extent;x<=cx+extent;x++){
                    int k=plane.At(x,y);
                    if(Math.Max(Math.Abs(plane.Bytes[k]-checkedBytes[k]),Math.Max(Math.Abs(plane.Bytes[k+1]-checkedBytes[k+1]),Math.Abs(plane.Bytes[k+2]-checkedBytes[k+2])))>=3)changed++;
                }
                return changed>=3;
            }).OrderBy(s=>s.y).ThenBy(s=>s.x).ToArray();
        }
    }
}
