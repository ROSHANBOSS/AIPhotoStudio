using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AIPhotoStudio
{
    // Suggestions are transient. Only an explicitly chosen ellipse enters the normal versioned fitter.
    internal sealed class SetMatchSuggestions
    {
        internal sealed class Candidate
        {
            public double x {get;set;} public double y {get;set;}
            public double rx {get;set;} public double ry {get;set;}
            public double[] color {get;set;}
            public double score {get;set;}
            public string side {get;set;}
        }
        internal sealed class Person
        {
            public int number {get;set;}
            public BodyBox box {get;set;}
            public Candidate[] cheeks {get;set;}
            public bool uncertain {get;set;}
            public SetMatchFaceEffect effect {get;set;}
            public string overlay {get;set;}
            public object hair {get;set;}
        }
        internal sealed class Result
        {
            public Person[] people {get;set;}
            public Candidate[] backgrounds {get;set;}
            public bool savedFaces {get;set;}
            public int savedBodies {get;set;}
        }

        private readonly int width,height;
        private readonly byte[] pixels,skin,background,bodies;
        private readonly FaceMesh[] faces;
        private readonly bool coveredBodies;
        private const int CategorySide=256;
        internal SetMatchSuggestions(BitmapSource frame,FaceMesh[] faces,BodyScene pose,byte[] skin,byte[] background)
        {
            width=frame.PixelWidth;height=frame.PixelHeight;
            if(skin!=null||background!=null||pose!=null){
                var rgb=new FormatConvertedBitmap(frame,PixelFormats.Bgra32,null,0);
                pixels=new byte[width*height*4];rgb.CopyPixels(pixels,width*4,0);
            }
            this.faces=faces;this.skin=skin;this.background=background;
            if(pose!=null){
                bodies=new byte[BodyAssets.MaskSide*BodyAssets.MaskSide];
                foreach(var person in pose.Subjects)
                    for(int i=0;i<bodies.Length;i++)bodies[i]=Math.Max(bodies[i],person.Mask[i]);
                coveredBodies=faces.Length>0&&PhotoPeople.Match(pose.Subjects,faces).All(p=>!p.faceIndex.HasValue||p.bodyIndex.HasValue);
            }
        }

        // The image used by the fitter includes saved warps. In that case locate the subjects on
        // that exact image again instead of placing old source landmarks on newly shaped features.
        internal static bool SavedGeometryFits(IDictionary<string,double> values)
        {
            if(BodyEdits.Active(values))return false;
            if(values.Any(p=>p.Value!=0&&PortraitControls.Index(p.Key)>=0&&
                Array.IndexOf(PortraitControls.Keys,PortraitControls.Suffix(p.Key))>=Array.IndexOf(PortraitControls.Keys,"slim")))return false;
            double warp;
            if(values.TryGetValue("local_warp",out warp)&&warp==0)return true;
            return !LocalEdits.ReadSet(values).primary.Any(s=>s.kind=="warp"&&s.strength>0);
        }

        internal static byte[] Category(Dictionary<string,object> args,string key)
        {
            object raw;if(!args.TryGetValue(key,out raw)||raw==null)return null;
            string encoded=raw as string;
            if(encoded==null||encoded.Length!=4*((CategorySide*CategorySide+2)/3))
                throw new ArgumentException("取样分割图的大小无效。");
            var bytes=Convert.FromBase64String(encoded);
            if(bytes.Length!=CategorySide*CategorySide)throw new ArgumentException("取样分割图不完整。");
            return bytes;
        }
        private static byte CategoryAt(byte[] map,double x,double y)
        {
            return map[Math.Min(CategorySide-1,(int)(y*CategorySide))*CategorySide+Math.Min(CategorySide-1,(int)(x*CategorySide))];
        }
        internal static bool Contains(FaceMesh face,int[] ring,double x,double y)
        {
            bool inside=false;var a=face.Points[ring[ring.Length-1]];
            foreach(int index in ring){
                var b=face.Points[index];
                if((a.Y>y)!=(b.Y>y)&&x<a.X+(b.X-a.X)*(y-a.Y)/(b.Y-a.Y))inside=!inside;
                a=b;
            }
            return inside;
        }
        private static readonly int[][] Features={FaceMesh.LeftEye,FaceMesh.RightEye,FaceMesh.LeftBrow,FaceMesh.RightBrow,FaceMesh.Lips};
        private bool OnCheek(FaceMesh face,double x,double y)
        {
            if(!Contains(face,FaceMesh.Oval,x,y)||Features.Any(r=>Contains(face,r,x,y)))return false;
            foreach(var other in faces)if(other!=face&&Contains(other,FaceMesh.Oval,x,y))return false;
            return true;
        }
        private bool NearHead(Candidate a,FaceMesh face)
        {
            var b=face.Box;
            return a.x+a.rx>b.x-.28*b.w&&a.x-a.rx<b.x+1.28*b.w&&a.y+a.ry>b.y-.48*b.h&&a.y-a.ry<b.y+1.22*b.h;
        }
        private bool Measure(Candidate area,FaceMesh face)
        {
            bool cheek=face!=null;
            if(area.x-area.rx<=0||area.y-area.ry<=0||area.x+area.rx>=1||area.y+area.ry>=1)return false;
            if(!cheek&&faces.Any(f=>NearHead(area,f)))return false;
            int left=(int)Math.Floor((area.x-area.rx)*width),right=(int)Math.Ceiling((area.x+area.rx)*width);
            int top=(int)Math.Floor((area.y-area.ry)*height),bottom=(int)Math.Ceiling((area.y+area.ry)*height);
            int step=Math.Max(1,Math.Min(right-left,bottom-top)/21);
            int count=0,uncertain=0,clipped=0;
            double[] sums=new double[3],squares=new double[3];
            for(int j=top;j<bottom;j+=step)for(int i=left;i<right;i+=step){
                double x=(i+.5)/width,y=(j+.5)/height,dx=(x-area.x)/area.rx,dy=(y-area.y)/area.ry;
                if(dx*dx+dy*dy>1)continue;
                int at=(j*width+i)*4;
                if(pixels[at+3]<245)return false;
                if(cheek){
                    if(!OnCheek(face,x,y))return false;
                    if(skin!=null&&CategoryAt(skin,x,y)<152)uncertain++;
                }else{
                    if(bodies!=null&&BodyAssets.Probability(bodies,x,y)>.14)return false;
                    if(background!=null&&CategoryAt(background,x,y)<216)uncertain++;
                }
                double r=pixels[at+2],g=pixels[at+1],b=pixels[at],l=.2126*r+.7152*g+.0722*b;
                if(Math.Max(r,Math.Max(g,b))>=251||l<=17||l>=237)clipped++;
                sums[0]+=r;sums[1]+=g;sums[2]+=b;squares[0]+=r*r;squares[1]+=g*g;squares[2]+=b*b;
                count++;
            }
            if(count<26||clipped>count*(cheek?.16:.06)||uncertain>count*(cheek?.13:.025))return false;
            double spread=0;
            for(int k=0;k<3;k++){
                sums[k]/=count;
                spread=Math.Max(spread,Math.Sqrt(Math.Max(0,squares[k]/count-sums[k]*sums[k])));
            }
            double light=.2126*sums[0]+.7152*sums[1]+.0722*sums[2];
            if(spread>(cheek?27:24)||light<24||light>228)return false;
            area.color=sums.Select(v=>v/255).ToArray();
            // Reward an evenly lit patch; a broad midtone preference applies only to background.
            area.score=spread/40.0+(clipped+uncertain)/(double)count+(cheek?0:Math.Abs(light-125)/260);
            return true;
        }
        private Candidate Cheek(FaceMesh face,bool left,Func<double,double,double> coverage)
        {
            if(skin==null)return null;
            var eye=face.Points[left?33:263];var mouth=face.Points[left?61:291];var edge=face.Points[left?234:454];
            double physical=Math.Min(face.Box.w*width,face.Box.h*height);
            Candidate best=null;
            foreach(double down in new[]{.55,.40,.68,.81})foreach(double outwards in new[]{.08,.22,.35})
                foreach(double radius in new[]{.125,.09}){
                    Point center=eye+(mouth-eye)*down;center+=(edge-center)*outwards;
                    var a=new Candidate{x=center.X,y=center.Y,rx=Math.Max(SetMatchRegions.MinimumRadius,physical*radius/width),
                        ry=Math.Max(SetMatchRegions.MinimumRadius,physical*radius/height),side=left?"left":"right"};
                    if(!Measure(a,face))continue;
                    double px=center.X*width-.5,py=center.Y*height-.5,rx=a.rx*width*.45,ry=a.ry*height*.45;
                    double supported=(2*coverage(px,py)+coverage(px-rx,py)+coverage(px+rx,py)+coverage(px,py-ry)+coverage(px,py+ry))/6;
                    if(supported<.40)continue;
                    a.score+=(.125-radius)*5+Math.Abs(down-.55)*.15+2*(1-supported);
                    if(best==null||a.score<best.score)best=a;
                }
            return best;
        }
        private static bool OverlappingHeads(FaceMesh a,FaceMesh b)
        {
            double w=Math.Min(a.Box.w,b.Box.w),h=Math.Min(a.Box.h,b.Box.h);
            var p=a.Box;var q=b.Box;
            double overlap=Math.Max(0,Math.Min(p.x+p.w,q.x+q.w)-Math.Max(p.x,q.x))*
                Math.Max(0,Math.Min(p.y+p.h,q.y+q.h)-Math.Max(p.y,q.y));
            if(overlap>.68*(p.w*p.h+q.w*q.h-overlap))return true;
            return new[]{33,263}.All(i=>Math.Abs(a.Points[i].X-b.Points[i].X)<w*.27&&Math.Abs(a.Points[i].Y-b.Points[i].Y)<h*.22);
        }
        private SetMatchFaceEffect Effect(FaceMesh face)
        {
            var b=face.Box;
            var effect=new SetMatchFaceEffect{x=b.x+.5*b.w,y=b.y+.52*b.h,rx=.49*b.w,ry=.47*b.h};
            if(effect.x-effect.rx<0||effect.x+effect.rx>1||effect.y-effect.ry<0||effect.y+effect.ry>1)return null;
            int edge=Math.Min(width,height);
            var cuts=new List<double[]>();
            Action<double,Point,Point> erase=(r,a,z)=>cuts.Add(new[]{Math.Max(.005,r/edge),a.X,a.Y,z.X,z.Y});
            // Deliberately preserve the eyes, brows and lips even where the full-image skin
            // classifier calls them facial skin. Normalized stroke radii use the short edge.
            foreach(var ring in Features){
                var points=ring.Select(i=>face.Points[i]).ToArray();
                var a=points.OrderBy(p=>p.X).First();var z=points.OrderBy(p=>p.X).Last();
                double dx=(z.X-a.X)*width,dy=(z.Y-a.Y)*height,length=Math.Max(1,Math.Sqrt(dx*dx+dy*dy));
                double thick=points.Max(p=>Math.Abs((p.X-a.X)*width*dy-(p.Y-a.Y)*height*dx)/length);
                erase((thick+Math.Max(2,.018*b.w*width))*1.48,a,z);
            }
            // Short horizontal eraser strokes exclude low-probability skin, adjacent faces and
            // the outside of the actual mesh. They remain editable in the existing mask editor.
            double step=Math.Max(edge*.0055,Math.Min(b.w*width,b.h*height)/28);
            int columns=Math.Max(1,(int)Math.Ceiling(2*effect.rx*width/step)),rows=Math.Max(1,(int)Math.Ceiling(2*effect.ry*height/step));
            int safe=0,total=0;
            for(int j=0;j<rows;j++){
                double v=effect.y+((j+.5)/rows*2-1)*effect.ry;
                int start=-1;
                for(int i=0;i<=columns;i++){
                    double u=effect.x+((i+.5)/columns*2-1)*effect.rx;
                    bool remove=false;
                    if(i<columns&&Math.Pow((u-effect.x)/effect.rx,2)+Math.Pow((v-effect.y)/effect.ry,2)<1){
                        total++;remove=CategoryAt(skin,u,v)<157||!Contains(face,FaceMesh.Oval,u,v)||
                            faces.Any(other=>other!=face&&Contains(other,FaceMesh.Oval,u,v));
                        if(!remove)safe++;
                    }
                    if(remove&&start<0)start=i;
                    if(!remove&&start>=0){
                        double from=effect.x+((start+.5)/columns*2-1)*effect.rx;
                        double to=effect.x+((i-.5)/columns*2-1)*effect.rx;
                        erase(1.60*step,new Point(from,v),new Point(to,v));start=-1;
                    }
                }
            }
            if(safe<.62*total||cuts.Count>160)return null;
            effect.cuts=cuts.ToArray();return effect;
        }
        internal Result Recommend(bool savedFaces,int savedBodies,int focusNumber=0,byte[] hair=null,string binding=null,
            LocalEdits.MaskSet existing=null,string digest=null)
        {
            var people=new List<Person>();
            int shown=0;
            for(int i=0;i<faces.Length;i++){
                var f=faces[i];
                if(!savedFaces&&faces.Take(i).Any(other=>OverlappingHeads(f,other)))continue;
                shown++;
                int number=savedFaces?i+1:shown;
                if(focusNumber!=0&&focusNumber!=number)continue;
                bool ambiguous=faces.Any(other=>other!=f&&OverlappingHeads(f,other));
                var effect=ambiguous||skin==null?null:Effect(f);
                var coverage=effect==null?null:LocalEdits.MatchCoverage(effect.Geometry(),effect.Strokes(),width,height);
                var cheeks=effect==null?new Candidate[0]:new[]{Cheek(f,true,coverage),Cheek(f,false,coverage)}.Where(c=>c!=null).OrderBy(c=>c.score).ToArray();
                var head=ambiguous||hair==null?null:SetMatchHair.Locate(f,faces,hair,binding,width,height);
                if(head!=null&&existing!=null)head=head.Reuse(existing,digest,binding);
                people.Add(new Person{number=number,box=f.Box,uncertain=ambiguous,
                    cheeks=cheeks,effect=cheeks.Length==0?null:effect,overlay=cheeks.Length==0?null:effect.Overlay(width,height,coverage),
                    hair=head==null?null:head.View()});
            }
            var places=new List<Candidate>();
            if(focusNumber==0&&(background!=null||coveredBodies)){
                double radius=.055*Math.Min(width,height);
                for(int row=0;row<11;row++)for(int col=0;col<15;col++){
                    var a=new Candidate{x=(col+1)/16d,y=(row+1)/12d,rx=Math.Max(.01,radius/width),ry=Math.Max(.01,radius/height)};
                    if(Measure(a,null))places.Add(a);
                }
            }
            return new Result{people=people.ToArray(),backgrounds=places.OrderBy(a=>a.score).ToArray(),savedFaces=savedFaces,savedBodies=savedBodies};
        }
    }
}
