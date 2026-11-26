using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AIPhotoStudio
{
    public static class SubjectSelection
    {
        public const int Size = 128;
        private const int Cells = Size * Size;
        private static readonly int[] Dx = {-1,0,1,-1,1,-1,0,1};
        private static readonly int[] Dy = {-1,-1,-1,0,0,1,1,1};

        private struct Sample
        {
            internal double L, A, B, Opacity;
        }
        private sealed class Seed
        {
            internal int X, Y;
            internal Sample Mean;
            internal double Spread;
        }

        private static void CheckImage(BitmapSource image)
        {
            if (image == null) throw new ArgumentNullException("image");
            if (!image.IsFrozen && !image.CheckAccess()) throw new InvalidOperationException("跨线程处理前请冻结工作图。");
            if (image.PixelWidth < 1 || image.PixelHeight < 1 || image.PixelWidth > 65535 || image.PixelHeight > 65535 ||
                (long)image.PixelWidth * image.PixelHeight > 80000000L)
                throw new NotSupportedException("选区处理支持单边至 65535 像素、总计至 8000 万像素的工作图。");
        }
        private static bool Finite(double value) { return !Double.IsNaN(value) && !Double.IsInfinity(value); }
        private static int Cell(double point)
        {
            if (!Finite(point) || point < 0 || point > 1) throw new ArgumentOutOfRangeException("point", "取样点需位于照片内。");
            return Math.Min(Size-1,(int)(point*Size));
        }
        private static BitmapSource Rgba(BitmapSource image)
        {
            return image.Format == PixelFormats.Rgba64 ? image : new FormatConvertedBitmap(image,PixelFormats.Rgba64,null,0);
        }
        private static ReadOnlyCollection<ColorContext> Profiles(BitmapSource image)
        {
            var frame = image as BitmapFrame;
            return frame == null ? null : frame.ColorContexts;
        }
        private static BitmapSource ToWorking(BitmapSource source, ReadOnlyCollection<ColorContext> profiles)
        {
            if (profiles == null || profiles.Count == 0) return Rgba(source);
            PixelFormat f = source.Format;
            BitmapSource input = f == PixelFormats.Cmyk32 || f == PixelFormats.Gray16 || f == PixelFormats.Gray8 ? source : Rgba(source);
            return new ColorConvertedBitmap(input,profiles[0],new ColorContext(PixelFormats.Bgra32),PixelFormats.Rgba64);
        }
        private static Sample[] Grid(BitmapSource original)
        {
            BitmapSource input = ToWorking(original,Profiles(original));
            if (input.PixelWidth != Size || input.PixelHeight != Size)
            {
                var premultiplied = new FormatConvertedBitmap(input,PixelFormats.Prgba64,null,0);
                input = Rgba(new TransformedBitmap(premultiplied,new ScaleTransform((double)Size/input.PixelWidth,(double)Size/input.PixelHeight)));
            }
            var pixels = new ushort[Cells*4];
            input.CopyPixels(pixels,Size*8,0);
            var grid = new Sample[Cells];
            for (int i=0,p=0; i<Cells; i++,p+=4)
            {
                double r=ImagingRecipe.Linear[pixels[p]],g=ImagingRecipe.Linear[pixels[p+1]],b=ImagingRecipe.Linear[pixels[p+2]];
                // Linear sRGB -> Oklab. The lightness axis and opponent axes separate texture from chromatic edges.
                double l=Math.Pow(.4122214708*r+.5363325363*g+.0514459929*b,1.0/3);
                double m=Math.Pow(.2119034982*r+.6806995451*g+.1073969566*b,1.0/3);
                double s=Math.Pow(.0883024619*r+.2817188376*g+.6299787005*b,1.0/3);
                grid[i]=new Sample { L=.2104542553*l+.7936177850*m-.0040720468*s,
                    A=1.9779984951*l-2.4285922050*m+.4505937099*s, B=.0259040371*l+.7827717662*m-.8086757660*s,
                    Opacity=pixels[p+3]/65535.0 };
            }
            return grid;
        }
        private static double ColorDistanceSquared(Sample a, Sample b)
        {
            double l=a.L-b.L, ca=a.A-b.A, cb=a.B-b.B;
            return l*l+1.7*(ca*ca+cb*cb);
        }
        private static double Distance(Sample a, Sample b)
        {
            double alpha=a.Opacity-b.Opacity;
            return Math.Sqrt(ColorDistanceSquared(a,b)+.2*alpha*alpha);
        }
        private static Seed FindSeed(Sample[] grid, int x, int y)
        {
            Sample center=grid[y*Size+x];
            if (center.Opacity < .25) throw new InvalidOperationException("种子落在透明处，请在可见前景和背景中各点一次。");
            double total=0, variance=0;
            var mean=new Sample();
            for (int dy=-2; dy<=2; dy++) for (int dx=-2; dx<=2; dx++)
            {
                int xx=x+dx, yy=y+dy;
                if (xx<0 || yy<0 || xx>=Size || yy>=Size) continue;
                var value=grid[yy*Size+xx];
                double difference=Distance(center,value);
                if (value.Opacity < .20 || difference > .16) continue;
                double weight=value.Opacity/((1+.35*(dx*dx+dy*dy))*(1+220*difference*difference));
                total+=weight; variance+=weight*difference*difference;
                mean.L+=value.L*weight; mean.A+=value.A*weight; mean.B+=value.B*weight; mean.Opacity+=value.Opacity*weight;
            }
            if (total < 1.8) throw new InvalidOperationException("取样点附近缺少连续颜色，请在区域内部重新取点。");
            mean.L/=total; mean.A/=total; mean.B/=total; mean.Opacity/=total;
            return new Seed { X=x,Y=y,Mean=mean,Spread=Math.Sqrt(variance/total) };
        }

        // Indexed decrease-key heap. Each cell has at most one pending entry, including on detailed photographs.
        private sealed class Frontier
        {
            private readonly int[] heap=new int[Cells], location=new int[Cells];
            private readonly double[] distance;
            private int count;
            internal Frontier(double[] distance)
            {
                this.distance=distance;
                for (int i=0; i<Cells; i++) location[i]=-1;
            }
            internal void Offer(int cell, double cost)
            {
                if (location[cell]==-2 || cost>=distance[cell]) return;
                distance[cell]=cost;
                int slot=location[cell];
                if (slot<0) slot=count++;
                while (slot>0)
                {
                    int parent=(slot-1)/2;
                    if (distance[heap[parent]]<=cost) break;
                    heap[slot]=heap[parent]; location[heap[slot]]=slot; slot=parent;
                }
                heap[slot]=cell; location[cell]=slot;
            }
            internal int Take()
            {
                if (count==0) return -1;
                int result=heap[0], tail=heap[--count], slot=0;
                location[result]=-2;
                if (count==0) return result;
                while (2*slot+1<count)
                {
                    int child=2*slot+1;
                    if (child+1<count && distance[heap[child+1]]<distance[heap[child]]) child++;
                    if (distance[tail]<=distance[heap[child]]) break;
                    heap[slot]=heap[child]; location[heap[slot]]=slot; slot=child;
                }
                heap[slot]=tail; location[tail]=slot;
                return result;
            }
        }
        private static double[] Paths(Sample[] grid,Seed seed,double[] mismatch,double xStep,double yStep)
        {
            var distances=new double[Cells];
            for (int i=0; i<Cells; i++) distances[i]=Double.PositiveInfinity;
            var frontier=new Frontier(distances);
            for (int y=Math.Max(0,seed.Y-1); y<=Math.Min(Size-1,seed.Y+1); y++)
                for (int x=Math.Max(0,seed.X-1); x<=Math.Min(Size-1,seed.X+1); x++)
                {
                    int p=y*Size+x;
                    if (Distance(grid[p],seed.Mean)<.085 || (x==seed.X && y==seed.Y)) frontier.Offer(p,0);
                }
            double diagonal=Math.Sqrt(xStep*xStep+yStep*yStep);
            int cell;
            while ((cell=frontier.Take())>=0)
            {
                int x=cell%Size,y=cell/Size;
                for (int k=0; k<8; k++)
                {
                    int xx=x+Dx[k], yy=y+Dy[k];
                    if (xx<0 || yy<0 || xx>=Size || yy>=Size) continue;
                    int other=yy*Size+xx;
                    double step=Dx[k]==0 ? yStep : Dy[k]==0 ? xStep : diagonal;
                    double edge=Distance(grid[cell],grid[other]);
                    double cost=step*(.0025+.048*mismatch[other]*mismatch[other])+edge*(.55+3.8*edge);
                    frontier.Offer(other,distances[cell]+cost);
                }
            }
            return distances;
        }

        public static string Compute(BitmapSource original,double fx,double fy,double bx,double by)
        {
            CheckImage(original);
            if (original.PixelWidth<2 || original.PixelHeight<2) throw new InvalidOperationException("图像过小，无法划分主体和背景。");
            int x1=Cell(fx),y1=Cell(fy),x2=Cell(bx),y2=Cell(by);
            if ((x1-x2)*(x1-x2)+(y1-y2)*(y1-y2)<9) throw new InvalidOperationException("两个取样点太接近，请分别点击前景和背景内部。");
            Sample[] grid=Grid(original);
            Seed foreground=FindSeed(grid,x1,y1),background=FindSeed(grid,x2,y2);
            double separation=Math.Sqrt(ColorDistanceSquared(foreground.Mean,background.Mean));
            if (separation < .065+.7*(foreground.Spread+background.Spread))
                throw new InvalidOperationException("两处样本颜色过于相近，无法可靠分割。请改选颜色差异更清楚的前景和背景。");
            double[] fgCost=new double[Cells], bgCost=new double[Cells];
            int fgSupport=0,bgSupport=0,visible=0;
            for (int i=0; i<Cells; i++)
            {
                double f=Distance(grid[i],foreground.Mean), b=Distance(grid[i],background.Mean);
                fgCost[i]=f/(f+b+.00001); bgCost[i]=1-fgCost[i];
                if (grid[i].Opacity<.2) continue;
                visible++;
                if (f < .65*b && f < .65*separation) fgSupport++;
                if (b < .65*f && b < .65*separation) bgSupport++;
            }
            int required=Math.Max(7,visible/1000);
            if (fgSupport<required || bgSupport<required) throw new InvalidOperationException("取样颜色只出现在少量像素，请改选较稳定的区域内部。");
            double longest=Math.Max(original.PixelWidth,original.PixelHeight);
            double xStep=Math.Max(.08,original.PixelWidth/longest),yStep=Math.Max(.08,original.PixelHeight/longest);
            var df=Paths(grid,foreground,fgCost,xStep,yStep);
            var db=Paths(grid,background,bgCost,xStep,yStep);
            var raw=new double[Cells];
            for (int i=0; i<Cells; i++)
            {
                double geodesic=(db[i]-df[i])/(df[i]+db[i]+.015);
                double appearance=bgCost[i]-fgCost[i];
                raw[i]=grid[i].Opacity < .02 ? 0 : ImagingRecipe.Ramp(-.38,.38,.70*geodesic+.30*appearance);
            }
            var coverage=new byte[Cells];
            for (int y=0,i=0; y<Size; y++) for (int x=0; x<Size; x++,i++)
            {
                if (grid[i].Opacity<.02) continue;
                double sum=raw[i]*3,weight=3;
                for(int k=0; k<8; k++)
                {
                    int xx=x+Dx[k],yy=y+Dy[k];
                    if (xx<0 || yy<0 || xx>=Size || yy>=Size) continue;
                    int n=yy*Size+xx;
                    double diff=Distance(grid[i],grid[n]);
                    double w=(Dx[k]==0 || Dy[k]==0 ? 1 : .65)/(1+800*diff*diff);
                    sum+=raw[n]*w; weight+=w;
                }
                coverage[i]=(byte)Math.Round(255*sum/weight);
            }
            coverage[y1*Size+x1]=255; coverage[y2*Size+x2]=0;
            return Convert.ToBase64String(coverage);
        }

        private sealed class MaskSampler
        {
            private readonly byte[] mask;
            private readonly int[] x0,x1;
            private readonly double[] tx;
            private readonly int height;
            internal MaskSampler(byte[] mask,int width,int height)
            {
                if (mask==null || mask.Length!=Cells) throw new ArgumentException("选区数据应为 128×128 个 8 位覆盖值。","alpha");
                this.mask=(byte[])mask.Clone(); this.height=height;
                x0=new int[width]; x1=new int[width]; tx=new double[width];
                for (int x=0; x<width; x++)
                {
                    double pos=Math.Max(0,Math.Min(Size-1,(x+.5)*Size/width-.5));
                    x0[x]=(int)pos; x1[x]=Math.Min(Size-1,x0[x]+1); tx[x]=pos-x0[x];
                }
            }
            internal void Row(int y,out int a,out int b,out double fraction)
            {
                double pos=Math.Max(0,Math.Min(Size-1,(y+.5)*Size/height-.5));
                int row=(int)pos;
                a=row*Size; b=Math.Min(Size-1,row+1)*Size; fraction=pos-row;
            }
            internal double At(int x,int a,int b,double ty)
            {
                double top=mask[a+x0[x]]+(mask[a+x1[x]]-mask[a+x0[x]])*tx[x];
                double bottom=mask[b+x0[x]]+(mask[b+x1[x]]-mask[b+x0[x]])*tx[x];
                return (top+(bottom-top)*ty)/255;
            }
        }
        internal static string BackgroundSelection(byte[] foreground,int side)
        {
            var sampler=new MaskSampler(foreground,side,side);var coverage=new byte[side*side];
            for(int y=0;y<side;y++)
            {
                int a,b;double ty;sampler.Row(y,out a,out b,out ty);
                for(int x=0;x<side;x++)coverage[y*side+x]=(byte)Math.Round(255*(1-sampler.At(x,a,b,ty)));
            }
            return Convert.ToBase64String(coverage);
        }
        private static BitmapSource Frozen(BitmapSource image)
        {
            if (image.IsFrozen) return image;
            var copy=(BitmapSource)image.CloneCurrentValue();
            copy.Freeze(); return copy;
        }
        public static BitmapSource Apply(BitmapSource image,byte[] alpha,double foregroundEV,double backgroundEV,CancellationToken token)
        {
            if (!Finite(foregroundEV) || !Finite(backgroundEV) || Math.Abs(foregroundEV)>5 || Math.Abs(backgroundEV)>5)
                throw new ArgumentOutOfRangeException("EV","双侧曝光应位于 −5 至 +5 EV。");
            return Paint(image,alpha,Math.Pow(2,foregroundEV),Math.Pow(2,backgroundEV),false,token);
        }
        public static BitmapSource Overlay(BitmapSource image,byte[] alpha)
        {
            return Paint(image,alpha,1,1,true,CancellationToken.None);
        }
        private static BitmapSource Paint(BitmapSource image,byte[] alpha,double fg,double bg,bool overlay,CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); CheckImage(image);
            if (alpha==null || alpha.Length!=Cells) throw new ArgumentException("无效的双种子选区尺寸。","alpha");
            if (!overlay && fg==1 && bg==1) return Frozen(image);
            int w=image.PixelWidth,h=image.PixelHeight,stride=w*8;
            var mask=new MaskSampler(alpha,w,h);
            var profiles=Profiles(image);
            bool tagged=profiles!=null && profiles.Count>0;
            BitmapSource working=ToWorking(image,profiles);
            int band=Math.Min(h,Math.Max(1,Math.Min(64,2097152/stride)));
            var buffer=new ushort[w*band*4];
            var result=new WriteableBitmap(w,h,image.DpiX,image.DpiY,PixelFormats.Rgba64,null);
            var parallel=new ParallelOptions {CancellationToken=token,MaxDegreeOfParallelism=Math.Min(6,Environment.ProcessorCount)};
            for(int y=0; y<h; y+=band)
            {
                token.ThrowIfCancellationRequested();
                int count=Math.Min(band,h-y), start=y;
                var rect=new Int32Rect(0,y,w,count);
                working.CopyPixels(rect,buffer,stride,0);
                Parallel.For(0,count,parallel,delegate(int row)
                {
                    int a,b; double ty;
                    mask.Row(start+row,out a,out b,out ty);
                    for(int x=0,p=row*w*4; x<w; x++,p+=4)
                    {
                        if (buffer[p+3]==0) continue;
                        double m=mask.At(x,a,b,ty);
                        if (overlay)
                        {
                            double mix=.44*m;
                            if (mix==0) continue;
                            buffer[p]=(ushort)Math.Round(buffer[p]+(11000-buffer[p])*mix);
                            buffer[p+1]=(ushort)Math.Round(buffer[p+1]+(55500-buffer[p+1])*mix);
                            buffer[p+2]=(ushort)Math.Round(buffer[p+2]+(44500-buffer[p+2])*mix);
                        }
                        else
                        {
                            double gain=bg+(fg-bg)*m;
                            if (gain==1) continue;
                            for(int c=0; c<3; c++) buffer[p+c]=ImagingRecipe.U16(ImagingRecipe.EncodeBounded(ImagingRecipe.Linear[buffer[p+c]]*gain));
                        }
                    }
                });
                result.WritePixels(rect,buffer,stride,0);
            }
            result.Freeze();
            if (!tagged) return result;
            // The main engine normally supplies untagged, normalized working pixels. Directly passed tagged RGB frames
            // also retain their original profile and untouched samples after the excursion through working sRGB.
            if (image.Format==PixelFormats.Cmyk32 || image.Format==PixelFormats.Gray16 || image.Format==PixelFormats.Gray8)
            {
                var rgbProfile=new ReadOnlyCollection<ColorContext>(new[]{new ColorContext(PixelFormats.Bgra32)});
                var rgb=BitmapFrame.Create(result,null,null,rgbProfile); rgb.Freeze(); return rgb;
            }
            BitmapSource original=Rgba(image);
            var restore=new ColorConvertedBitmap(result,new ColorContext(PixelFormats.Bgra32),profiles[0],PixelFormats.Rgba64);
            var restored=new WriteableBitmap(w,h,image.DpiX,image.DpiY,PixelFormats.Rgba64,null);
            var exact=new ushort[buffer.Length];
            for(int y=0;y<h;y+=band)
            {
                token.ThrowIfCancellationRequested();
                int n=Math.Min(band,h-y);
                var rect=new Int32Rect(0,y,w,n);
                restore.CopyPixels(rect,buffer,stride,0); original.CopyPixels(rect,exact,stride,0);
                for(int row=0;row<n;row++)
                {
                    int a,b;double ty; mask.Row(y+row,out a,out b,out ty);
                    for(int x=0,p=row*w*4;x<w;x++,p+=4)
                    {
                        double m=mask.At(x,a,b,ty);
                        if(exact[p+3]==0 || (overlay ? m==0 : bg+(fg-bg)*m==1))
                            for(int c=0;c<3;c++) buffer[p+c]=exact[p+c];
                        buffer[p+3]=exact[p+3];
                    }
                }
                restored.WritePixels(rect,buffer,stride,0);
            }
            restored.Freeze();
            var taggedResult=BitmapFrame.Create(restored,null,null,profiles); taggedResult.Freeze(); return taggedResult;
        }
    }
}
