using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AIPhotoStudio
{
    internal static class PersonalRetouch
    {
        internal static bool IsBlurKey(string key)
        {
            return LocalEdits.ExtraKey(key)&&key.EndsWith("_backgroundBlur",StringComparison.Ordinal);
        }
        internal static bool IsNewControl(string key){return key=="noise_chroma"||key=="noise_luma"||IsBlurKey(key);}
        internal static int BlurRadius(int edge,double amount)
        {
            return amount<=0?0:Math.Max(1,(int)Math.Round(Math.Min(70,Math.Max(2,edge*.022))*amount/100));
        }

        // Sliding triangular kernel: two box filters composed, with symmetric edge replication.
        // Four channels carry background RGB multiplied by coverage and opacity, plus the weight.
        private static void Tent(long[] input,long[] output,int start,int stride,int count,int radius)
        {
            for(int c=0;c<4;c++)
            {
                int p=start+c;long weighted=0,left=0,right=0;
                for(int d=-radius;d<=radius;d++)weighted+=(radius+1-Math.Abs(d))*input[p+stride*Math.Max(0,Math.Min(count-1,d))];
                left=(radius+1L)*input[p];
                for(int d=1;d<=radius+1;d++)right+=input[p+stride*Math.Min(count-1,d)];
                for(int i=0;i<count;i++)
                {
                    output[p+stride*i]=weighted;
                    weighted+=right-left;
                    long next=input[p+stride*Math.Min(count-1,i+1)];
                    left+=next-input[p+stride*Math.Max(0,i-radius)];
                    right+=input[p+stride*Math.Min(count-1,i+radius+2)]-next;
                }
            }
        }
        internal static BitmapSource Blur(BitmapSource source,Func<int,int,double> coverage,int radius,double amount,CancellationToken token)
        {
            if(radius<=0)return source;
            var surface=source.Format==PixelFormats.Rgba64?source:new FormatConvertedBitmap(source,PixelFormats.Rgba64,null,0);
            int w=surface.PixelWidth,h=surface.PixelHeight;
            var output=new WriteableBitmap(w,h,surface.DpiX,surface.DpiY,PixelFormats.Rgba64,null);
            // Integers keep running sums identical across previews and arbitrary tile origins.
            // RGB16 × coverage16 × 71^4 fits in a signed 64-bit accumulator.
            double minWeight=.003*65535*Math.Pow(radius+1,4);
            int batch=Math.Max(4,Math.Min(128,220000/w));
            var options=new ParallelOptions{CancellationToken=token,MaxDegreeOfParallelism=Math.Min(6,Environment.ProcessorCount)};
            for(int y=0;y<h;y+=batch)
            {
                token.ThrowIfCancellationRequested();
                int rows=Math.Min(batch,h-y),top=Math.Max(0,y-radius),bottom=Math.Min(h,y+rows+radius),n=bottom-top;
                var pixels=new ushort[checked(w*n*4)];surface.CopyPixels(new Int32Rect(0,top,w,n),pixels,w*8,0);
                var weights=new float[checked(w*n)];var data=new long[pixels.Length];var scratch=new long[data.Length];
                Parallel.For(0,n,options,row=>{
                    for(int x=0;x<w;x++)
                    {
                        int i=row*w+x,p=i*4;
                        double mask=coverage(x,top+row);
                        long weight=(long)Math.Round(mask*pixels[p+3]);
                        weights[i]=(float)mask;
                        for(int c=0;c<3;c++)data[p+c]=pixels[p+c]*weight;
                        data[p+3]=weight;
                    }
                    Tent(data,scratch,row*w*4,4,w,radius);
                });
                Parallel.For(0,w,options,x=>Tent(scratch,data,x*4,w*4,n,radius));
                int offset=(y-top)*w*4,stop=offset+rows*w*4;
                for(int p=offset;p<stop;p+=4)
                {
                    double blend=weights[p/4]*Math.Min(1,amount/15);
                    if(blend<=0||data[p+3]<minWeight||pixels[p+3]==0)continue;
                    for(int c=0;c<3;c++)
                    {
                        double value=pixels[p+c]+blend*((double)data[p+c]/data[p+3]-pixels[p+c]);
                        pixels[p+c]=(ushort)Math.Max(0,Math.Min(65535,Math.Round(value)));
                    }
                }
                if(offset!=0)Array.Copy(pixels,offset,pixels,0,rows*w*4);
                output.WritePixels(new Int32Rect(0,y,w,rows),pixels,w*8,0);
            }
            output.Freeze();return output;
        }
    }
}
