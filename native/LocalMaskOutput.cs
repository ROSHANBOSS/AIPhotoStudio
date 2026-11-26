using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AIPhotoStudio
{
    public static partial class LocalEdits
    {
        internal static bool StrengthKey(string key)
        {
            return key=="local_strength" || ExtraKey(key)&&key.EndsWith("_strength",StringComparison.Ordinal);
        }
        internal static bool BackdropKey(string key)
        {
            return ExtraKey(key)&&key.EndsWith("_backgroundOutput",StringComparison.Ordinal);
        }
        internal static bool LayerOutputKey(string key) { return StrengthKey(key)||BackdropKey(key); }
        internal static double LayerDefault(string key) { return StrengthKey(key)?100:0; }
        internal static double Strength(IDictionary<string,double> values,int slot){return Val(values,ToneKey(slot,"strength"),100);}

        private static Func<int,int,double> BackdropCoverage(MaskLayer layer,int width,int height,ToneRegion region,CancellationToken cancel)
        {
            if(layer.kind!="backdrop")throw new ArgumentException("请先选择背景蒙版。");
            int ox=region==null?0:region.Area.X,oy=region==null?0:region.Area.Y;
            var alpha=Convert.FromBase64String(layer.alpha);
            var brush=layer.strokes.Length==0?null:new DetailBrush(layer.strokes,width,height,cancel);
            return (x,y)=>{
                int gx=x+ox,gy=y+oy;
                double weight=SamplePrediction(alpha,(gx+.5)/width,(gy+.5)/height);
                return brush==null?weight:brush.At(gx,gy,weight);
            };
        }

        private static BitmapSource LayerStrength(BitmapSource before,BitmapSource after,double strength,CancellationToken cancel)
        {
            if(strength==100||before==after)return after;
            int w=after.PixelWidth,h=after.PixelHeight;
            int band=Math.Min(h,Math.Max(1,Math.Min(64,131072/w)));
            var a=new ushort[band*w*4];var b=new ushort[a.Length];
            var output=new WriteableBitmap(w,h,after.DpiX,after.DpiY,PixelFormats.Rgba64,null);
            double amount=strength/100;
            for(int y=0;y<h;y+=band){
                cancel.ThrowIfCancellationRequested();
                var rect=new Int32Rect(0,y,w,Math.Min(band,h-y));
                before.CopyPixels(rect,a,w*8,0);after.CopyPixels(rect,b,w*8,0);
                for(int i=0;i<rect.Height*w*4;i+=4)for(int c=i;c<i+3;c++)
                    b[c]=(ushort)Math.Round(a[c]+(b[c]-a[c])*amount);
                output.WritePixels(rect,b,w*8,0);
            }
            output.Freeze();return output;
        }

        // 0 retains the photograph, 1 creates transparency, and 2 + RGB encodes a solid sRGB background.
        private static BitmapSource ReplaceBackdrop(BitmapSource source,Func<int,int,double> background,int setting,double strength,CancellationToken cancel)
        {
            int w=source.PixelWidth,h=source.PixelHeight,band=Math.Min(h,Math.Max(1,Math.Min(64,131072/w)));
            int color=setting-2;
            var rgb=new[]{(ushort)(((color>>16)&255)*257),(ushort)(((color>>8)&255)*257),(ushort)((color&255)*257)};
            var data=new ushort[w*band*4];
            var output=new WriteableBitmap(w,h,source.DpiX,source.DpiY,PixelFormats.Rgba64,null);
            for(int y=0;y<h;y+=band){
                cancel.ThrowIfCancellationRequested();
                var rect=new Int32Rect(0,y,w,Math.Min(h-y,band));
                source.CopyPixels(rect,data,w*8,0);
                for(int row=0;row<rect.Height;row++)for(int x=0;x<w;x++){
                    int i=(row*w+x)*4;
                    ushort originalAlpha=data[i+3];
                    double keep=originalAlpha*(1-strength*Clamp(background(x,y+row),0,1));
                    ushort a=(ushort)Math.Round(keep);
                    if(setting==1){
                        data[i+3]=a;
                        if(a==0)data[i]=data[i+1]=data[i+2]=0;
                    }else{
                        double total=originalAlpha+strength*(65535-originalAlpha);
                        data[i+3]=(ushort)Math.Round(total);
                        if(keep==total)continue;
                        double foreground=keep/total;
                        for(int c=0;c<3;c++)data[i+c]=keep==0?rgb[c]:(ushort)Math.Round(65535*ImagingRecipe.EncodeBounded(
                            foreground*ImagingRecipe.Linear[data[i+c]]+(1-foreground)*ImagingRecipe.Linear[rgb[c]]));
                    }
                }
                output.WritePixels(rect,data,w*8,0);
            }
            output.Freeze();return output;
        }
    }
}
