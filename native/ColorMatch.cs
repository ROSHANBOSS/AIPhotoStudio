using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AIPhotoStudio
{
    internal static class ColorMatch
    {
        private static readonly string[] Keys={"exposure","temperature","tint","saturation","contrast","shadows","highlights"};
        private static readonly double[] SearchStep={.45,16,12,15,12,12,12};
        internal static Dictionary<string,double> Fit(string source,string reference,Dictionary<string,double> current,
            IEnumerable<string> locked,CancellationToken cancel)
        {
            var goal=Describe(Imaging.Render(reference,new Dictionary<string,double>(),160,cancel));
            double before,after;
            return Search(source,goal,current,locked,Keys,SearchStep,3,cancel,out before,out after);
        }

        internal static Dictionary<string,double> FitSet(string source,double[] goal,Dictionary<string,double> current,
            IEnumerable<string> locked,CancellationToken cancel,out double before,out double after)
        {
            return Search(source,goal,current,locked,new[]{"exposure","temperature","tint"},new[]{1.6,40,32},
                6,cancel,out before,out after);
        }

        internal static double SetDeviation(double distance) {return 100*Math.Sqrt(distance/8.85);}

        private static Dictionary<string,double> Search(string source,double[] goal,Dictionary<string,double> current,
            IEnumerable<string> locked,string[] keys,double[] steps,int levels,CancellationToken cancel,out double before,out double after)
        {
            var candidate=new Dictionary<string,double>(current);
            var specs=Imaging.Controls.ToDictionary(c=>c.key);
            var skip=new HashSet<string>(locked);
            double best=Distance(Describe(Imaging.Render(source,candidate,160,cancel)),goal);
            before=SetDeviation(best);
            for(int level=0;level<levels;level++)
            {
                for(int j=0;j<keys.Length;j++)
                {
                    string key=keys[j];if(skip.Contains(key))continue;
                    ControlSpec spec=specs[key];
                    double start=candidate[key],winner=start, step=steps[j]/(1<<level);
                    foreach(double sign in new[]{-1d,1d})
                    {
                        cancel.ThrowIfCancellationRequested();
                        double test=Math.Max(spec.min,Math.Min(spec.max,Math.Round((start+sign*step)/spec.step)*spec.step));
                        if(Math.Abs(test-start)<1e-7)continue;
                        candidate[key]=test;
                        double score=Distance(Describe(Imaging.Render(source,candidate,160,cancel)),goal);
                        if(score<best-1e-9){best=score;winner=test;}
                    }
                    candidate[key]=winner;
                }
            }
            after=SetDeviation(best);
            return keys.Where(k=>Math.Abs(current[k]-candidate[k])>1e-6).ToDictionary(k=>k,k=>candidate[k]);
        }

        internal static double[] Describe(BitmapSource image)
        {
            var pixels=new FormatConvertedBitmap(image,PixelFormats.Bgra32,null,0);
            int stride=pixels.PixelWidth*4;byte[] data=new byte[stride*pixels.PixelHeight];pixels.CopyPixels(data,stride,0);
            var hist=new int[256];var sums=new double[4];int count=0;
            for(int p=0;p<data.Length;p+=4)
            {
                if(data[p+3]<128)continue;
                double r=data[p+2]/255d,g=data[p+1]/255d,b=data[p]/255d;
                double y=.2126*r+.7152*g+.0722*b;
                hist[(int)Math.Round(y*255)]++;sums[0]+=y;
                // Discount near-black/white pixels in the chroma channels.
                double weight=Math.Min(1,Math.Min(y,1-y)*6);
                sums[1]+=(r-b)*weight;sums[2]+=(g-(r+b)*.5)*weight;
                sums[3]+=(Math.Max(r,Math.Max(g,b))-Math.Min(r,Math.Min(g,b)))*weight;count++;
            }
            if(count==0)throw new InvalidOperationException("照片没有足够的不透明像素可用于仿色。");
            var f=new double[7];for(int i=0;i<4;i++)f[i]=sums[i]/count;
            int n=0,q=0;double[] target={.12,.5,.88};
            for(int i=0;i<256&&q<3;i++){n+=hist[i];while(q<3&&n>=count*target[q])f[4+q++]=i/255d;}
            return f;
        }
        internal static double Distance(double[] a,double[] b)
        {
            double sum=0;double[] weights={1,2.4,2.4,1.5,.45,.65,.45};
            for(int i=0;i<a.Length;i++){double d=a[i]-b[i];sum+=weights[i]*d*d;}return sum;
        }
    }
}
