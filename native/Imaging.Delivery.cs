using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AIPhotoStudio
{
    public static partial class Imaging
    {
        internal static readonly string AdobeRgbProfile=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"spool","drivers","color","AdobeRGB1998.icc");

        private static ColorContext OutputProfile(string name)
        {
            if(name=="srgb")return new ColorContext(PixelFormats.Bgra32);
            if(name!="adobeRgb")throw new ArgumentException("未知的导出色彩空间。");
            if(!File.Exists(AdobeRgbProfile))throw new FileNotFoundException("本机缺少 Adobe RGB (1998) ICC，请在预设中选用 sRGB。");
            return new ColorContext(new Uri(AdobeRgbProfile));
        }

        private static BitmapSource ConvertOutput(BitmapSource input,ColorContext profile,bool jpeg,CancellationToken cancel)
        {
            var srgb=new ColorContext(PixelFormats.Bgra32);
            if(jpeg)return new ColorConvertedBitmap(input,srgb,profile,PixelFormats.Rgb24);
            // WIC transforms the three color channels. Copy the straight 16-bit alpha separately.
            var rgba=AsFormat(input,PixelFormats.Rgba64);
            var converted=new ColorConvertedBitmap(rgba,srgb,profile,PixelFormats.Rgba64);
            int w=rgba.PixelWidth,h=rgba.PixelHeight,rows=Math.Min(h,Math.Max(1,Math.Min(64,131072/w)));
            var result=new WriteableBitmap(w,h,rgba.DpiX,rgba.DpiY,PixelFormats.Rgba64,null);
            var output=new ushort[w*4*rows];var source=new ushort[output.Length];
            for(int y=0;y<h;y+=rows)
            {
                cancel.ThrowIfCancellationRequested();
                var rect=new Int32Rect(0,y,w,Math.Min(rows,h-y));
                converted.CopyPixels(rect,output,w*8,0);
                rgba.CopyPixels(rect,source,w*8,0);
                for(int i=3;i<rect.Height*w*4;i+=4)output[i]=source[i];
                result.WritePixels(rect,output,w*8,0);
            }
            result.Freeze();return result;
        }

        private static BitmapSource StampOutput(BitmapSource photo,DeliveryMark mark,CancellationToken cancel)
        {
            int w=photo.PixelWidth,h=photo.PixelHeight,shortEdge=Math.Min(w,h);
            string position=w==h?mark.square:w>h?mark.landscape:mark.portrait;
            int margin=Math.Min(shortEdge/5,(int)Math.Round(shortEdge*.035));
            if(mark.kind=="png")return StampLogo(photo,mark.picture.Value,mark,position,margin,cancel);
            var text=new FormattedText(mark.text,CultureInfo.GetCultureInfo("zh-CN"),FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Microsoft YaHei UI"),FontStyles.Normal,FontWeights.SemiBold,FontStretches.Normal),100,Brushes.White,1);
            var glyph=text.BuildGeometry(new Point());
            var outline=new Pen(Brushes.Black,3){LineJoin=PenLineJoin.Round};
            Rect box=glyph.GetRenderBounds(outline);
            if(box.IsEmpty || box.Width<=0 || box.Height<=0)throw new ArgumentException("水印须包含可显示的文字。");
            double scale=Math.Min((w-2*margin)/box.Width,Math.Min((h-2*margin)/box.Height,Math.Max(1,shortEdge*mark.size/100.0)/box.Height));
            int mw=Math.Max(1,Math.Min(w-2*margin,(int)Math.Ceiling(box.Width*scale)));
            int mh=Math.Max(1,Math.Min(h-2*margin,(int)Math.Ceiling(box.Height*scale)));
            var drawing=new DrawingVisual();
            using(var dc=drawing.RenderOpen())
            {
                dc.PushTransform(new MatrixTransform(scale,0,0,scale,-box.X*scale,-box.Y*scale));
                dc.DrawGeometry(Brushes.White,outline,glyph);
                dc.Pop();
            }
            var mask=new RenderTargetBitmap(mw,mh,96,96,PixelFormats.Pbgra32);
            mask.Render(drawing);
            var coverage=new byte[mw*mh*4];mask.CopyPixels(coverage,mw*4,0);
            var area=WatermarkArea(w,h,mw,mh,margin,position);
            var result=new WriteableBitmap(AsFormat(photo,PixelFormats.Rgba64));
            var row=new ushort[mw*4];
            for(int y=0;y<mh;y++)
            {
                cancel.ThrowIfCancellationRequested();
                var rect=new Int32Rect(area.X,area.Y+y,mw,1);result.CopyPixels(rect,row,mw*8,0);
                for(int x=0;x<mw;x++)
                {
                    int i=x*4,j=(y*mw+x)*4,alpha=coverage[j+3];
                    if(alpha==0)continue;
                    double fg=alpha*mark.opacity/25500.0, bg=(1-fg)*row[i+3]/65535.0, total=fg+bg;
                    int grey=Math.Min(65535,(coverage[j]*65535+alpha/2)/alpha);
                    for(int c=0;c<3;c++)row[i+c]=ImagingRecipe.U16(ImagingRecipe.EncodeBounded((ImagingRecipe.Linear[grey]*fg+ImagingRecipe.Linear[row[i+c]]*bg)/total));
                    row[i+3]=ImagingRecipe.U16(total);
                }
                result.WritePixels(rect,row,mw*8,0);
            }
            result.Freeze();return result;
        }

        private static Int32Rect WatermarkArea(int width,int height,int w,int h,int margin,string position)
        {
            int left=position=="center"?(width-w)/2:position.EndsWith("Right",StringComparison.Ordinal)?width-margin-w:margin;
            int top=position=="center"?(height-h)/2:position.StartsWith("bottom",StringComparison.Ordinal)?height-margin-h:margin;
            return new Int32Rect(left,top,w,h);
        }

        private static BitmapSource StampLogo(BitmapSource photo,BitmapSource logo,DeliveryMark mark,string position,int margin,CancellationToken cancel)
        {
            int width=photo.PixelWidth,height=photo.PixelHeight;
            double requested=Math.Max(1,Math.Min(width,height)*mark.size/100.0);
            double scale=Math.Min((width-2.0*margin)/logo.PixelWidth,Math.Min(height-2.0*margin,requested)/logo.PixelHeight);
            int w=Math.Max(1,(int)Math.Round(logo.PixelWidth*scale)),h=Math.Max(1,(int)Math.Round(logo.PixelHeight*scale));
            var area=WatermarkArea(width,height,w,h,margin,position);
            // Resize premultiplied channels to avoid bleeding hidden RGB from transparent edges.
            var layer=SizeOutput(logo,w,h);
            var result=new WriteableBitmap(AsFormat(photo,PixelFormats.Rgba64));
            var foreground=new ushort[w*4];var background=new ushort[w*4];
            for(int y=0;y<h;y++){
                cancel.ThrowIfCancellationRequested();
                var rect=new Int32Rect(area.X,area.Y+y,w,1);
                layer.CopyPixels(new Int32Rect(0,y,w,1),foreground,w*8,0);result.CopyPixels(rect,background,w*8,0);
                for(int i=0;i<w*4;i+=4){
                    if(foreground[i+3]==0)continue;
                    double fg=foreground[i+3]*mark.opacity/6553500.0,bg=(1-fg)*background[i+3]/65535.0,total=fg+bg;
                    for(int c=0;c<3;c++)background[i+c]=bg==0?foreground[i+c]:ImagingRecipe.U16(ImagingRecipe.EncodeBounded(
                        (ImagingRecipe.Linear[foreground[i+c]]*fg+ImagingRecipe.Linear[background[i+c]]*bg)/total));
                    background[i+3]=ImagingRecipe.U16(total);
                }
                result.WritePixels(rect,background,w*8,0);
            }
            result.Freeze();return result;
        }
    }
}
