using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AIPhotoStudio
{
    internal sealed class ExportCanvas
    {
        internal int Width,Height;
        internal bool Pad;
        internal double X,Y;
        internal string Background;

        internal static double Fraction(Dictionary<string,object> args,string key)
        {
            if(!args.ContainsKey(key))return .5;
            double value=StudioWorkspace.Number(args[key]);
            if(!(value>=0 && value<=1))throw new ArgumentException("成品位置超出图像范围。");
            return value;
        }

        internal static ExportCanvas Read(Dictionary<string,object> args)
        {
            string mode=StudioBridge.TextOf(args,"layout");
            if(String.IsNullOrEmpty(mode))return null;
            if(mode!="crop" && mode!="pad")throw new ArgumentException("请选择裁切填满或完整加边。");
            int w=StudioBridge.ExportSettings.ReadInteger(args,"outputWidth",0,1,65535);
            int h=StudioBridge.ExportSettings.ReadInteger(args,"outputHeight",0,1,65535);
            if(w<1 || h<1 || (long)w*h>80000000)throw new ArgumentException("请填写有效宽高，总像素须在 8000 万以内。");
            string background=StudioBridge.TextOf(args,"background");
            if(String.IsNullOrEmpty(background))background="transparent";
            if(background!="transparent" && !System.Text.RegularExpressions.Regex.IsMatch(background,@"\A#[a-fA-F0-9]{6}\z"))
                throw new ArgumentException("边框底色无效。");
            return new ExportCanvas{Width=w,Height=h,X=Fraction(args,"positionX"),Y=Fraction(args,"positionY"),Pad=mode=="pad",Background=background};
        }
        private static int At(double value){return (int)Math.Round(value,MidpointRounding.AwayFromZero);}

        internal BitmapSource Apply(BitmapSource source,CancellationToken cancel)
        {
            int sw=source.PixelWidth,sh=source.PixelHeight;
            if(!Pad){
                double scale=Math.Max((double)Width/sw,(double)Height/sh);
                int w=Math.Max(1,Math.Min(sw,At(Width/scale))),h=Math.Max(1,Math.Min(sh,At(Height/scale)));
                var crop=new CroppedBitmap(source,new Int32Rect(At((sw-w)*X),At((sh-h)*Y),w,h));
                return Imaging.SizeOutput(crop,Width,Height);
            }
            double fit=Math.Min((double)Width/sw,(double)Height/sh);
            int iw=Math.Max(1,Math.Min(Width,At(sw*fit))),ih=Math.Max(1,Math.Min(Height,At(sh*fit)));
            int left=At((Width-iw)*X),top=At((Height-ih)*Y);
            var photo=Imaging.SizeOutput(source,iw,ih);
            var result=new WriteableBitmap(Width,Height,96,96,PixelFormats.Rgba64,null);
            // Preserve all alpha on transparent paper. Solid paper also shows through translucent source pixels.
            var fill=new ushort[4];
            bool solid=Background!="transparent";
            if(solid){
                int color=Int32.Parse(Background.Substring(1),NumberStyles.HexNumber,CultureInfo.InvariantCulture);
                fill=new[]{(ushort)((color>>16)*257),(ushort)(((color>>8)&255)*257),(ushort)((color&255)*257),UInt16.MaxValue};
            }
            int rows=Math.Max(1,Math.Min(64,1048576/(Width*8)));
            var block=new ushort[Width*rows*4];var pixels=new ushort[iw*rows*4];
            for(int y=0;y<Height;y+=rows){
                cancel.ThrowIfCancellationRequested();
                int count=Math.Min(rows,Height-y);
                if(solid)for(int p=0;p<Width*count*4;p+=4){block[p]=fill[0];block[p+1]=fill[1];block[p+2]=fill[2];block[p+3]=fill[3];}
                else Array.Clear(block,0,Width*count*4);
                int y0=Math.Max(y,top),y1=Math.Min(y+count,top+ih);
                if(y0<y1){
                    photo.CopyPixels(new Int32Rect(0,y0-top,iw,y1-y0),pixels,iw*8,0);
                    for(int row=0;row<y1-y0;row++){
                        int dest=((y0-y+row)*Width+left)*4;
                        Array.Copy(pixels,row*iw*4,block,dest,iw*4);
                        if(solid)for(int p=dest;p<dest+iw*4;p+=4){
                            double alpha=block[p+3]/65535.0;
                            if(alpha<1)for(int c=0;c<3;c++)block[p+c]=(ushort)(65535*ImagingRecipe.EncodeBounded(alpha*ImagingRecipe.Linear[block[p+c]]+(1-alpha)*ImagingRecipe.Linear[fill[c]])+.5);
                            block[p+3]=65535;
                        }
                    }
                }
                result.WritePixels(new Int32Rect(0,y,Width,count),block,Width*8,0);
            }
            result.Freeze();return result;
        }
    }
}
