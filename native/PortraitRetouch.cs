using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AIPhotoStudio
{
    internal static class PortraitRetouch
    {
        private sealed class Warp
        {
            internal Point Center;
            internal double Rx,Ry,MoveX,MoveY,ScaleX,ScaleY;
        }
        private sealed class Frame
        {
            internal Point[] P;
            internal Vector Axis,Down;
            internal double Breadth,Height;
            internal readonly List<Warp> Warps=new List<Warp>();
            internal Point Mid(int a,int b){return new Point((P[a].X+P[b].X)/2,(P[a].Y+P[b].Y)/2);}
            internal void Add(Point center,double rx,double ry,double moveX,double moveY,double scaleX,double scaleY)
            {
                if(moveX==0&&moveY==0&&scaleX==0&&scaleY==0)return;
                Warps.Add(new Warp{Center=center,Rx=rx*Breadth,Ry=ry*Height,MoveX=moveX*Breadth,MoveY=moveY*Height,ScaleX=scaleX,ScaleY=scaleY});
            }
            internal Frame(FaceMesh mesh,int width,int height,double[] v)
            {
                P=mesh.Points.Select(p=>new Point(p.X*width,p.Y*height)).ToArray();
                Axis=P[263]-P[33];Axis.Normalize();
                Down=new Vector(-Axis.Y,Axis.X);
                if(Vector.Multiply(Down,P[152]-P[10])<0)Down=-Down;
                Breadth=(P[454]-P[234]).Length;Height=(P[152]-P[10]).Length;
                for(int side=0;side<2;side++){
                    int sign=side==0?-1:1;
                    Add(P[side==0?93:323],.32,.28,sign*.054*v[9],0,0,0);
                    Add(P[side==0?172:397],.28,.23,sign*.052*v[10],-.009*v[10],0,0);
                    Add(P[side==0?149:378],.20,.16,-sign*.042*v[12],0,0,0);
                    Point eye=side==0?Mid(33,133):Mid(263,362);
                    Add(eye,.24,.14,-sign*.028*v[15],0,-.22*v[14],-.20*v[14]);
                    Add(P[side==0?98:327],.16,.12,-sign*.025*v[17],0,0,0);
                }
                Add(P[152],.31,.20,0,-.046*v[11],0,0);
                Add(Mid(10,151),.43,.23,0,.041*v[13],0,0);
                Add(Mid(168,4),.16,.22,0,0,-.29*v[16],0);
                Add(Mid(13,14),.30,.13,0,0,0,-.25*v[18]);
            }
            internal void Offset(double x,double y,out double dx,out double dy)
            {
                double hx=0,vy=0;
                foreach(var w in Warps){
                    double px=x-w.Center.X,py=y-w.Center.Y,localX=px*Axis.X+py*Axis.Y,localY=px*Down.X+py*Down.Y;
                    double a=localX/w.Rx,b=localY/w.Ry,r2=a*a+b*b;
                    if(r2>=1)continue;
                    double weight=(1-r2)*(1-r2);
                    hx+=weight*(w.MoveX+w.ScaleX*localX);vy+=weight*(w.MoveY+w.ScaleY*localY);
                }
                dx=hx*Axis.X+vy*Down.X;dy=hx*Axis.Y+vy*Down.Y;
                double cap=.073*Math.Max(Breadth,Height),dist=dx*dx+dy*dy;
                if(dist>cap*cap){double t=cap/Math.Sqrt(dist);dx*=t;dy*=t;}
            }
            internal double UnderEye(double x,double y,int eye)
            {
                Point a=P[eye==0?33:263],b=P[eye==0?133:362],lid=P[eye==0?145:374];
                double span=(a-b).Length;
                Point center=lid+.20*span*Down;
                Vector delta=new Point(x,y)-center;
                double u=Vector.Multiply(delta,Axis)/(.64*span),v=Vector.Multiply(delta,Down)/(.33*span);
                return PortraitRegion.Ease(1-u*u-v*v);
            }
        }
        private sealed class Strip
        {
            internal int X,Y,Width,Height;
            internal ushort[] Data;
            internal double Value(double x,double y,int channel)
            {
                x=Math.Max(0,Math.Min(Width-1,x-X));y=Math.Max(0,Math.Min(Height-1,y-Y));
                int ix=(int)x,iy=(int)y,ix1=Math.Min(ix+1,Width-1),iy1=Math.Min(iy+1,Height-1);
                double a=x-ix,b=y-iy;
                int top=iy*Width*4+channel,bottom=iy1*Width*4+channel;
                return ((Data[top+ix*4]*(1-a)+Data[top+ix1*4]*a)*(1-b)+(Data[bottom+ix*4]*(1-a)+Data[bottom+ix1*4]*a)*b)/65535;
            }
            internal double Lum(double x,double y){return .299*Value(x,y,0)+.587*Value(x,y,1)+.114*Value(x,y,2);}
            internal double Color(double x,double y,int channel,double alpha)
            {
                if(alpha>=1 || (x==Math.Truncate(x)&&y==Math.Truncate(y)))return Value(x,y,channel);
                if(alpha<=0)return 0;
                x=Math.Max(0,Math.Min(Width-1,x-X));y=Math.Max(0,Math.Min(Height-1,y-Y));
                int ix=(int)x,iy=(int)y,ix1=Math.Min(ix+1,Width-1),iy1=Math.Min(iy+1,Height-1);
                double a=x-ix,b=y-iy;
                int tl=(iy*Width+ix)*4,tr=(iy*Width+ix1)*4,bl=(iy1*Width+ix)*4,br=(iy1*Width+ix1)*4;
                double top=Data[tl+channel]*(double)Data[tl+3]*(1-a)+Data[tr+channel]*(double)Data[tr+3]*a;
                double bottom=Data[bl+channel]*(double)Data[bl+3]*(1-a)+Data[br+channel]*(double)Data[br+3]*a;
                return (top*(1-b)+bottom*b)/(65535d*65535*alpha);
            }
            internal double Neighbors(double x,double y,int radius,double lum)
            {
                double sum=0,weights=0;
                for(int j=-1;j<=1;j++)for(int i=-1;i<=1;i++){
                    double l=Lum(x+i*radius,y+j*radius);
                    double w=PortraitRegion.Ease(1-Math.Abs(l-lum)/.13)*(i!=0&&j!=0?.6:1)*Value(x+i*radius,y+j*radius,3);
                    sum+=l*w;weights+=w;
                }
                return sum/Math.Max(.001,weights);
            }
        }
        private static ushort Encode(double x){return (ushort)Math.Round(Math.Max(0,Math.Min(1,x))*65535);}
        private static double Amount(IDictionary<string,double> values,string key){double v;return values.TryGetValue(key,out v)?v/100:0;}
        internal static BitmapSource Apply(BitmapSource source,IDictionary<string,double> values,CancellationToken cancel)
        {
            if(!PortraitControls.Active(values))return source;
            var scene=PortraitAssets.Resolve(values);
            if(scene==null)throw new InvalidOperationException("请先识别当前照片中的人脸。");
            foreach(var pair in values.Where(p=>p.Value!=0&&PortraitControls.Index(p.Key)>=0))
                if(!PortraitAssets.CanAdjust(values,pair.Key))throw new InvalidOperationException("当前照片中没有可用于此调整的人脸部位。");
            if(source.Format!=PixelFormats.Rgba64){var c=new FormatConvertedBitmap(source,PixelFormats.Rgba64,null,0);c.Freeze();source=c;}
            var output=new WriteableBitmap(source);
            for(int i=0;i<scene.Faces.Length;i++){
                var amount=PortraitControls.Keys.Select(key=>Amount(values,PortraitControls.Key(i,key))).ToArray();
                if(amount.Any(v=>v!=0))ApplyFace(source,output,scene,scene.Faces[i],amount,cancel);
            }
            output.Freeze();return output;
        }
        private static void ApplyFace(BitmapSource source,WriteableBitmap output,PortraitScene scene,FaceMesh face,double[] v,CancellationToken cancel)
        {
            int width=source.PixelWidth,height=source.PixelHeight;
            var regions=face.Regions.Value;
            var frame=new Frame(face,width,height,v);
            var others=scene.Faces.Where(m=>m!=face).ToArray();
            var box=face.Box;
            // A narrow feather beyond the cheek contour lets the boundary move without displacing more distant background.
            int x0=Math.Max(0,(int)Math.Floor((box.x-.085*box.w)*width)),x1=Math.Min(width,(int)Math.Ceiling((box.x+1.085*box.w)*width));
            int y0=Math.Max(0,(int)Math.Floor((box.y-.085*box.h)*height)),y1=Math.Min(height,(int)Math.Ceiling((box.y+1.085*box.h)*height));
            bool tone=v.Take(9).Any(a=>a!=0);
            var keep=scene.KeepMarks.Where(p=>scene.Faces[p.face]==face).ToArray();
            var keepField=keep.Length>0&&tone?new PortraitKeep.Field(keep,(double)width/height):null;
            int radius=Math.Max(1,(int)Math.Round(Math.Min(14,frame.Breadth/110)));
            int padding=(int)Math.Ceiling((frame.Warps.Count>0?.073*Math.Max(frame.Breadth,frame.Height):0)+radius+2);
            int left=Math.Max(0,x0-padding),right=Math.Min(width,x1+padding),patchWidth=x1-x0;
            int chunk=Math.Max(1,Math.Min(96,1024*1024/(8*Math.Max(1,right-left))));
            for(int y=y0;y<y1;y+=chunk){
                cancel.ThrowIfCancellationRequested();
                int rows=Math.Min(chunk,y1-y),first=Math.Max(0,y-padding),last=Math.Min(height,y+rows+padding);
                var strip=new Strip{X=left,Y=first,Width=right-left,Height=last-first,Data=new ushort[checked((right-left)*(last-first)*4)]};
                source.CopyPixels(new Int32Rect(left,first,strip.Width,strip.Height),strip.Data,strip.Width*8,0);
                var rect=new Int32Rect(x0,y,patchWidth,rows);var patch=new ushort[checked(patchWidth*rows*4)];
                output.CopyPixels(rect,patch,patchWidth*8,0);
                for(int py=y;py<y+rows;py++)for(int px=x0;px<x1;px++){
                    double nx=(px+.5)/width,ny=(py+.5)/height,guard=regions.Guard.At(nx,ny);
                    if(guard<=0)continue;
                    bool protectedFace=false;
                    foreach(var other in others){
                        var b=other.Box;
                        if(nx>=b.x&&nx<=b.x+b.w&&ny>=b.y&&ny<=b.y+b.h&&other.Regions.Value.Guard.At(nx,ny)>.02){protectedFace=true;break;}
                    }
                    if(protectedFace)continue;
                    double dx,dy;frame.Offset(px+.5,py+.5,out dx,out dy);
                    double sx=px+guard*dx,sy=py+guard*dy;
                    if(!tone && sx==px && sy==py)continue;
                    double alpha=strip.Value(sx,sy,3);
                    if(alpha==0&&sx==px&&sy==py)continue;
                    double r=strip.Color(sx,sy,0,alpha),g=strip.Color(sx,sy,1,alpha),b0=strip.Color(sx,sy,2,alpha);
                    if(tone&&alpha>0)Tone(strip,face,frame,regions,keepField,v,(sx+.5)/width,(sy+.5)/height,sx,sy,radius,ref r,ref g,ref b0);
                    int at=((py-y)*patchWidth+px-x0)*4;
                    patch[at]=Encode(r);patch[at+1]=Encode(g);patch[at+2]=Encode(b0);
                    if(sx!=px||sy!=py)patch[at+3]=Encode(alpha);
                }
                output.WritePixels(rect,patch,patchWidth*8,0);
            }
        }
        private static void Tone(Strip src,FaceMesh face,Frame f,PortraitRegions m,PortraitKeep.Field keep,double[] v,double x,double y,
            double sx,double sy,int radius,ref double r,ref double g,ref double b)
        {
            double left=m.LeftEye.At(x,y),right=m.RightEye.At(x,y);
            double eye=Math.Max(face.LeftOpen?left:0,face.RightOpen?right:0);
            double lum=.299*r+.587*g+.114*b;
            // Geometry anchors skin protection even in monochrome. Chroma attenuates strongly saturated non-skin pixels.
            double exclude=Math.Max(Math.Max(left,right),Math.Max(m.Lips.At(x,y),Math.Max(m.LeftBrow.At(x,y),m.RightBrow.At(x,y))));
            double saturation=Math.Max(r,Math.Max(g,b))-Math.Min(r,Math.Min(g,b));
            double skin=m.Outline.At(x,y)*(1-exclude)*PortraitRegion.Ease((.86-saturation)/.35);
            if(keep!=null&&skin>0)skin*=keep.SkinWeight(x,y);
            double delta=0;
            if(skin>0){
                if(v[1]>0)delta+=.42*v[1]*skin*(src.Neighbors(sx,sy,radius,lum)-lum);
                double red=Math.Max(0,r-g-.033)*.42*v[2]*skin;
                delta+=(1-lum)*.13*v[0]*skin;
                r-=red;g+=red*.32;b+=red*.13;
                double even=.24*v[3]*skin;
                if(even>0){
                    double rr=src.Value(sx-radius,sy,0)+src.Value(sx+radius,sy,0)+src.Value(sx,sy-radius,0)+src.Value(sx,sy+radius,0);
                    double gg=src.Value(sx-radius,sy,1)+src.Value(sx+radius,sy,1)+src.Value(sx,sy-radius,1)+src.Value(sx,sy+radius,1);
                    double bb=src.Value(sx-radius,sy,2)+src.Value(sx+radius,sy,2)+src.Value(sx,sy-radius,2)+src.Value(sx,sy+radius,2);
                    double baseLum=(.299*rr+.587*gg+.114*bb)/4;
                    r+=even*((rr/4-baseLum)-(r-lum));g+=even*((gg/4-baseLum)-(g-lum));b+=even*((bb/4-baseLum)-(b-lum));
                }
                if(v[4]>0)delta+=v[4]*skin*.10*(1-lum)*Math.Max(f.UnderEye(sx+.5,sy+.5,0),f.UnderEye(sx+.5,sy+.5,1));
            }
            if(eye>0){
                delta+=.10*eye*v[5]*(1-lum);
                if(v[6]>0)delta+=.65*eye*v[6]*(lum-src.Neighbors(sx,sy,radius,lum));
                double sclera=eye*v[7]*PortraitRegion.Ease((lum-.20)/.33)*PortraitRegion.Ease((.43-saturation)/.27);
                // The pupil and iris have their own landmarks. Keep them out of the sclera correction.
                if(sclera>0){
                    int center=left>right?468:473;
                    double ir=(f.P[center+1]-f.P[center+3]).Length*.5;
                    double d=(new Point(sx+.5,sy+.5)-f.P[center]).Length;
                    sclera*=PortraitRegion.Ease((d/Math.Max(1,ir)-1)/.22);
                    double tint=sclera*.37;
                    r+=(lum-r)*tint;g+=(lum-g)*tint;b+=(lum-b)*tint;delta+=.17*sclera*(1-lum);
                }
            }
            if(v[8]>0&&face.MouthOpen){
                double tooth=m.Mouth.At(x,y)*PortraitRegion.Ease((lum-.34)/.31)*PortraitRegion.Ease((.36-saturation)/.24)*
                    PortraitRegion.Ease((.25-(r-g))/.14)*v[8];
                double neutral=.45*tooth;
                r+=(lum-r)*neutral;g+=(lum-g)*neutral;b+=(lum-b)*neutral;delta+=.24*tooth*(1-lum);
            }
            r+=delta;g+=delta;b+=delta;
        }
    }
}
