using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AIPhotoStudio
{
    internal static partial class StudioFinish
    {
        internal static bool Supported(string kind) { return kind=="skin" || kind=="clothes" || kind=="backdrop"; }
        internal static bool Predicted(string kind) { return kind=="hair" || Supported(kind); }
        internal static bool HandDetail(string kind) { return kind=="lens" || kind=="fabric"; }
        internal static bool Garment(string kind) { return kind=="clothes" || kind=="fabric"; }
        internal static bool FabricGuard(string key) { return LocalEdits.ExtraKey(key)&&key.EndsWith("_fabricguard",StringComparison.Ordinal); }
        internal static bool RequiresV10(string key)
        {
            return FabricGuard(key)||LocalEdits.ExtraKey(key)&&key.EndsWith("_lensglare",StringComparison.Ordinal);
        }
        internal static bool IsControl(string key) { return LocalEdits.ExtraKey(key) && key.EndsWith("_finish",StringComparison.Ordinal); }
        internal static bool RequiresV8(IDictionary<string,double> values) { return values!=null && values.Any(p=>IsControl(p.Key)&&p.Value!=0); }
        internal static bool DetailControl(string key)
        {
            return LocalEdits.ExtraKey(key)&&(key.EndsWith("_finefold",StringComparison.Ordinal)||
                key.EndsWith("_broadfold",StringComparison.Ordinal)||key.EndsWith("_skintone",StringComparison.Ordinal)||PersonalRetouch.IsBlurKey(key)||RequiresV10(key));
        }
        internal static bool Allows(string kind,string key)
        {
            if(PersonalRetouch.IsBlurKey(key))return kind=="backdrop";
            if(FabricGuard(key))return Garment(kind);
            if(RequiresV10(key))return kind=="lens";
            if(IsControl(key))return Supported(kind)||kind=="fabric";
            return key.EndsWith("_skintone",StringComparison.Ordinal)?kind=="skin":Garment(kind)||kind=="backdrop";
        }
        internal static string Name(string kind)
        {
            if(kind=="lens")return "镜片反光";
            if(kind=="fabric")return "精细衣料";
            return kind=="skin"?"脸颈与手臂":kind=="backdrop"?"背景布":kind=="clothes"?"衣服":"头发";
        }
        internal sealed class FaceSample
        {
            public double x {get;set;}
            public double y {get;set;}
            public double rx {get;set;}
            public double ry {get;set;}
            internal bool Valid {get{return x>=0&&x<=1&&y>=0&&y<=1&&rx>=.002&&rx<=.19&&ry>=.002&&ry<=.19;}}
            internal Int32Rect Bounds(int width,int height)
            {
                int left=Math.Max(0,(int)Math.Floor((x-rx)*width)),top=Math.Max(0,(int)Math.Floor((y-ry)*height));
                int right=Math.Min(width,(int)Math.Ceiling((x+rx)*width)),bottom=Math.Min(height,(int)Math.Ceiling((y+ry)*height));
                return new Int32Rect(left,top,right-left,bottom-top);
            }
            internal static FaceSample From(BodyGeometry person)
            {
                if(person==null || !person.Visible[0])throw new InvalidOperationException("请先识别并选中鼻部和双肩可见的人物。");
                var nose=person.Points[0];
                double dx=Math.Abs(person.Points[7].X-person.Points[8].X);
                double dy=Math.Abs((person.Points[11].Y+person.Points[12].Y)*.5-nose.Y);
                var sample=new FaceSample{x=nose.X,y=nose.Y,rx=Math.Min(.16,dx*.37),ry=Math.Min(.16,dy*.23)};
                if(!sample.Valid)throw new InvalidOperationException("脸部可见范围太小，请框选补检或手绘调色。");
                return sample;
            }
        }

        private static double Lum(ushort[] a,int i) {return (.2126*a[i]+.7152*a[i+1]+.0722*a[i+2])/65535.0;}
        private static double Limit(double x,double bound) {return Math.Max(-bound,Math.Min(bound,x));}
        private static ushort Code(double x) {return (ushort)Math.Round(Math.Max(0,Math.Min(65535,x)));}
        private static double[] FaceColor(BitmapSource src,Int32Rect bounds,Func<int,int,double> mask)
        {
            int left=bounds.X,top=bounds.Y,right=left+bounds.Width,bottom=top+bounds.Height;
            if(left>=right||top>=bottom)return null;
            int step=Math.Max(1,Math.Min(right-left,bottom-top)/28);
            var row=new ushort[(right-left)*4];
            var reds=new List<double>();var blues=new List<double>();
            for(int y=top;y<bottom;y+=step)
            {
                src.CopyPixels(new Int32Rect(left,y,right-left,1),row,row.Length*2,0);
                for(int x=left;x<right;x+=step)
                {
                    int i=(x-left)*4;
                    if(row[i+3]==0||mask(x,y)<.6)continue;
                    double l=Lum(row,i);
                    if(l<.055||l>.91)continue;
                    reds.Add((row[i]-row[i+1])/65535.0/(l+.12));
                    blues.Add((row[i+2]-row[i+1])/65535.0/(l+.12));
                }
            }
            if(reds.Count<4)return null;
            reds.Sort();blues.Sort();
            return new[]{reds[reds.Count/2],blues[blues.Count/2]};
        }

        internal static int FinishRadius(string kind,int shortEdge)
        {
            return kind=="skin"?0:Math.Max(2,Math.Min(64,(int)Math.Round(shortEdge*(kind=="clothes"?.013:.023))));
        }
        internal static BitmapSource Apply(BitmapSource source,string kind,double strength,Func<int,int,double> coverage,FaceSample face,CancellationToken cancel,int sourceShortEdge=0,SkinReference measured=null)
        {
            if(strength==0)return source;
            if(!Supported(kind))throw new ArgumentException("请选择肤色、衣服或背景布选区。");
            var src=source.Format==PixelFormats.Rgba64?source:new FormatConvertedBitmap(source,PixelFormats.Rgba64,null,0);
            bool skin=kind=="skin";
            double[] reference=null;
            if(skin)
            {
                if(face==null||!face.Valid)throw new ArgumentException("肤色选区缺少人脸参照。");
                reference=measured==null?FaceColor(src,face.Bounds(src.PixelWidth,src.PixelHeight),coverage):measured.Color;
                // A dark/small/painted-out face cannot give a reliable reference.
                if(reference==null)return source;
            }
            int w=src.PixelWidth,h=src.PixelHeight,stride=checked(w*8);
            int radius=FinishRadius(kind,sourceShortEdge>0?sourceShortEdge:Math.Min(w,h));
            int rows=Math.Max(1,Math.Min(48,1024*1024/stride));
            var output=new WriteableBitmap(w,h,src.DpiX,src.DpiY,PixelFormats.Rgba64,null);
            var options=new ParallelOptions{CancellationToken=cancel,MaxDegreeOfParallelism=Math.Min(6,Environment.ProcessorCount)};
            for(int start=0;start<h;start+=rows)
            {
                cancel.ThrowIfCancellationRequested();
                int top=Math.Max(0,start-radius),count=Math.Min(rows,h-start),end=Math.Min(h,start+count+radius);
                var input=new ushort[checked((end-top)*w*4)];var target=new ushort[checked(count*w*4)];
                src.CopyPixels(new Int32Rect(0,top,w,end-top),input,stride,0);
                Array.Copy(input,(start-top)*w*4,target,0,target.Length);
                Parallel.For(0,count,options,iy=>{
                    int y=start+iy;
                    for(int x=0;x<w;x++)
                    {
                        int i=((y-top)*w+x)*4,o=(iy*w+x)*4;
                        double weight=coverage(x,y)*strength/100.0;
                        if(weight<.003||input[i+3]==0)continue;
                        double lum=Lum(input,i);
                        if(skin)
                        {
                            // Preserve deep shadows, specular highlights and strongly different pigments such as lipstick.
                            double difference=Math.Abs((input[i]-input[i+1])/65535.0/(lum+.12)-reference[0])+
                                Math.Abs((input[i+2]-input[i+1])/65535.0/(lum+.12)-reference[1]);
                            weight*=Math.Max(0,Math.Min(1,(.72-difference)/.34))*
                                Math.Max(0,Math.Min(1,Math.Min((lum-.025)/.06,(.98-lum)/.10)));
                            double dr=Limit(reference[0]*(lum+.12)-(input[i]-input[i+1])/65535.0,.10)*weight*.67;
                            double db=Limit(reference[1]*(lum+.12)-(input[i+2]-input[i+1])/65535.0,.10)*weight*.67;
                            double dg=-.2126*dr-.0722*db;
                            target[o]=Code(input[i]+65535*(dr+dg));target[o+1]=Code(input[i+1]+65535*dg);target[o+2]=Code(input[i+2]+65535*(db+dg));
                            continue;
                        }
                        if(x==0||x==w-1||y==0||y==h-1)continue;
                        double near=(Lum(input,i-4)+Lum(input,i+4)+Lum(input,i-w*4)+Lum(input,i+w*4))*.25;
                        double texture=Math.Max(0,1-Math.Abs(near-lum)/.027);
                        if(texture<=0)continue;
                        double total=0,weights=0;
                        for(int sy=-1;sy<=1;sy++)for(int sx=-1;sx<=1;sx++)
                        {
                            if(sx==0&&sy==0)continue;
                            int nx=Math.Max(0,Math.Min(w-1,x+sx*radius)),ny=Math.Max(0,Math.Min(h-1,y+sy*radius));
                            int j=((ny-top)*w+nx)*4;
                            if(input[j+3]==0||coverage(nx,ny)<.5)continue;
                            double neighbor=Lum(input,j);
                            double chroma=Math.Abs((input[i]-input[i+1])-(input[j]-input[j+1]))/65535.0+
                                Math.Abs((input[i+2]-input[i+1])-(input[j+2]-input[j+1]))/65535.0;
                            double similarity=Math.Max(0,1-Math.Abs(neighbor-lum)/.23-chroma/.17);
                            total+=similarity*neighbor;weights+=similarity;
                        }
                        if(weights<2)continue;
                        double shift=Limit(total/weights-near,.085)*65535*weight*texture*.75;
                        for(int c=0;c<3;c++)target[o+c]=Code(input[i+c]+shift);
                    }
                });
                output.WritePixels(new Int32Rect(0,start,w,count),target,stride,0);
            }
            output.Freeze();return output;
        }
    }
}
