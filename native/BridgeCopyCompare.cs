using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AIPhotoStudio
{
    internal sealed partial class StudioBridge
    {
        private async Task<object> CompareSource(Dictionary<string,object> args,bool detail)
        {
            string id=TextOf(args,"id");long revision=Convert.ToInt64(args["revision"]);
            var values=work.LibraryValues(id,revision);
            await thumbnailSlot.WaitAsync(closing.Token);
            try{return await Task.Run<object>(delegate{
                var token=closing.Token;string path=work.PathFor(id);
                using(ImagingInput.OpenRead(path))
                {
                    var neutral=CameraRawModes.Neutral(values);var areas=Imaging.SamplingRedactions(path,values);
                    var info=work.CopyPhotoInfo(id);
                    int width=Convert.ToInt32(info["width"]),height=Convert.ToInt32(info["height"]);
                    BitmapSource image;
                    if(detail)
                    {
                        double x=Convert.ToDouble(args["x"]),y=Convert.ToDouble(args["y"]);
                        if(!(x>=0&&x<=1&&y>=0&&y<=1))throw new ArgumentException("请点在照片内。");
                        int w=Math.Min(112,width),h=Math.Min(112,height);
                        var rect=new Int32Rect(Math.Max(0,Math.Min(width-w,(int)Math.Round(x*(width-1))-w/2)),Math.Max(0,Math.Min(height-h,(int)Math.Round(y*(height-1))-h/2)),w,h);
                        string kind=ImagingInput.TypeFromExtension(path);
                        var camera=kind=="DNG"||VendorRaw.Supports(kind)?Imaging.LoadCameraViewport(path,CameraRawModes.Selected(neutral),token):null;
                        image=Imaging.ReadEditedViewport(path,neutral,rect,token,camera);
                        if(areas!=null)image=LocalEdits.CloseRedactions(image,areas,new LocalEdits.ToneRegion(width,height,rect),token);
                        work.VerifyLibraryPhoto(id,revision);return EncodeFrame(image,false,token);
                    }
                    image=areas==null?Imaging.Render(path,neutral,160,token):Imaging.ConcealedRangeInput(path,neutral,areas,token);
                    if(Math.Max(image.PixelWidth,image.PixelHeight)>160)
                    {
                        double factor=160d/Math.Max(image.PixelWidth,image.PixelHeight);
                        image=new TransformedBitmap(image,new ScaleTransform(factor,factor));
                    }
                    int iw=image.PixelWidth,ih=image.PixelHeight;var small=new FormatConvertedBitmap(image,PixelFormats.Bgra32,null,0);
                    var px=new byte[iw*ih*4];small.CopyPixels(px,iw*4,0);
                    const int n=32;var sig=new byte[n*n*3];
                    for(int cy=0;cy<n;cy++)for(int cx=0;cx<n;cx++)
                    {
                        int x0=cx*iw/n,y0=cy*ih/n,x1=Math.Max(x0+1,(cx+1)*iw/n),y1=Math.Max(y0+1,(cy+1)*ih/n);
                        for(int c=0;c<3;c++){
                            int sum=0;
                            for(int y=y0;y<y1;y++)for(int x=x0;x<x1;x++){int p=(y*iw+x)*4;sum+=(px[p+c]*px[p+3]+127*(255-px[p+3])+127)/255;}
                            sig[(cy*n+cx)*3+c]=(byte)(sum/((x1-x0)*(y1-y0)));
                        }
                    }
                    work.VerifyLibraryPhoto(id,revision);
                    return new{signature=Convert.ToBase64String(sig),image=EncodeFrame(image,false,token)["image"],source=info["hash"],width,height};
                }
            },closing.Token);}
            finally{thumbnailSlot.Release();}
        }
    }
    public sealed partial class StudioWorkspace
    {
        internal Dictionary<string,object> CopyPhotoInfo(string id)
        {
            lock(gate){var record=SourceRecord(id);var info=record.Info;info["hash"]=record.Hash;return info;}
        }
    }
}
