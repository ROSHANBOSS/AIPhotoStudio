using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AIPhotoStudio
{
    internal static partial class StudioFinish
    {
        internal static int DetailRadius(int shortEdge)
        {
            return Math.Max(2,Math.Min(5,(int)Math.Round(shortEdge*.0017)));
        }

        // Reflections add light to the scene. Invert a modest neutral veil in linear sRGB,
        // limited by the darkest channel. No samples from another eye or face enter the colour.
        private static readonly double[] linearSrgb=LinearSrgbTable();
        private static double[] LinearSrgbTable()
        {
            var table=new double[65536];
            for(int k=0;k<table.Length;k++)
            {
                double v=k/65535.0;
                table[k]=v<=.04045?v/12.92:Math.Pow((v+.055)/1.055,2.4);
            }
            return table;
        }
        private static ushort EncodeLinear(double linear)
        {
            double v=linear<=.0031308?12.92*linear:1.055*Math.Pow(linear,1.0/2.4)-.055;
            return Code(v*65535);
        }
        private static void ReduceVeil(ushort[] input,int i,ushort[] output,int o,int row,int radius,double amount)
        {
            double center=Lum(input,i);
            double tones=ImagingRecipe.Ramp(.10,.30,center)*(1-ImagingRecipe.Ramp(.79,.96,center));
            if(tones==0)return;
            int sx=radius*4,sy=radius*row;
            double edge=Math.Max(Math.Max(Math.Abs(Lum(input,i-sx)-center),Math.Abs(Lum(input,i+sx)-center)),
                Math.Max(Math.Abs(Lum(input,i-sy)-center),Math.Abs(Lum(input,i+sy)-center)));
            double gate=1-ImagingRecipe.Ramp(.030,.100,edge);
            if(gate==0)return;
            double darkest=linearSrgb[Math.Min(input[i],Math.Min(input[i+1],input[i+2]))];
            double veil=amount*tones*gate*Math.Min(.115,.46*darkest);
            if(veil<=0)return;
            for(int c=0;c<3;c++)output[o+c]=EncodeLinear((linearSrgb[input[i+c]]-veil)/(1-veil));
        }

        private static double Curvature(ushort[] input,int i,int offset,double center)
        {
            return Math.Abs(2*center-Lum(input,i-offset)-Lum(input,i+offset));
        }
        private static double WeaveProtection(ushort[] input,int i,int row,int radius)
        {
            double center=Lum(input,i);
            double fine=Math.Max(Curvature(input,i,4,center),Curvature(input,i,row,center));
            fine=Math.Max(fine,Math.Max(Curvature(input,i,row+4,center),Curvature(input,i,row-4,center)));
            double wide=Math.Max(Curvature(input,i,4*radius,center),Curvature(input,i,row*radius,center));
            wide=Math.Max(wide,Math.Max(Curvature(input,i,(row+4)*radius,center),Curvature(input,i,(row-4)*radius,center)));
            double highest=Math.Max(input[i],Math.Max(input[i+1],input[i+2]))/65535.0;
            double lowest=Math.Min(input[i],Math.Min(input[i+1],input[i+2]))/65535.0;
            double sparkle=ImagingRecipe.Ramp(.88,.97,highest)*
                Math.Max(ImagingRecipe.Ramp(.018,.056,highest-lowest),ImagingRecipe.Ramp(.010,.032,wide));
            return Math.Max(sparkle,Math.Max(ImagingRecipe.Ramp(.006,.024,fine),ImagingRecipe.Ramp(.012,.048,wide)));
        }

        // For fabric, reduce only the delta already produced by the three fold controls.
        // Unpainted pixels and unrelated adjustments remain exact. Both modes use the original
        // image scale even when operating on a crop of the 100% view.
        internal static BitmapSource SurfaceDetail(BitmapSource source,BitmapSource folded,double strength,
            Func<int,int,double> coverage,int shortEdge,CancellationToken cancel)
        {
            bool lens=folded==null;
            if(strength<=0)return folded??source;
            var original=source.Format==PixelFormats.Rgba64?source:new FormatConvertedBitmap(source,PixelFormats.Rgba64,null,0);
            var current=lens?original:folded.Format==PixelFormats.Rgba64?folded:new FormatConvertedBitmap(folded,PixelFormats.Rgba64,null,0);
            int w=original.PixelWidth,h=original.PixelHeight,row=w*4,stride=row*2,radius=DetailRadius(shortEdge);
            if(lens&&(w<2*radius+1||h<2*radius+1))return current;
            int rows=Math.Max(4,Math.Min(64,1048576/stride));
            var output=new WriteableBitmap(w,h,original.DpiX,original.DpiY,PixelFormats.Rgba64,null);
            var options=new ParallelOptions{CancellationToken=cancel,MaxDegreeOfParallelism=Math.Min(6,Environment.ProcessorCount)};
            for(int start=0;start<h;start+=rows)
            {
                cancel.ThrowIfCancellationRequested();
                int n=Math.Min(rows,h-start),top=Math.Max(0,start-radius),bottom=Math.Min(h,start+n+radius);
                var input=new ushort[checked((bottom-top)*row)];
                var dest=new ushort[checked(n*row)];
                original.CopyPixels(new Int32Rect(0,top,w,bottom-top),input,stride,0);
                current.CopyPixels(new Int32Rect(0,start,w,n),dest,stride,0);
                Parallel.For(0,n,options,dy=>{
                    int y=start+dy;
                    bool outerRow=y<radius||y>=h-radius;
                    if(lens&&outerRow)return;
                    for(int x=lens?radius:0;x<(lens?w-radius:w);x++)
                    {
                        int i=(y-top)*row+4*x,o=dy*row+4*x;
                        if(input[i+3]==0)continue;
                        if(lens)
                        {
                            double amount=coverage(x,y)*strength/100;
                            if(amount>0)ReduceVeil(input,i,dest,o,row,radius,amount);
                        }
                        else if(input[i]!=dest[o]||input[i+1]!=dest[o+1]||input[i+2]!=dest[o+2])
                        {
                            // At the edge there are no samples on both sides of a
                            // thread. Respect the requested protection conservatively.
                            double keep=strength/100;
                            if(!outerRow&&x>=radius&&x<w-radius)keep*=WeaveProtection(input,i,row,radius);
                            for(int c=0;c<3;c++)dest[o+c]=Code(dest[o+c]+(input[i+c]-dest[o+c])*keep);
                        }
                    }
                });
                output.WritePixels(new Int32Rect(0,start,w,n),dest,stride,0);
            }
            output.Freeze();
            return output;
        }
    }
}
