using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AIPhotoStudio
{
    public static partial class LocalEdits
    {
        internal static bool RangeKind(string kind) { return kind=="colorRange"||kind=="luminanceRange"; }

        internal static bool NewColorKey(string key)
        {
            return key=="local_temperature" || ExtraKey(key)&&(key.EndsWith("_temperature",StringComparison.Ordinal)||key.EndsWith("_saturation",StringComparison.Ordinal));
        }

        internal sealed class RangeSettings
        {
            public int red {get;set;}
            public int green {get;set;}
            public int blue {get;set;}
            public int tolerance {get;set;}
            public int low {get;set;}
            public int high {get;set;}
            public int feather {get;set;}

            internal void Check(string kind)
            {
                if(!RangeKind(kind)||red<0||red>255||green<0||green>255||blue<0||blue>255||
                    tolerance<0||tolerance>100||low<0||high>100||low>high||feather<0||feather>100)
                    throw new InvalidDataException("取样颜色、亮度上下限或范围羽化无效。");
            }
        }

        internal static RangeSettings ReadRange(string kind,object raw)
        {
            var fields=raw as IDictionary<string,object>;
            string[] names={"red","green","blue","tolerance","low","high","feather"};
            if(fields==null||fields.Count!=names.Length||names.Any(k=>!fields.ContainsKey(k)||!(fields[k] is int)))
                throw new InvalidDataException("范围蒙版需要完整的整数取样和范围设置。");
            var range=MaskJson().ConvertToType<RangeSettings>(raw);
            range.Check(kind);
            return range;
        }

        // All comparisons use the oriented, neutral sRGB source, independently of
        // earlier layers and of the adjustment currently being previewed.
        private sealed class RangeField
        {
            private readonly bool color;
            private readonly double red,green,blue,radius,inner,outer,low,high,feather;
            internal RangeField(string kind,RangeSettings range)
            {
                color=kind=="colorRange";
                red=range.red*257;green=range.green*257;blue=range.blue*257;
                // A zero tolerance still includes the original 16-bit pixel rounded to the sampled 8-bit swatch.
                radius=Math.Max(.5/255,range.tolerance*.01);
                inner=radius*radius;
                feather=range.feather*.01;
                outer=(radius+feather)*(radius+feather);
                low=range.low*.01;high=range.high*.01;
            }
            internal double At(ushort[] rgba,int index)
            {
                if(rgba[index+3]==0)return 0;
                double distance;
                if(color)
                {
                    double dr=rgba[index]-red,dg=rgba[index+1]-green,db=rgba[index+2]-blue;
                    distance=(dr*dr+dg*dg+db*db)/(3.0*65535*65535);
                    if(distance<=inner)return 1;
                    if(distance>=outer)return 0;
                    return 1-ImagingRecipe.Ramp(radius,radius+feather,Math.Sqrt(distance));
                }
                double level=ImagingRecipe.Luma(rgba[index],rgba[index+1],rgba[index+2])/65535;
                distance=Math.Max(low-level,level-high);
                if(distance<=0)return 1;
                return distance>=feather?0:1-ImagingRecipe.Ramp(0,feather,distance);
            }
        }

        internal sealed class RangeSampler
        {
            private readonly int width,height;
            private readonly ushort[] rgba;

            internal RangeSampler(BitmapSource reference)
            {
                width=reference.PixelWidth;height=reference.PixelHeight;
                var surface=reference.Format==PixelFormats.Rgba64?reference:new FormatConvertedBitmap(reference,PixelFormats.Rgba64,null,0);
                rgba=new ushort[checked(width*height*4)];
                surface.CopyPixels(rgba,width*8,0);
            }
            private int Index(double x,double y)
            {
                return 4*((int)Math.Min(height-1,y*height)*width+(int)Math.Min(width-1,x*width));
            }
            internal void Pick(string kind,RangeSettings range,double x,double y)
            {
                if(!(x>=0&&x<=1&&y>=0&&y<=1))throw new ArgumentException("请在照片范围内取样。");
                int index=Index(x,y);
                if(rgba[index+3]==0)throw new InvalidOperationException("此处完全透明，请点击可见的照片像素。");
                range.red=(rgba[index]+128)/257;range.green=(rgba[index+1]+128)/257;range.blue=(rgba[index+2]+128)/257;
                if(kind=="luminanceRange")
                {
                    int level=(int)Math.Round(ImagingRecipe.Luma(rgba[index],rgba[index+1],rgba[index+2])*100/65535);
                    int width=range.high-range.low,low=Math.Max(0,Math.Min(100-width,level-width/2));
                    range.low=low;range.high=low+width;
                }
            }
            internal string Preview(string kind,RangeSettings range,out int covered)
            {
                var field=new RangeField(kind,range);
                var alpha=new byte[PredictionSide*PredictionSide];
                covered=0;
                // Average the pixels belonging to each guide cell so small or disconnected features
                // survive even when the guide is coarser than the sampler. Export evaluates RangeField directly.
                for(int y=0;y<PredictionSide;y++)
                {
                    int top=y*height/PredictionSide,bottom=Math.Max(top+1,(y+1)*height/PredictionSide);
                    for(int x=0;x<PredictionSide;x++)
                    {
                        int left=x*width/PredictionSide,right=Math.Max(left+1,(x+1)*width/PredictionSide);
                        double sum=0;
                        for(int sy=top;sy<bottom;sy++)for(int sx=left;sx<right;sx++)
                            sum+=field.At(rgba,4*(sy*width+sx));
                        if(sum<=0)continue;
                        alpha[y*PredictionSide+x]=(byte)Math.Max(1,Math.Round(255*sum/((bottom-top)*(right-left))));
                        covered++;
                    }
                }
                return Convert.ToBase64String(alpha);
            }
        }
        internal static bool NeedsRangeReference(MaskSet set,IDictionary<string,double> values)
        {
            return set!=null&&set.layers.Any(layer=>RangeKind(layer.kind)&&ToneNames.Any(key=>Val(values,ToneKey(layer.slot,key),0)!=0));
        }
    }
}
