using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AIPhotoStudio
{
    public static partial class Imaging
    {
        internal static LocalEdits.AreaMark[] SamplingRedactions(string path,Dictionary<string,double> values)
        {
            if(LocalEdits.Hash(values)==null)return null;
            var masks=LocalEdits.ReadSet(values);
            LocalEdits.ValidateSource(masks,path);
            return masks.areas!=null&&masks.areas.Any(mark=>mark.kind=="redactRect")?masks.areas:null;
        }

        internal static BitmapSource ConcealedRangeInput(string path,Dictionary<string,double> neutral,
            LocalEdits.AreaMark[] areas,CancellationToken token)
        {
            return LocalEdits.CloseRedactions(RenderGlobal(path,neutral,768,token,areas),areas,null,token);
        }

        // Picker and overlay use this same oriented surface. Spatial retouch, an existing HSL
        // edit, LUTs and grading come after these colors in the production recipe.
        internal sealed class ColorBandInput
        {
            internal readonly BitmapSource Source;
            private readonly FrameGeometry frame;
            internal ColorBandInput(string path,Dictionary<string,double> values,CancellationToken token)
            {
                var areas=SamplingRedactions(path,values);
                Source=LocalEdits.CloseRedactions(RenderGlobal(path,values,1024,token,areas,true),areas,null,token);
                if(FrameGeometry.Active(values))frame=FrameGeometry.ForFile(path,values);
            }
            internal BitmapSource View(BitmapSource image,bool composed,CancellationToken token)
            {
                return composed&&frame!=null?frame.Apply(image,1024,token):image;
            }
            private static ImagingRecipe.HueBands Measure(ushort[] pixels,int p)
            {
                return ImagingRecipe.MeasureHue(pixels[p]/65535.0,pixels[p+1]/65535.0,pixels[p+2]/65535.0);
            }
            internal BitmapSource Sample(int x,int y,bool composed,CancellationToken token,out string band,out double strength)
            {
                var pixel=new ushort[4];
                Source.CopyPixels(new Int32Rect(x,y,1,1),pixel,8,0);
                var picked=Measure(pixel,0);
                int index=picked.Strongest;
                strength=picked.Weight(index)*pixel[3]/65535.0;
                if(strength<=0){band=null;return null;}
                band=Bands[index];
                int w=Source.PixelWidth,h=Source.PixelHeight;
                var layer=new WriteableBitmap(w,h,96,96,PixelFormats.Rgba64,null);
                var row=new ushort[w*4];
                for(int iy=0;iy<h;iy++)
                {
                    token.ThrowIfCancellationRequested();
                    var area=new Int32Rect(0,iy,w,1);
                    Source.CopyPixels(area,row,w*8,0);
                    for(int i=0;i<row.Length;i+=4)
                    {
                        double weight=Measure(row,i).Weight(index);
                        row[i+3]=(ushort)Math.Round(weight*row[i+3]);
                        row[i]=65535;row[i+1]=48830;row[i+2]=11308;
                    }
                    layer.WritePixels(area,row,w*8,0);
                }
                layer.Freeze();
                return View(layer,composed,token);
            }
        }
    }
}
