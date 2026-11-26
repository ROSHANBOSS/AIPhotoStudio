using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AIPhotoStudio
{
    internal static partial class SpotHealing
    {
        private const int TileEdge=64;
        private const long WorkBudget=16000000;

        private struct Color3
        {
            internal double R,G,B;
            internal Color3(double r,double g,double b) { R=r; G=g; B=b; }
            public static Color3 operator +(Color3 a,Color3 b) { return new Color3(a.R+b.R,a.G+b.G,a.B+b.B); }
            public static Color3 operator -(Color3 a,Color3 b) { return new Color3(a.R-b.R,a.G-b.G,a.B-b.B); }
            public static Color3 operator *(Color3 a,double t) { return new Color3(a.R*t,a.G*t,a.B*t); }
            internal double Energy { get { return .25*R*R+.5*G*G+.25*B*B; } }
            internal Color3 Limit(double magnitude)
            {
                return new Color3(Clamp(R,-magnitude,magnitude),Clamp(G,-magnitude,magnitude),Clamp(B,-magnitude,magnitude));
            }
        }
        private static double Clamp(double v,double low,double high) { return Math.Max(low,Math.Min(high,v)); }
        private static ushort Code(double v) { return (ushort)Math.Round(Clamp(v,0,1)*65535); }
        private static double Falloff(double relative)
        {
            double t=Clamp((1-relative)/.44,0,1);
            return t*t*(3-2*t);
        }
        private sealed class Spot
        {
            internal int X,Y;
            internal double Radius,Strength;
            internal Int32Rect Bounds;
        }
        private static Int32Rect Bounds(LocalEdits.Stroke stroke,int w,int h,double margin)
        {
            double r=stroke.radius*Math.Min(w,h)+margin;
            var target=stroke.kind=="sampleRepair"?stroke.points.Take(1):stroke.points;
            int left=Math.Max(0,(int)Math.Floor(target.Min(p=>p.x)*(w-1)-r));
            int top=Math.Max(0,(int)Math.Floor(target.Min(p=>p.y)*(h-1)-r));
            int right=Math.Min(w-1,(int)Math.Ceiling(target.Max(p=>p.x)*(w-1)+r));
            int bottom=Math.Min(h-1,(int)Math.Ceiling(target.Max(p=>p.y)*(h-1)+r));
            return new Int32Rect(left,top,right-left+1,bottom-top+1);
        }
        private sealed class Tile
        {
            internal Int32Rect Rect;
            internal ushort[] Data;
        }
        // Read only original pixels for donor selection. Mutable tiles contain just the parts touched by retouching.
        private sealed class Surface
        {
            internal readonly BitmapSource Original;
            internal readonly int W,H;
            private readonly int across;
            private readonly Dictionary<int,Tile> reads=new Dictionary<int,Tile>();
            private readonly Queue<int> order=new Queue<int>();
            private readonly Dictionary<int,Tile> changed=new Dictionary<int,Tile>();
            internal bool HasChanges { get { return changed.Count>0; } }
            internal Surface(BitmapSource source)
            {
                if(source==null)throw new ArgumentNullException("source");
                if(source.PixelWidth<1||source.PixelHeight<1||source.PixelWidth>65535||source.PixelHeight>65535||
                    (long)source.PixelWidth*source.PixelHeight>80000000L)
                    throw new NotSupportedException("点修支持单边至 65535 像素、总计至 8000 万像素的工作图。");
                Original=source.Format==PixelFormats.Rgba64 ? source : new FormatConvertedBitmap(source,PixelFormats.Rgba64,null,0);
                W=Original.PixelWidth; H=Original.PixelHeight; across=(W+TileEdge-1)/TileEdge;
            }
            private int Key(int x,int y) { return (y/TileEdge)*across+x/TileEdge; }
            private static int Index(Tile t,int x,int y) { return ((y-t.Rect.Y)*t.Rect.Width+x-t.Rect.X)*4; }
            private Tile ReadTile(int key)
            {
                Tile tile;
                if(reads.TryGetValue(key,out tile))return tile;
                int left=(key%across)*TileEdge,top=(key/across)*TileEdge;
                var rect=new Int32Rect(left,top,Math.Min(TileEdge,W-left),Math.Min(TileEdge,H-top));
                tile=new Tile { Rect=rect,Data=new ushort[rect.Width*rect.Height*4] };
                Original.CopyPixels(rect,tile.Data,rect.Width*8,0);
                if(reads.Count>=128)reads.Remove(order.Dequeue());
                reads.Add(key,tile); order.Enqueue(key);
                return tile;
            }
            internal bool OriginalAt(int x,int y,out Color3 color,out double alpha)
            {
                if(x<0||x>=W||y<0||y>=H){color=new Color3();alpha=0;return false;}
                Tile t=ReadTile(Key(x,y)); int i=Index(t,x,y);
                color=new Color3(t.Data[i]/65535.0,t.Data[i+1]/65535.0,t.Data[i+2]/65535.0);
                alpha=t.Data[i+3]/65535.0;
                return true;
            }
            internal void Blend(int x,int y,Color3 target,double strength)
            {
                if(strength<=0)return;
                int key=Key(x,y); Tile t;
                bool existed=changed.TryGetValue(key,out t);
                if(!existed)t=ReadTile(key);
                int i=Index(t,x,y);
                if(t.Data[i+3]==0)return;
                ushort r=(ushort)Math.Round(t.Data[i]+(Code(target.R)-t.Data[i])*strength);
                ushort g=(ushort)Math.Round(t.Data[i+1]+(Code(target.G)-t.Data[i+1])*strength);
                ushort b=(ushort)Math.Round(t.Data[i+2]+(Code(target.B)-t.Data[i+2])*strength);
                if(r==t.Data[i]&&g==t.Data[i+1]&&b==t.Data[i+2])return;
                if(!existed)
                {
                    if((long)(changed.Count+1)*TileEdge*TileEdge>10000000)throw new InvalidOperationException("点修影响区域过大，请减小修复半径或笔数。");
                    t=new Tile{Rect=t.Rect,Data=(ushort[])t.Data.Clone()}; changed.Add(key,t);
                }
                t.Data[i]=r; t.Data[i+1]=g; t.Data[i+2]=b;
            }
            internal void Erase(Func<int,int,double> coverage,Int32Rect bounds,ref long work,CancellationToken cancel)
            {
                foreach(var pair in changed)
                {
                    cancel.ThrowIfCancellationRequested();
                    var t=pair.Value;
                    int x0=Math.Max(bounds.X,t.Rect.X),x1=Math.Min(bounds.X+bounds.Width,t.Rect.X+t.Rect.Width);
                    int y0=Math.Max(bounds.Y,t.Rect.Y),y1=Math.Min(bounds.Y+bounds.Height,t.Rect.Y+t.Rect.Height);
                    if(x1<=x0||y1<=y0)continue;
                    work+=(long)(x1-x0)*(y1-y0);
                    if(work>2*WorkBudget)throw new InvalidOperationException("点修擦除计算量超过预算，请缩小笔画范围。");
                    var origin=ReadTile(pair.Key).Data;
                    for(int y=y0;y<y1;y++) for(int x=x0;x<x1;x++)
                    {
                        double a=coverage(x,y);
                        if(a<=0)continue;
                        int i=Index(t,x,y);
                        for(int c=0;c<3;c++)t.Data[i+c]=(ushort)Math.Round(t.Data[i+c]+(origin[i+c]-t.Data[i+c])*a);
                    }
                }
            }
            internal BitmapSource Finish(BitmapSource source,CancellationToken cancel)
            {
                bool different=false;
                foreach(var pair in changed)
                {
                    cancel.ThrowIfCancellationRequested();
                    if(!pair.Value.Data.SequenceEqual(ReadTile(pair.Key).Data)){different=true;break;}
                }
                if(!different)return source;
                var result=new WriteableBitmap(W,H,source.DpiX,source.DpiY,PixelFormats.Rgba64,null);
                int stride=W*8,rows=Math.Max(1,Math.Min(TileEdge,2097152/stride));
                var band=new ushort[W*rows*4];
                for(int y=0;y<H;y+=rows)
                {
                    cancel.ThrowIfCancellationRequested();
                    int n=Math.Min(rows,H-y); var rect=new Int32Rect(0,y,W,n);
                    Original.CopyPixels(rect,band,stride,0);
                    for(int ty=y/TileEdge;ty<=(y+n-1)/TileEdge;ty++) for(int tx=0;tx<across;tx++)
                    {
                        Tile t;
                        if(!changed.TryGetValue(ty*across+tx,out t))continue;
                        int a=Math.Max(y,t.Rect.Y),b=Math.Min(y+n,t.Rect.Y+t.Rect.Height);
                        for(int row=a;row<b;row++)
                            Buffer.BlockCopy(t.Data,(row-t.Rect.Y)*t.Rect.Width*8,band,((row-y)*W+t.Rect.X)*8,t.Rect.Width*8);
                    }
                    result.WritePixels(rect,band,stride,0);
                }
                result.Freeze();
                var frame=source as BitmapFrame;
                if(frame!=null&&frame.ColorContexts!=null&&frame.ColorContexts.Count!=0)
                {
                    var tagged=BitmapFrame.Create(result,null,null,frame.ColorContexts);
                    tagged.Freeze();return tagged;
                }
                return result;
            }
        }
        private sealed class Tap
        {
            internal int X,Y; internal double U,V; internal Color3 Target;
        }
        private struct Plane
        {
            internal Color3 Center,X,Y;
            internal Color3 At(double u,double v){return Center+X*u+Y*v;}
        }
        private sealed class Context
        {
            internal Tap[] Taps;
            internal double[] Inverse;
            internal Plane Target;
            internal double Variation;
        }
        private static double[] Geometry(Tap[] taps)
        {
            double n=taps.Length,x=0,y=0,xx=0,xy=0,yy=0;
            foreach(var t in taps){x+=t.U;y+=t.V;xx+=t.U*t.U;xy+=t.U*t.V;yy+=t.V*t.V;}
            double a=xx*yy-xy*xy,b=y*xy-x*yy,c=x*xy-y*xx,d=n*yy-y*y,e=x*y-n*xy,f=n*xx-x*x;
            double determinant=n*a+x*b+y*c;
            if(determinant<.01*n*n)return null;
            return new[]{a/determinant,b/determinant,c/determinant,d/determinant,e/determinant,f/determinant};
        }
        private static Plane Fit(Context context,Color3[] colors)
        {
            Color3 s=new Color3(),sx=new Color3(),sy=new Color3();
            for(int i=0;i<context.Taps.Length;i++)
            {
                var c=colors==null?context.Taps[i].Target:colors[i];
                s+=c;sx+=c*context.Taps[i].U;sy+=c*context.Taps[i].V;
            }
            var m=context.Inverse;
            return new Plane{Center=s*m[0]+sx*m[1]+sy*m[2],X=s*m[1]+sx*m[3]+sy*m[4],Y=s*m[2]+sx*m[4]+sy*m[5]};
        }
        private static Context Model(Tap[] taps)
        {
            var context=new Context{Taps=taps,Inverse=Geometry(taps)};
            if(context.Inverse==null)return null;
            context.Target=Fit(context,null);
            double energy=0;
            foreach(var t in taps)energy+=(t.Target-context.Target.At(t.U,t.V)).Energy;
            context.Variation=Math.Sqrt(energy/taps.Length);
            return context;
        }
        private static Context Ring(Surface surface,Spot spot,Spot[] marked,bool compact)
        {
            var taps=new List<Tap>();
            var coordinates=new HashSet<long>();
            double[] radii=compact?new[]{.68,.82,.96}:new[]{1.04,1.27,1.50};
            for(int j=0;j<radii.Length;j++) for(int i=0;i<20;i++)
            {
                double angle=2*Math.PI*(i+.36*j)/20;
                int x=spot.X+(int)Math.Round(spot.Radius*radii[j]*Math.Cos(angle));
                int y=spot.Y+(int)Math.Round(spot.Radius*radii[j]*Math.Sin(angle));
                long id=((long)x<<32)|(uint)y;
                if(!coordinates.Add(id)||Occupied(x,y,0,marked,spot))continue;
                Color3 sample;double alpha;
                if(!surface.OriginalAt(x,y,out sample,out alpha)||alpha<.42)continue;
                taps.Add(new Tap{X=x,Y=y,U=(x-spot.X)/spot.Radius,V=(y-spot.Y)/spot.Radius,Target=sample});
            }
            if(taps.Count<12||(compact&&(taps.Count<24||taps.Count<coordinates.Count*.78)))return null;
            var context=Model(taps.ToArray());
            if(context==null)return null;
            if(!compact)return context.Variation>.16?null:context;

            // A nearby contour can spoil the broad samples while the skin immediately around the click is consistent.
            // Refit the inner annulus twice, and require most of its original samples in almost every direction.
            for(int pass=0;pass<2;pass++)
            {
                var errors=context.Taps.Select(t=>Math.Sqrt((t.Target-context.Target.At(t.U,t.V)).Energy)).OrderBy(v=>v).ToArray();
                double tolerance=Math.Max(.015,2.6*errors[errors.Length/2]);
                var inliers=context.Taps.Where(t=>Math.Sqrt((t.Target-context.Target.At(t.U,t.V)).Energy)<=tolerance).ToArray();
                if(inliers.Length<taps.Count*.75)return null;
                if(inliers.Length==context.Taps.Length)break;
                context=Model(inliers);if(context==null)return null;
            }
            var sectors=new bool[8];
            foreach(var t in context.Taps)
            {
                int sector=(int)Math.Floor((Math.Atan2(t.V,t.U)+Math.PI)*4/Math.PI);
                sectors[sector%8]=true;
            }
            if(sectors.Count(on=>on)<7||context.Variation>.060||Math.Sqrt(context.Target.X.Energy+context.Target.Y.Energy)>.24)return null;
            return context;
        }
        private sealed class Match
        {
            internal int Dx,Dy;
            internal Plane Source;
            internal double TextureScale,Cost;
        }
        private static bool Occupied(int x,int y,double radius,Spot[] marked,Spot except=null)
        {
            foreach(var spot in marked)
            {
                if(Object.ReferenceEquals(spot,except))continue;
                double dx=x-spot.X,dy=y-spot.Y,clearance=radius+spot.Radius+.5;
                if(dx*dx+dy*dy<clearance*clearance)return true;
            }
            return false;
        }
        private static Match FindMatch(Surface surface,Spot spot,Spot[] marked,Context context,Func<int,int,double> protect,CancellationToken cancel)
        {
            Match best=null;
            var samples=new Color3[context.Taps.Length];
            var considered=new HashSet<long>();
            double[] rings={2.15,2.8,3.65,4.7};
            for(int ring=0;ring<rings.Length;ring++) for(int direction=0;direction<16;direction++)
            {
                cancel.ThrowIfCancellationRequested();
                double angle=2*Math.PI*(direction+.31*ring)/16;
                int dx=(int)Math.Round(Math.Cos(angle)*spot.Radius*rings[ring]),dy=(int)Math.Round(Math.Sin(angle)*spot.Radius*rings[ring]);
                int cx=spot.X+dx,cy=spot.Y+dy;
                if(!considered.Add(((long)cx<<32)|(uint)cy)||Occupied(cx,cy,spot.Radius,marked)||
                    cx<0||cy<0||cx>=surface.W||cy>=surface.H||(protect!=null&&protect(cx,cy)>.75))continue;
                bool usable=true;
                for(int i=0;i<context.Taps.Length;i++)
                {
                    var t=context.Taps[i];double alpha;
                    if(!surface.OriginalAt(t.X+dx,t.Y+dy,out samples[i],out alpha)||alpha<.42){usable=false;break;}
                }
                if(!usable)continue;
                Plane source=Fit(context,samples);
                double colorJump=Math.Sqrt((source.Center-context.Target.Center).Energy);
                if(colorJump>.24)continue;
                double energy=0,mismatch=0;
                for(int i=0;i<samples.Length;i++)
                {
                    var t=context.Taps[i];
                    Color3 texture=samples[i]-source.At(t.U,t.V);
                    Color3 reference=t.Target-context.Target.At(t.U,t.V);
                    energy+=texture.Energy;
                    mismatch+=Math.Min(.045,(texture-reference).Energy);
                }
                double variation=Math.Sqrt(energy/samples.Length),insideMax=0;
                for(int i=-1;i<8;i++)
                {
                    double u=i<0?0:.60*Math.Cos(i*Math.PI/4),v=i<0?0:.60*Math.Sin(i*Math.PI/4);
                    Color3 c;double alpha;
                    int x=cx+(int)Math.Round(u*spot.Radius),y=cy+(int)Math.Round(v*spot.Radius);
                    if(!surface.OriginalAt(x,y,out c,out alpha)||alpha<.42||(protect!=null&&protect(x,y)>.9)){usable=false;break;}
                    insideMax=Math.Max(insideMax,Math.Sqrt((c-source.At(u,v)).Energy));
                }
                if(!usable||insideMax>Math.Max(.085,3.5*context.Variation+.035))continue;
                double slope=Math.Sqrt((source.X-context.Target.X).Energy+(source.Y-context.Target.Y).Energy);
                double cost=Math.Sqrt(mismatch/samples.Length)+.30*colorJump+.18*slope+.29*Math.Abs(variation-context.Variation)+.0015*ring;
                if(cost>.080+.60*Math.Min(context.Variation,.10)||(best!=null&&cost>=best.Cost))continue;
                best=new Match{Dx=dx,Dy=dy,Source=source,Cost=cost,TextureScale=Clamp((context.Variation+.003)/(variation+.003),.55,1.30)};
            }
            return best;
        }
        private static bool CentralDefect(Surface surface,Spot spot,Context context)
        {
            int usable=0,deviating=0;
            var mean=new Color3();
            double threshold=.018+1.5*context.Variation;
            for(int i=-1;i<8;i++)
            {
                int dx=i<0?0:(int)Math.Round(.18*spot.Radius*Math.Cos(i*Math.PI/4));
                int dy=i<0?0:(int)Math.Round(.18*spot.Radius*Math.Sin(i*Math.PI/4));
                Color3 color;double alpha;
                if(!surface.OriginalAt(spot.X+dx,spot.Y+dy,out color,out alpha)||alpha<.42)continue;
                var difference=color-context.Target.At(dx/spot.Radius,dy/spot.Radius);
                usable++;mean+=difference;
                if(Math.Sqrt(difference.Energy)>threshold)deviating++;
            }
            return usable>=7&&deviating>=.66*usable&&Math.Sqrt(mean.Energy)/usable>threshold;
        }
        private static Match NearbyTexture(Surface surface,Spot spot,Spot[] marked,Context target,Func<int,int,double> protect,CancellationToken cancel)
        {
            // Verify a smaller source patch next to the mark. Its entire effective area stays beyond the central defect.
            var taps=new List<Tap>{new Tap()};
            var seen=new HashSet<long>{0};
            double[] sourceRadii={.30,.59};
            for(int ring=0;ring<sourceRadii.Length;ring++)for(int i=0;i<16;i++)
            {
                double angle=2*Math.PI*(i+.36*ring)/16;
                int x=(int)Math.Round(sourceRadii[ring]*spot.Radius*Math.Cos(angle));
                int y=(int)Math.Round(sourceRadii[ring]*spot.Radius*Math.Sin(angle));
                if(seen.Add(((long)x<<32)|(uint)y))taps.Add(new Tap{X=x,Y=y,U=x/spot.Radius,V=y/spot.Radius});
            }
            if(taps.Count<12)return null;
            var basis=new Context{Taps=taps.ToArray()};basis.Inverse=Geometry(basis.Taps);
            if(basis.Inverse==null)return null;
            var samples=new Color3[taps.Count];
            double[] distances={1.38,1.63,1.96};
            Match best=null;
            for(int ring=0;ring<distances.Length;ring++)for(int i=0;i<16;i++)
            {
                cancel.ThrowIfCancellationRequested();
                double angle=2*Math.PI*(i+.31*ring)/16;
                int dx=(int)Math.Round(distances[ring]*spot.Radius*Math.Cos(angle));
                int dy=(int)Math.Round(distances[ring]*spot.Radius*Math.Sin(angle));
                int cx=spot.X+dx,cy=spot.Y+dy;
                if((double)dx*dx+(double)dy*dy<1.32*1.32*spot.Radius*spot.Radius||Occupied(cx,cy,.67*spot.Radius,marked,spot))continue;
                bool usable=true;
                for(int j=0;j<basis.Taps.Length;j++)
                {
                    var t=basis.Taps[j];double alpha;int x=cx+t.X,y=cy+t.Y;
                    if(!surface.OriginalAt(x,y,out samples[j],out alpha)||alpha<.55||(protect!=null&&protect(x,y)>.75)){usable=false;break;}
                }
                if(!usable)continue;
                Plane source=Fit(basis,samples);
                double jump=Math.Sqrt((source.Center-target.Target.Center).Energy);
                if(jump>.075+2.6*target.Variation)continue;
                double energy=0,peak=0;
                for(int j=0;j<basis.Taps.Length;j++)
                {
                    var t=basis.Taps[j];double residual=(samples[j]-source.At(t.U,t.V)).Energy;
                    energy+=residual;peak=Math.Max(peak,residual);
                }
                double variation=Math.Sqrt(energy/samples.Length);
                double slope=Math.Sqrt((source.X-target.Target.X).Energy+(source.Y-target.Target.Y).Energy);
                if(Math.Sqrt(peak)>.045+3.2*target.Variation||variation>.022+1.6*target.Variation||slope>.33)continue;
                double cost=.45*jump+.12*slope+.45*Math.Abs(variation-target.Variation)+.55*variation+.0007*ring;
                if(best==null||cost<best.Cost)
                    best=new Match{Dx=dx,Dy=dy,Source=source,Cost=cost,TextureScale=Clamp((target.Variation+.003)/(variation+.003),.55,1.30)};
            }
            return best;
        }
        private static void Heal(Surface surface,Spot spot,Spot[] marked,Func<int,int,double> protect,CancellationToken cancel)
        {
            if(spot.Radius<1.4)return;
            Context context=Ring(surface,spot,marked,false);
            Match donor=context==null?null:FindMatch(surface,spot,marked,context,protect,cancel);
            double extent=1;
            if(donor==null)
            {
                context=Ring(surface,spot,marked,true);
                if(context==null||!CentralDefect(surface,spot,context))return;
                donor=NearbyTexture(surface,spot,marked,context,protect,cancel);
                if(donor==null)return;
                extent=.66;
            }
            double residualLimit=Math.Max(.020,Math.Min(.22,.012+3.2*context.Variation));
            Int32Rect rect=spot.Bounds;
            for(int y=rect.Y;y<rect.Y+rect.Height;y++)
            {
                cancel.ThrowIfCancellationRequested();
                double v=(y-spot.Y)/spot.Radius;
                for(int x=rect.X;x<rect.X+rect.Width;x++)
                {
                    double u=(x-spot.X)/spot.Radius,d2=u*u+v*v;
                    if(d2>=extent*extent)continue;
                    double amount=spot.Strength*Falloff(Math.Sqrt(d2)/extent);
                    if(protect!=null)amount*=1-protect(x,y);
                    if(amount<=.00001)continue;
                    Color3 color,original;double alpha,originalAlpha;
                    if(!surface.OriginalAt(x,y,out original,out originalAlpha)||originalAlpha<=0||
                        !surface.OriginalAt(x+donor.Dx,y+donor.Dy,out color,out alpha)||alpha<Math.Max(.20,originalAlpha*.70))continue;
                    Color3 baseline=context.Target.At(u,v);
                    double deviation=Math.Sqrt((original-baseline).Energy);
                    amount*=ImagingRecipe.Ramp(.002+.30*context.Variation,.012+1.15*context.Variation,deviation);
                    if(amount<=.00001)continue;
                    Color3 texture=(color-donor.Source.At(u,v)).Limit(residualLimit)*donor.TextureScale;
                    surface.Blend(x,y,baseline+texture,amount);
                }
            }
        }
        private static void CopySample(Surface surface,Spot target,LocalEdits.Mark sample,Func<int,int,double> protect,CancellationToken cancel)
        {
            int dx=(int)Math.Round(sample.x*(surface.W-1))-target.X,dy=(int)Math.Round(sample.y*(surface.H-1))-target.Y;
            if(dx==0&&dy==0)return;
            Int32Rect rect=target.Bounds;
            for(int y=rect.Y;y<rect.Y+rect.Height;y++)
            {
                cancel.ThrowIfCancellationRequested();
                double v=(y-target.Y)/target.Radius;
                for(int x=rect.X;x<rect.X+rect.Width;x++)
                {
                    double u=(x-target.X)/target.Radius,d2=u*u+v*v;
                    if(d2>=1)continue;
                    double amount=target.Strength*Falloff(Math.Sqrt(d2));
                    if(protect!=null)amount*=1-protect(x,y);
                    if(amount<=.00001)continue;
                    Color3 color,original;double alpha,originalAlpha;
                    if(surface.OriginalAt(x,y,out original,out originalAlpha)&&originalAlpha>0&&
                        surface.OriginalAt(x+dx,y+dy,out color,out alpha)&&alpha>=Math.Max(.20,originalAlpha*.70))
                        surface.Blend(x,y,color,amount);
                }
            }
        }
        internal static BitmapSource Apply(BitmapSource source,LocalEdits.Stroke[] strokes,CancellationToken cancel,Action<int> markCompleted=null,bool onlyEyes=false)
        {
            cancel.ThrowIfCancellationRequested();
            var surface=new Surface(source);
            var all=new Dictionary<LocalEdits.Stroke,Spot>(); long work=0;
            foreach(var s in strokes)
            {
                cancel.ThrowIfCancellationRequested();
                if((onlyEyes?s.kind!="redEye":!LocalEdits.IsRepair(s.kind))||s.strength<=0)continue;
                var spot=new Spot{X=(int)Math.Round(s.points[0].x*(surface.W-1)),Y=(int)Math.Round(s.points[0].y*(surface.H-1)),
                    Radius=s.radius*Math.Min(surface.W,surface.H),Strength=s.strength,Bounds=Bounds(s,surface.W,surface.H,0)};
                work+=(long)spot.Bounds.Width*spot.Bounds.Height;
                if(work>WorkBudget)throw new InvalidOperationException("点修总范围超过 1600 万个待检查像素，请减小修复半径或笔数。");
                all[s]=spot;
            }
            if(all.Count==0)return source;
            // Adding a manually sampled patch must not retarget earlier automatic repairs.
            Spot[] marked=all.Where(pair=>pair.Key.kind=="heal").Select(pair=>pair.Value).ToArray();
            var protectStrokes=strokes.Where(s=>s.kind=="protect"&&s.strength>0).ToArray();
            Func<int,int,double> protect=protectStrokes.Length==0?null:LocalEdits.RepairCoverage(surface.W,surface.H,protectStrokes,true,cancel);
            long eraseWork=0;
            int completed=0;
            foreach(var s in strokes)
            {
                cancel.ThrowIfCancellationRequested();
                Spot spot;
                if(all.TryGetValue(s,out spot))
                {
                    if(s.kind=="redEye")CorrectEye(surface,spot,protect,cancel);
                    else if(s.kind=="sampleRepair")CopySample(surface,spot,s.points[1],protect,cancel);
                    else Heal(surface,spot,marked,protect,cancel);
                    if(markCompleted!=null)markCompleted(++completed);
                }
                else if(s.kind=="erase"&&s.strength>0&&surface.HasChanges)
                {
                    var brush=new LocalEdits.Stroke {kind="paint",points=s.points,radius=s.radius,strength=s.strength};
                    var coverage=LocalEdits.RepairCoverage(surface.W,surface.H,new[]{brush},false,cancel);
                    double gridStep=Math.Max(.5,Math.Max(Math.Min(surface.W,surface.H)/220.0,Math.Max(surface.W,surface.H)/1000.0));
                    surface.Erase(coverage,Bounds(s,surface.W,surface.H,2*gridStep),ref eraseWork,cancel);
                }
            }
            cancel.ThrowIfCancellationRequested();
            return surface.Finish(source,cancel);
        }
    }
}
