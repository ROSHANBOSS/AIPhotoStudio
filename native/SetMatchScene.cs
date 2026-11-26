using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AIPhotoStudio
{
    // Compare neutral captures. A reference grade must not make unrelated photographs look related.
    internal sealed class SetMatchScene
    {
        private const int Side=10;
        private readonly int width,height;
        private readonly byte[] pixels;
        internal readonly int? Faces;

        internal SetMatchScene(BitmapSource frame,int? faces)
        {
            var rgb=new FormatConvertedBitmap(frame,PixelFormats.Bgra32,null,0);
            width=rgb.PixelWidth;height=rgb.PixelHeight;Faces=faces;
            pixels=new byte[width*height*4];rgb.CopyPixels(pixels,width*4,0);
        }

        // Each cell contains luminance and two chroma opponents. Margins allow mildly different crops.
        private double[][] Grid(double spanX,double spanY,double shiftX,double shiftY)
        {
            var cells=new double[Side*Side][];
            for(int y=0;y<Side;y++)for(int x=0;x<Side;x++)
            {
                double left=(1-spanX)*shiftX,top=(1-spanY)*shiftY;
                int x0=(int)(width*(left+spanX*x/Side)),y0=(int)(height*(top+spanY*y/Side));
                int x1=Math.Min(width,Math.Max(x0+1,(int)(width*(left+spanX*(x+1)/Side))));
                int y1=Math.Min(height,Math.Max(y0+1,(int)(height*(top+spanY*(y+1)/Side))));
                double r=0,g=0,b=0,w=0;
                for(int j=y0;j<y1;j++)for(int i=x0;i<x1;i++)
                {
                    int p=(j*width+i)*4,a=pixels[p+3];
                    r+=pixels[p+2]*a;g+=pixels[p+1]*a;b+=pixels[p]*a;w+=a;
                }
                if(w<128*(x1-x0)*(y1-y0))continue;
                w*=255;r/=w;g/=w;b/=w;
                cells[y*Side+x]=new[]{.2126*r+.7152*g+.0722*b,r-b,g-(r+b)/2};
            }
            return cells;
        }

        internal sealed class Comparison
        {
            internal double Layout,ColorSpread;
            internal bool Enough;
            internal readonly List<string> Reasons=new List<string>();
        }

        private static Comparison CompareCells(double[][] left,double[][] right)
        {
            double x=0,y=0,xx=0,yy=0,xy=0;
            var warm=new List<double>();var tint=new List<double>();
            for(int i=0;i<left.Length;i++)
            {
                if(left[i]==null||right[i]==null)continue;
                double a=left[i][0],b=right[i][0];
                x+=a;y+=b;xx+=a*a;yy+=b*b;xy+=a*b;
                warm.Add(left[i][1]-right[i][1]);tint.Add(left[i][2]-right[i][2]);
            }
            int count=warm.Count;
            if(count<Side*Side*.6)return new Comparison();
            double vx=xx-x*x/count,vy=yy-y*y/count;
            if(Math.Min(vx,vy)<count*.018*.018)return new Comparison();
            warm.Sort();tint.Sort();int low=count/6,high=count-1-low;
            return new Comparison{Enough=true,Layout=(xy-x*y/count)/Math.Sqrt(vx*vy),
                ColorSpread=Math.Max(warm[high]-warm[low],tint[high]-tint[low])};
        }

        internal Comparison Compare(SetMatchScene other)
        {
            var best=CompareCells(Grid(1,1,.5,.5),other.Grid(1,1,.5,.5));
            double aspect=(double)width*other.height/(height*other.width);
            if(aspect<.9||aspect>1.11)
            {
                bool wider=aspect>1;double ratio=wider?1/aspect:aspect;
                // Compare the common central field of view when one camera crops horizontally and the other vertically.
                foreach(double split in new[]{.30,.60,1.0})
                {
                    double sx=Math.Max(.5,Math.Pow(ratio,split)),sy=Math.Max(.5,Math.Pow(ratio,1-split));
                    foreach(double shift in new[]{.3,.5,.7})
                    {
                        var a=Grid(wider?sx:1,wider?1:sy,shift,.5);
                        var b=other.Grid(wider?1:sx,wider?sy:1,shift,.5);
                        var v=CompareCells(a,b);
                        if(v.Enough&&(!best.Enough||v.Layout>best.Layout))best=v;
                    }
                }
            }
            if(!best.Enough)best.Reasons.Add("缺少足够的不透明明暗结构来核对场景");
            else
            {
                if(best.Layout<.58)best.Reasons.Add("明暗分布与基准构图差异大");
                if(best.ColorSpread>.23)best.Reasons.Add("不同位置的色偏差异大，可能换了背景或使用混合光");
            }
            if(Faces.HasValue&&other.Faces.HasValue&&Faces!=other.Faces)
                best.Reasons.Add("已识别人脸数量不同（基准 "+Faces+"，目标 "+other.Faces+"）");
            return best;
        }
    }
}
