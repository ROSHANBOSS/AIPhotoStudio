using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AIPhotoStudio
{
    internal sealed class SetEvidence
    {
        public int version {get;set;}
        public string source {get;set;}
        public int frame {get;set;}
        public string mesh {get;set;}
        public string signature {get;set;}
        public string composition {get;set;}
        public string blur {get;set;}
        public string eyes {get;set;}
        public string detection {get;set;}
        public int faceCount {get;set;}
        public int[] softFaces {get;set;}
        public int[] narrowEyes {get;set;}
        public int[] uncertainEyes {get;set;}
        public ReviewFace[] faces {get;set;}
        public ReviewQuality[] quality {get;set;}
        public double brightFraction {get;set;}
        public double darkFraction {get;set;}
        public int medianLuma {get;set;}
        public double aspect {get;set;}
    }

    internal sealed class ReviewFace
    {
        public string key {get;set;}
        // Ordered from left to right in the oriented source, independent of head rotation.
        public double[] centers {get;set;}
        public string[] eyes {get;set;}
        public int closerEye {get;set;}
        public double minEyePixels {get;set;}
        public bool colorCover {get;set;}
        public string manual {get;set;}
    }

    internal sealed class ReviewQuality
    {
        public string blur {get;set;}
        public int[] softFaces {get;set;}
        public bool over {get;set;}
        public bool under {get;set;}
    }

    internal sealed class SetReviewPixels
    {
        internal const int Version=5;
        private const int Cells=10;
        private readonly int width,height;
        private readonly byte[] luma,alpha,color;
        private readonly BitmapSource detectionImage;
        private readonly int median;
        private readonly double bright,dark;
        private static readonly double[] FocusLimit={3.5,4.5,6};
        private static readonly double[] ClosedLimit={.10,.13,.16};
        private static readonly double[] ReviewLimit={.145,.16,.19};
        private static readonly double[] EndFraction={.24,.14,.065};
        private static readonly int[] DarkMedian={29,40,52},BrightMedian={227,217,206};
        internal readonly string Signature;

        internal SetReviewPixels(BitmapSource image)
        {
            double scale=768d/Math.Max(image.PixelWidth,image.PixelHeight);
            if(scale<1) {detectionImage=image;image=new TransformedBitmap(image,new ScaleTransform(scale,scale));}
            var source=new FormatConvertedBitmap(image,PixelFormats.Bgra32,null,0);
            width=source.PixelWidth; height=source.PixelHeight;
            var pixels=new byte[width*height*4]; source.CopyPixels(pixels,width*4,0);color=pixels;
            luma=new byte[width*height]; alpha=new byte[luma.Length];
            var totals=new long[Cells*Cells*3]; var weights=new long[Cells*Cells];
            var histogram=new int[256];
            for(int y=0;y<height;y++) for(int x=0;x<width;x++) {
                int i=y*width+x,p=i*4,cell=(y*Cells/height)*Cells+x*Cells/width;
                int b=pixels[p],g=pixels[p+1],r=pixels[p+2],a=pixels[p+3];
                luma[i]=(byte)((54*r+183*g+19*b+128)>>8); alpha[i]=(byte)a;
                histogram[luma[i]]+=a;
                weights[cell]+=a;
                totals[3*cell]+=r*a; totals[3*cell+1]+=g*a; totals[3*cell+2]+=b*a;
            }
            var thumb=new byte[totals.Length];
            for(int i=0;i<thumb.Length;i++) thumb[i]=weights[i/3]==0?(byte)128:(byte)(totals[i]/weights[i/3]);
            Signature=Convert.ToBase64String(thumb);
            int visible=histogram.Sum();
            median=-1;
            if(visible>=64*255) {
                bright=(double)histogram.Skip(248).Sum()/visible;
                dark=(double)histogram.Take(13).Sum()/visible;
                int sum=0;
                for(int n=0;n<histogram.Length;n++) {
                    sum+=histogram[n];
                    if(2L*sum>=visible) {median=n;break;}
                }
            }
        }

        // High-frequency energy on a common preview scale. A flat patch gives too little focus evidence.
        private double Focus(double left,double top,double right,double bottom)
        {
            return FocusIn(luma,alpha,width,Math.Max(1,(int)(width*left)),Math.Max(1,(int)(height*top)),
                Math.Min(width-1,(int)(width*right)),Math.Min(height-1,(int)(height*bottom)));
        }

        private static double FocusIn(byte[] luma,byte[] alpha,int width,int x0,int y0,int x1,int y1)
        {
            if(x1-x0<24 || y1-y0<24) return -1;
            double sum=0,square=0,edges=0; int count=0;
            for(int y=y0;y<y1;y++) for(int x=x0;x<x1;x++) {
                int i=y*width+x;
                if(alpha[i]<200 || alpha[i-1]<200 || alpha[i+1]<200 || alpha[i-width]<200 || alpha[i+width]<200) continue;
                int v=luma[i],laplace=4*v-luma[i-1]-luma[i+1]-luma[i-width]-luma[i+width];
                sum+=v; square+=v*v; edges+=laplace*laplace; count++;
            }
            if(count<400 || square/count-Math.Pow(sum/count,2)<64) return -1;
            return Math.Sqrt(edges/count);
        }

        private double DetailFocus(double left,double top,double right,double bottom)
        {
            int fullWidth=detectionImage.PixelWidth,fullHeight=detectionImage.PixelHeight;
            int x=Math.Max(0,(int)(left*fullWidth)),y=Math.Max(0,(int)(top*fullHeight));
            int w=Math.Min(fullWidth,(int)Math.Ceiling(right*fullWidth))-x,h=Math.Min(fullHeight,(int)Math.Ceiling(bottom*fullHeight))-y;
            if(w<26 || h<26)return -1;
            var source=new FormatConvertedBitmap(new CroppedBitmap(detectionImage,new Int32Rect(x,y,w,h)),PixelFormats.Bgra32,null,0);
            var bytes=new byte[w*h*4];source.CopyPixels(bytes,w*4,0);
            var luminance=new byte[w*h];var opacity=new byte[w*h];
            for(int i=0;i<luminance.Length;i++) {
                int p=i*4;luminance[i]=(byte)((54*bytes[p+2]+183*bytes[p+1]+19*bytes[p]+128)>>8);opacity[i]=bytes[p+3];
            }
            return FocusIn(luminance,opacity,w,1,1,w-1,h-1);
        }

        private int[] FaceTones(BodyBox box)
        {
            int x0=Math.Max(0,(int)((box.x+.22*box.w)*width)),y0=Math.Max(0,(int)((box.y+.25*box.h)*height));
            int x1=Math.Min(width,(int)((box.x+.78*box.w)*width)),y1=Math.Min(height,(int)((box.y+.78*box.h)*height));
            var bins=new int[256];
            for(int y=y0;y<y1;y++)for(int x=x0;x<x1;x++) {
                int i=y*width+x;bins[luma[i]]+=alpha[i];
            }
            int count=bins.Sum();
            if(count<128*255)return null;
            var tones=new int[3];
            int passed=0,next=0;
            for(int i=0;i<bins.Length && next<3;i++) {
                passed+=bins[i];
                while(next<3 && (long)passed*10>=(long)count*(1+4*next))tones[next++]=i;
            }
            return tones;
        }

        private static double Span(FaceMesh face,int first,int last,int width,int height)
        {
            var a=face.Points[first]; var b=face.Points[last];
            double x=(a.X-b.X)*width,y=(a.Y-b.Y)*height;
            return Math.Sqrt(x*x+y*y);
        }

        private static void SampleLid(FaceMesh face,int outer,int inner,int upper,int lower,
            int w,int h,ref double smallest,ref double largest)
        {
            var o=face.Points[outer];var end=face.Points[inner];
            var top=face.Points[upper];var bottom=face.Points[lower];
            double dx=(end.X-o.X)*w,dy=(end.Y-o.Y)*h,span2=dx*dx+dy*dy;
            if(span2<1)return;
            double mx=((top.X+bottom.X)*.5-o.X)*w,my=((top.Y+bottom.Y)*.5-o.Y)*h;
            double along=(mx*dx+my*dy)/span2;
            // Use lid samples inside this eye. Missing or unrelated landmarks cannot turn it into a closed eye.
            if(along<.16 || along>.84 || Math.Abs(mx*dy-my*dx)>.45*span2)return;
            double aperture=Math.Abs(dx*(bottom.Y-top.Y)*h-dy*(bottom.X-top.X)*w)/span2;
            smallest=Math.Min(smallest,aperture);largest=Math.Max(largest,aperture);
        }

        // Look above and below both lids. A small iris, a colored frame or a single glint is insufficient.
        private bool ChromaAt(Point pixel,out Vector chroma)
        {
            chroma=new Vector();
            int x=(int)Math.Round(pixel.X),y=(int)Math.Round(pixel.Y);
            if(x<0||x>=width||y<0||y>=height)return false;
            int i=4*(y*width+x),b=color[i],g=color[i+1],r=color[i+2],total=r+g+b;
            if(color[i+3]<220||total<65)return false;
            chroma=new Vector((double)(r-g)/total,(double)(b-g)/total);
            return true;
        }

        private bool StrongCover(FaceMesh face,int outer,int inner,out Vector cast)
        {
            cast=new Vector();
            Point corner=face.Points[outer],end=face.Points[inner];
            var center=new Point((corner.X+end.X)*width/2,(corner.Y+end.Y)*height/2);
            var along=new Vector((end.X-corner.X)*width,(end.Y-corner.Y)*height);
            if(along.Length<12)return false;
            if(along.X<0)along=-along;
            var down=new Vector(-along.Y,along.X);
            var cheek=new Vector();int count=0;
            for(int column=-2;column<=2;column++) {
                Vector sample;
                if(ChromaAt(center+along*(column*.17)+down*1.3,out sample)){cheek+=sample;count++;}
            }
            if(count<4)return false;
            cheek/=count;
            for(int row=-1;row<=1;row+=2) {
                int covered=0;
                for(int column=-2;column<=2;column++) {
                    Vector sample;
                    if(!ChromaAt(center+along*(column*.18)+down*(row*.26),out sample))continue;
                    Vector difference=sample-cheek;
                    if(sample.Length<.17||difference.Length<.22||Vector.Multiply(sample,difference)<=0)continue;
                    covered++;cast+=difference;
                }
                if(covered<4)return false;
            }
            if(cast.Length<1.7)return false;
            cast.Normalize();return true;
        }

        // Sample clothes and the surrounding scene relative to a single dominant face.
        // This measures scene colors, without computing identity. Out-of-frame and transparent patches stay absent.
        private string Composition(ReviewFace[] faces)
        {
            double first=0,second=0; double[] anchor=null;
            foreach(var face in faces) {
                var c=face.centers; double dx=(c[2]-c[0])*width,dy=(c[3]-c[1])*height;
                double span=Math.Sqrt(dx*dx+dy*dy);
                if(span>first) {second=first;first=span;anchor=c;}
                else second=Math.Max(second,span);
            }
            if(anchor==null || first<.042*height || second>.67*first)return "";
            double cx=(anchor[0]+anchor[2])*width/2,cy=(anchor[1]+anchor[3])*height/2;
            var samples=new byte[(9+8)*6*4];
            int offset=0;
            for(int area=0;area<2;area++)for(int row=0;row<6;row++)for(int col=0;col<(area==0?9:8);col++,offset+=4) {
                double x=cx+first*(area==0?(col-4)*.6:(col<4?col-5:col-2)*.8);
                double y=cy+first*(area==0?1.48+row*.424:-2.15+row*.68);
                int radius=Math.Max(1,(int)Math.Round(first*.16)),ix=(int)Math.Round(x),iy=(int)Math.Round(y);
                if(ix-radius<0 || ix+radius>=width || iy-radius<0 || iy+radius>=height)continue;
                int red=0,green=0,blue=0,count=0,visible=0,step=Math.Max(1,radius/4);
                for(int py=iy-radius;py<=iy+radius;py+=step)for(int px=ix-radius;px<=ix+radius;px+=step) {
                    int i=4*(py*width+px);count++;
                    if(color[i+3]<224)continue;
                    red+=color[i+2];green+=color[i+1];blue+=color[i];visible++;
                }
                if(visible<count*.9)continue;
                samples[offset]=(byte)(red/visible);samples[offset+1]=(byte)(green/visible);
                samples[offset+2]=(byte)(blue/visible);samples[offset+3]=255;
            }
            return Convert.ToBase64String(samples);
        }

        internal void Measure(SetEvidence result,FaceMesh[] faces,string detection)
        {
            result.signature=Signature; result.detection=detection; result.faceCount=faces.Length;
            result.brightFraction=bright; result.darkFraction=dark; result.medianLuma=median; result.aspect=(double)width/height;
            double focus=Focus(0,0,1,1);
            var faceFocus=new double[faces.Length];
            bool shadowDetail=faces.Length>0,highlightDetail=faces.Length>0;
            result.faces=new ReviewFace[faces.Length];
            for(int n=0;n<faces.Length;n++) {
                var f=faces[n]; var b=f.Box;
                // A dark uniform or white studio sweep can dominate the whole-frame histogram.
                // Protect the photo from a whole-frame warning only when every detected face has usable tonal detail.
                var tones=FaceTones(b);
                bool detail=tones!=null && tones[2]-tones[0]>=18;
                shadowDetail &= detail && tones[2]>=87 && tones[1]>=42;
                highlightDetail &= detail && tones[0]<=174 && tones[1]<=207;
                double x0=b.x+.18*b.w,y0=b.y+.2*b.h,x1=b.x+.82*b.w,y1=b.y+.79*b.h;
                faceFocus[n]=Focus(x0,y0,x1,y1);
                int w=width,h=height;
                double eyeFocus=faceFocus[n],left=Span(f,33,133,w,h),right=Span(f,263,362,w,h);
                if(detectionImage!=null && (eyeFocus<0 || Math.Min(left,right)<14)) {
                    // The actual detection image can still resolve a face lost in the common 768px focus thumbnail.
                    double detailedFocus=DetailFocus(x0,y0,x1,y1);
                    eyeFocus=eyeFocus<0?detailedFocus:Math.Min(detailedFocus,eyeFocus);
                    w=detectionImage.PixelWidth;h=detectionImage.PixelHeight;
                    left=Span(f,33,133,w,h);right=Span(f,263,362,w,h);
                }
                double a=Span(f,159,145,w,h)/Math.Max(.01,left),c=Span(f,386,374,w,h)/Math.Max(.01,right);
                double smallest=Math.Min(a,c),largest=Math.Max(a,c);
                // The center can already be opening while one side still has barely visible iris.
                SampleLid(f,33,133,160,144,w,h,ref smallest,ref largest);
                SampleLid(f,33,133,158,153,w,h,ref smallest,ref largest);
                SampleLid(f,263,362,387,373,w,h,ref smallest,ref largest);
                SampleLid(f,263,362,385,380,w,h,ref smallest,ref largest);
                var centers=new[]{(f.Points[33].X+f.Points[133].X)/2,(f.Points[33].Y+f.Points[133].Y)/2,
                    (f.Points[263].X+f.Points[362].X)/2,(f.Points[263].Y+f.Points[362].Y)/2};
                bool reverse=centers[0]>centers[2];
                if(reverse) centers=new[]{centers[2],centers[3],centers[0],centers[1]};
                var eye=new ReviewFace {centers=centers,eyes=new string[3],closerEye=(a<=c)!=reverse?0:1,minEyePixels=Math.Min(left,right),
                    key=String.Join("-",centers.Select(v=>Math.Round(v*65535).ToString(CultureInfo.InvariantCulture)))};
                Vector firstCast,secondCast;
                eye.colorCover=StrongCover(f,33,133,out firstCast)&&StrongCover(f,263,362,out secondCast)&&Vector.Multiply(firstCast,secondCast)>.85;
                bool resolved=Math.Min(left,right)>=8 && Math.Min(left,right)>=.55*Math.Max(left,right);
                for(int level=0;level<3;level++) {
                    bool narrow=largest<ClosedLimit[level];
                    // At 8–13 pixels wide a narrow slit can be subpixel. An apparently closed mesh is inconclusive.
                    if(!resolved || eyeFocus<FocusLimit[1] || eye.colorCover || (narrow && eye.minEyePixels<14))eye.eyes[level]="unknown";
                    else eye.eyes[level]=narrow?"suspect":smallest<ReviewLimit[level]?"unknown":"clear";
                }
                result.faces[n]=eye;
            }
            result.composition=Composition(result.faces);
            result.quality=new ReviewQuality[3];
            for(int level=0;level<3;level++) {
                int[] soft=Enumerable.Range(0,faces.Length).Where(n=>faceFocus[n]>=0 && faceFocus[n]<FocusLimit[level]).Select(n=>n+1).ToArray();
                result.quality[level]=new ReviewQuality {softFaces=soft,
                    blur=soft.Length>0?"face":focus<0?"unknown":focus<FocusLimit[level]?"image":"clear",
                    over=median>=0 && (!highlightDetail || median>=245) && (bright>=EndFraction[level] || median>=BrightMedian[level]),
                    under=median>=0 && (!shadowDetail || median<=10) && (dark>=EndFraction[level] || median<=DarkMedian[level])};
            }
            result.softFaces=result.quality[1].softFaces; result.blur=result.quality[1].blur;
            result.narrowEyes=Enumerable.Range(0,faces.Length).Where(n=>result.faces[n].eyes[1]=="suspect").Select(n=>n+1).ToArray();
            result.uncertainEyes=Enumerable.Range(0,faces.Length).Where(n=>result.faces[n].eyes[1]=="unknown").Select(n=>n+1).ToArray();
            result.eyes=result.narrowEyes.Length>0?"suspect":faces.Length>0 && result.uncertainEyes.Length==0?"clear":"unknown";
        }
    }
}
