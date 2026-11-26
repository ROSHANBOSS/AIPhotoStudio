using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AIPhotoStudio
{
    public static class PortraitEdits
    {
        public const string RegionGroup = "人脸区域";
        public static readonly ControlSpec[] Controls = {
            Box("x"), Box("y"), Box("w"), Box("h"),
            Slider("portrait_skin_light","肤色提亮"), Slider("portrait_skin_smooth","保留纹理磨皮"), Slider("portrait_skin_red","减淡泛红")
        };
        private static ControlSpec Box(string suffix)
        {
            return new ControlSpec { key="portrait_face_"+suffix,label=suffix,group=RegionGroup,min=0,max=1,step=.000001,initial=0 };
        }
        private static ControlSpec Slider(string key,string label)
        {
            return new ControlSpec { key=key,label=label,group="人像",min=0,max=100,step=1,initial=0 };
        }
        private static double Get(IDictionary<string,double> values,string key)
        {
            double value; return values.TryGetValue(key,out value) ? value : 0;
        }
        public static bool HasFace(IDictionary<string,double> values)
        {
            if(values==null) return false;
            double x=Get(values,"portrait_face_x"), y=Get(values,"portrait_face_y"), w=Get(values,"portrait_face_w"), h=Get(values,"portrait_face_h");
            return x>=0 && y>=0 && w>0 && h>0 && x+w<=1.0000001 && y+h<=1.0000001;
        }
        private static double Ramp(double t)
        {
            t=Math.Min(1,Math.Max(0,t)); return t*t*(3-2*t);
        }
        private static double Skin(double r,double g,double b)
        {
            double y=.299*r+.587*g+.114*b, cb=.5+.564*(b-y), cr=.5+.713*(r-y);
            // Broad YCbCr gates fade continuously across complexion and lighting.
            return Ramp((cb-.24)/.07)*Ramp((.565-cb)/.065)*Ramp((cr-.49)/.04)*Ramp((.79-cr)/.09)*
                Ramp((y-.028)/.095)*Ramp((.995-y)/.07);
        }
        private static ushort Encode(double v) { return (ushort)Math.Round(Math.Max(0,Math.Min(1,v))*65535); }

        public static BitmapSource Apply(BitmapSource orientedGlobal, IDictionary<string,double> values, CancellationToken cancel)
        {
            if(values==null) return orientedGlobal;
            double light=Get(values,"portrait_skin_light")/100, smooth=Get(values,"portrait_skin_smooth")/100, red=Get(values,"portrait_skin_red")/100;
            if (light==0 && smooth==0 && red==0) return orientedGlobal;
            if (!HasFace(values)) throw new InvalidOperationException("请先识别并选择这张照片中的人脸。");
            double left=Get(values,"portrait_face_x"), top=Get(values,"portrait_face_y"), fw=Get(values,"portrait_face_w"), fh=Get(values,"portrait_face_h");
            BitmapSource input=orientedGlobal;
            if (input.Format!=PixelFormats.Rgba64)
            {
                var convert=new FormatConvertedBitmap(input,PixelFormats.Rgba64,null,0); convert.Freeze(); input=convert;
            }
            int width=input.PixelWidth, height=input.PixelHeight, stride=checked(width*8);
            var scene=PortraitAssets.Resolve(values);
            var keep=scene!=null&&scene.KeepMarks.Length>0?new PortraitKeep.Field(scene.KeepMarks,(double)width/height):null;
            int x0=Math.Max(0,(int)Math.Floor(left*width)), x1=Math.Min(width,(int)Math.Ceiling((left+fw)*width));
            int y0=Math.Max(0,(int)Math.Floor(top*height)), y1=Math.Min(height,(int)Math.Ceiling((top+fh)*height));
            int radius=(int)Math.Max(1,Math.Min(6,Math.Round(Math.Min(fw*width,fh*height)/180)));
            var output=new WriteableBitmap(width,height,input.DpiX,input.DpiY,PixelFormats.Rgba64,null);
            int chunk=Math.Max(1,Math.Min(96,2*1024*1024/stride));
            for (int y=0;y<height;y+=chunk)
            {
                cancel.ThrowIfCancellationRequested();
                int rows=Math.Min(chunk,height-y), first=Math.Max(0,y-radius), last=Math.Min(height,y+rows+radius);
                ushort[] source=new ushort[checked((last-first)*width*4)];
                input.CopyPixels(new Int32Rect(0,first,width,last-first),source,stride,0);
                ushort[] target=new ushort[checked(rows*width*4)];
                Array.Copy(source,(y-first)*width*4,target,0,target.Length);
                for (int py=Math.Max(y,y0);py<Math.Min(y+rows,y1);py++)
                    for (int px=x0;px<x1;px++)
                    {
                        double nx=(((px+.5)/width-left)/fw-.5)*2, ny=(((py+.5)/height-top)/fh-.5)*2;
                        double ellipse=Ramp((1-nx*nx-ny*ny)/.31);
                        if (ellipse==0) continue;
                        int at=((py-first)*width+px)*4, dest=((py-y)*width+px)*4;
                        if (source[at+3]==0) continue;
                        double r=source[at]/65535.0,g=source[at+1]/65535.0,b=source[at+2]/65535.0;
                        double mask=ellipse*Skin(r,g,b);
                        if(keep!=null&&mask>0)mask*=keep.SkinWeight((px+.5)/width,(py+.5)/height);
                        if (mask<.00001) continue;
                        double lum=.299*r+.587*g+.114*b;
                        if (smooth>0)
                        {
                            double weights=1, average=lum;
                            for (int oy=-1;oy<=1;oy++)
                                for (int ox=-1;ox<=1;ox++)
                                {
                                    if (ox==0 && oy==0) continue;
                                    int sy=Math.Max(0,Math.Min(height-1,py+oy*radius)), sx=Math.Max(0,Math.Min(width-1,px+ox*radius));
                                    int pos=((sy-first)*width+sx)*4;
                                    double nr=source[pos]/65535.0,ng=source[pos+1]/65535.0,nb=source[pos+2]/65535.0;
                                    double nl=.299*nr+.587*ng+.114*nb;
                                    double gap=Math.Abs(nl-lum)+.45*(Math.Abs((nr-ng)-(r-g))+Math.Abs((nb-ng)-(b-g)));
                                    double weight=Ramp(1-gap/.11)*(ox!=0 && oy!=0 ? .6 : 1);
                                    weights+=weight; average+=nl*weight;
                                }
                            // Retain at least 55% of local luminance texture at maximum strength.
                            double delta=(average/weights-lum)*.45*smooth*mask;
                            r+=delta; g+=delta; b+=delta;
                        }
                        double redness=Math.Max(0,r-g-.035)*.42*red*mask;
                        double lift=(1-lum)*.105*light*mask;
                        target[dest]=Encode(r+lift-redness);
                        target[dest+1]=Encode(g+lift+redness*.32);
                        target[dest+2]=Encode(b+lift+redness*.13);
                    }
                output.WritePixels(new Int32Rect(0,y,width,rows),target,stride,0);
            }
            output.Freeze(); return output;
        }
    }
}
