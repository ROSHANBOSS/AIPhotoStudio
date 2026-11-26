using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AIPhotoStudio
{
    internal static partial class StudioFinish
    {
        private static double[][] SkinPalette(BitmapSource source,Int32Rect bounds,Func<int,int,double> mask)
        {
            // Estimate face chroma separately by brightness. Empty bands interpolate from the overall reference.
            var overall=FaceColor(source,bounds,mask);
            if(overall==null)return null;
            int left=bounds.X,right=left+bounds.Width,top=bounds.Y,bottom=top+bounds.Height;
            var reductions=new List<double>[3,2];
            for(int b=0;b<3;b++)for(int c=0;c<2;c++)reductions[b,c]=new List<double>();
            int step=Math.Max(1,Math.Min(right-left,bottom-top)/32);
            var row=new ushort[(right-left)*4];
            for(int y=top;y<bottom;y+=step)
            {
                source.CopyPixels(new Int32Rect(left,y,right-left,1),row,row.Length*2,0);
                for(int x=left;x<right;x+=step)
                {
                    int i=(x-left)*4;double lum=Lum(row,i);
                    if(row[i+3]==0||mask(x,y)<.68||lum<.065||lum>.91)continue;
                    double r=(row[i]-row[i+1])/65535.0/(lum+.12),b=(row[i+2]-row[i+1])/65535.0/(lum+.12);
                    if(Math.Abs(r-overall[0])+Math.Abs(b-overall[1])>.33)continue;
                    int band=lum<.33?0:lum<.62?1:2;
                    reductions[band,0].Add(r);reductions[band,1].Add(b);
                }
            }
            var result=new[]{new double[2],new double[2],new double[2]};
            for(int b=0;b<3;b++)for(int c=0;c<2;c++)
            {
                var samples=reductions[b,c];samples.Sort();
                result[b][c]=samples.Count>=6?.8*samples[samples.Count/2]+.2*overall[c]:overall[c];
            }
            return result;
        }

        internal sealed class SkinReference
        {
            internal double[] Color;
            internal double[][] Palette;
        }

        internal static SkinReference MeasureSkin(BitmapSource pixels,FaceSample face,Int32Rect bounds,
            Func<int,int,double> coverage,double finish,double skin,CancellationToken cancel)
        {
            var measured=new SkinReference();
            if(finish!=0)measured.Color=FaceColor(pixels,bounds,coverage);
            if(skin!=0)
            {
                // Refinement samples the result of the preceding skin finish in the saved layer order.
                var adjusted=Apply(pixels,"skin",finish,coverage,face,cancel,0,measured);
                measured.Palette=SkinPalette(adjusted,bounds,coverage);
            }
            return measured;
        }

        private static void BlendSkin(ushort[] input,int i,ushort[] dest,int o,double l,double amount,double[][] palette)
        {
            double band=Math.Max(0,Math.Min(2,(l-.18)/.29));
            int a=(int)band,b=Math.Min(2,a+1);double t=band-a;
            double r=palette[a][0]+(palette[b][0]-palette[a][0])*t;
            double blue=palette[a][1]+(palette[b][1]-palette[a][1])*t;
            double rr=(input[i]-input[i+1])/65535.0/(l+.12),bb=(input[i+2]-input[i+1])/65535.0/(l+.12);
            double deviation=Math.Abs(r-rr)+Math.Abs(blue-bb);
            amount*=ImagingRecipe.Ramp(.035,.15,l)*(1-ImagingRecipe.Ramp(.84,.98,l))*
                (1-ImagingRecipe.Ramp(.29,.55,deviation))*ImagingRecipe.Ramp(.012,.07,deviation);
            double dr=Limit((r-rr)*(l+.12),.085)*amount*.65,db=Limit((blue-bb)*(l+.12),.085)*amount*.65;
            double dg=-.2126*dr-.0722*db;
            dest[o]=Code(input[i]+65535*(dr+dg));dest[o+1]=Code(input[i+1]+65535*dg);dest[o+2]=Code(input[i+2]+65535*(db+dg));
        }

        private static double Neighbors(ushort[] pixels,byte[] selection,int i,int x,int y,int rowWidth,int height,int radius,double center)
        {
            double sum=center*2,weight=2;
            for(int dy=-1;dy<=1;dy++)for(int dx=-1;dx<=1;dx++)
            {
                if(dx==0&&dy==0)continue;
                int xx=x+dx*radius,yy=y+dy*radius;
                if(xx<0||yy<0||xx>=rowWidth||yy>=height)continue;
                int j=(yy*rowWidth+xx)*4;
                if(selection[j/4]<100||pixels[j+3]==0)continue;
                double l=Lum(pixels,j);
                double chroma=(Math.Abs((pixels[i]-pixels[i+1])-(pixels[j]-pixels[j+1]))+
                              Math.Abs((pixels[i+2]-pixels[i+1])-(pixels[j+2]-pixels[j+1])))/65535.0;
                double k=Math.Max(0,1-Math.Abs(l-center)/.18-chroma/.12);
                sum+=k*l;weight+=k;
            }
            return sum/weight;
        }
        internal static int FineRadius(int shortEdge)
        {
            return Math.Max(2,Math.Min(15,(int)Math.Round(shortEdge*.0045)));
        }
        internal static int FoldRadius(string kind,int shortEdge,double broad)
        {
            int small=FineRadius(shortEdge);
            return broad<=0?small:Math.Max(small+3,Math.Min(80,(int)Math.Round(shortEdge*(kind=="backdrop"?.036:.024))));
        }
        internal static BitmapSource Refine(BitmapSource source,string kind,double fine,double broad,double skin,
            Func<int,int,double> coverage,FaceSample face,CancellationToken cancel,int sourceShortEdge=0,SkinReference measured=null)
        {
            if(fine==0&&broad==0&&skin==0)return source;
            var src=source.Format==PixelFormats.Rgba64?source:new FormatConvertedBitmap(source,PixelFormats.Rgba64,null,0);
            bool isSkin=kind=="skin";
            if(isSkin&&face==null||!isSkin&&kind!="clothes"&&kind!="backdrop")throw new ArgumentException("选区与修整部位不符。");
            var palette=isSkin?(measured==null?SkinPalette(src,face.Bounds(src.PixelWidth,src.PixelHeight),coverage):measured.Palette):null;
            if(isSkin&&palette==null)return source;
            int w=src.PixelWidth,h=src.PixelHeight,stride=checked(w*8);
            int shortEdge=sourceShortEdge>0?sourceShortEdge:Math.Min(w,h);
            int small=FineRadius(shortEdge),large=FoldRadius(kind,shortEdge,broad);
            int halo=isSkin?0:large;
            var output=new WriteableBitmap(w,h,src.DpiX,src.DpiY,PixelFormats.Rgba64,null);
            int rows=Math.Max(8,Math.Min(96,2097152/stride));
            var options=new ParallelOptions{CancellationToken=cancel,MaxDegreeOfParallelism=Math.Min(6,Environment.ProcessorCount)};
            for(int start=0;start<h;start+=rows)
            {
                cancel.ThrowIfCancellationRequested();
                int n=Math.Min(rows,h-start),top=Math.Max(0,start-halo),end=Math.Min(h,start+n+halo);
                var input=new ushort[checked((end-top)*w*4)];var dest=new ushort[n*w*4];
                src.CopyPixels(new Int32Rect(0,top,w,end-top),input,stride,0);
                Array.Copy(input,(start-top)*w*4,dest,0,dest.Length);
                var selected=new byte[input.Length/4];
                Parallel.For(0,end-top,options,y=>{
                    for(int x=0;x<w;x++)selected[y*w+x]=(byte)Math.Round(255*Math.Max(0,Math.Min(1,coverage(x,y+top))));
                });
                Parallel.For(0,n,options,row=>{
                    int yy=start-top+row;
                    for(int x=0;x<w;x++)
                    {
                        int i=(yy*w+x)*4,o=(row*w+x)*4;
                        if(selected[i/4]==0||input[i+3]==0)continue;
                        double amount=selected[i/4]/255.0,l=Lum(input,i);
                        if(isSkin){BlendSkin(input,i,dest,o,l,amount*skin/100,palette);continue;}
                        if(x==0||x==w-1||yy==0||yy==end-top-1)continue;
                        // High-frequency edges and colour boundaries protect stitches, patterns, jewellery and hard props.
                        double ridge=Math.Max(Math.Abs(2*l-Lum(input,i-4)-Lum(input,i+4)),
                            Math.Abs(2*l-Lum(input,i-4*w)-Lum(input,i+4*w)));
                        double edge=Math.Max(Math.Abs(Lum(input,i-4)-Lum(input,i+4)),Math.Abs(Lum(input,i-4*w)-Lum(input,i+4*w)));
                        double protect=(1-ImagingRecipe.Ramp(.015,.045,ridge))*(1-ImagingRecipe.Ramp(.06,.14,edge));
                        if(protect<=0)continue;
                        double narrow=Neighbors(input,selected,i,x,yy,w,end-top,small,l);
                        double shift=(narrow-l)*fine/100.0;
                        if(broad>0)shift+=(Neighbors(input,selected,i,x,yy,w,end-top,large,l)-narrow)*broad/100.0;
                        shift=Limit(shift,.10)*65535*amount*protect*.86;
                        for(int c=0;c<3;c++)dest[o+c]=Code(input[i+c]+shift);
                    }
                });
                output.WritePixels(new Int32Rect(0,start,w,n),dest,stride,0);
            }
            output.Freeze();return output;
        }
    }
}
