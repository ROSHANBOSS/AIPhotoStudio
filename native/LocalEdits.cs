using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AIPhotoStudio
{
    // Local strokes are content-addressed. Undo keeps immutable targets even after further painting.
    public static partial class LocalEdits
    {
        public sealed class Mark { public double x {get;set;} public double y {get;set;} }
        public class Stroke { public string kind {get;set;} public Mark[] points {get;set;} public double radius {get;set;} public double strength {get;set;} }
        private static string library;
        public static ControlSpec[] Controls { get { return Specs(); } }
        private static ControlSpec[] Specs()
        {
            var list = new List<ControlSpec>();
            for (int i=0;i<5;i++) list.Add(new ControlSpec {key="mask_ref"+i,label="蒙版资源 "+(i+1),group="局部资源",min=0,max=i==4?281474976710655d:4503599627370495d,step=1,initial=0});
            list.Add(new ControlSpec {key="local_exposure",label="局部曝光",group="手绘局部",min=-3,max=3,step=.05,initial=0});
            list.Add(new ControlSpec {key="local_shadows",label="局部阴影",group="手绘局部",min=-100,max=100,step=1,initial=0});
            list.Add(new ControlSpec {key="local_highlights",label="局部高光",group="手绘局部",min=-100,max=100,step=1,initial=0});
            list.Add(new ControlSpec {key="local_temperature",label="局部色温",group="手绘局部",min=-100,max=100,step=1,initial=0});
            list.Add(new ControlSpec {key="local_saturation",label="局部饱和度",group="手绘局部",min=-100,max=100,step=1,initial=0});
            list.Add(new ControlSpec {key="local_smooth",label="局部保纹理柔化",group="手绘局部",min=0,max=100,step=1,initial=0});
            list.Add(new ControlSpec {key="local_warp",label="网格变形强度",group="手绘局部",min=0,max=100,step=1,initial=100});
            for(int slot=0;slot<=MaxExtraMasks;slot++){
                list.Add(new ControlSpec {key=ToneKey(slot,"strength"),label="整层调整强度",group=slot==0?"手绘局部":"独立蒙版",min=0,max=100,step=1,initial=100});
                if(slot>0)list.Add(new ControlSpec {key=ToneKey(slot,"backgroundOutput"),label="背景替换",group="独立蒙版",min=0,max=16777217,step=1,initial=0});
            }
            for(int slot=1;slot<=MaxExtraMasks;slot++)
                foreach(string key in ToneNames)
                {
                    var baseSpec=list.First(control=>control.key=="local_"+key);
                    list.Add(new ControlSpec {key=ToneKey(slot,key),label=baseSpec.label,group="独立蒙版",min=baseSpec.min,max=baseSpec.max,step=baseSpec.step,initial=0});
                }
            return list.ToArray();
        }
        public static void Configure(string directory)
        {
            string location=Path.GetFullPath(directory);
            lock(maskCacheGate)
            {
                if(!String.Equals(location,library,StringComparison.OrdinalIgnoreCase)){parsedSets.Clear();savedGrids.Clear();}
                library=location;
            }
        }
        private static string Resource(string key)
        {
            if (library==null) throw new InvalidOperationException("请先设置局部编辑资源目录。");
            return Path.Combine(library,key+".json");
        }
        private static double Val(IDictionary<string,double> values,string name,double fallback)
        {
            double v;
            return values!=null && values.TryGetValue(name,out v)?v:fallback;
        }
        public static string Hash(IDictionary<string,double> values)
        {
            var text=new StringBuilder(); bool any=false;
            for(int i=0;i<5;i++)
            {
                double v=Val(values,"mask_ref"+i,0);
                double max=i==4?281474976710655d:4503599627370495d;
                if(double.IsNaN(v)||v<0||v>max||v!=Math.Truncate(v)) throw new InvalidDataException("蒙版编号不完整。");
                text.Append(((long)v).ToString(i==4?"x12":"x13")); any|=v!=0;
            }
            return any?text.ToString():null;
        }
        private static string Digest(byte[] bytes) {using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","").ToLowerInvariant();}
        internal static bool IsRepair(string kind) { return kind=="heal"||kind=="sampleRepair"; }
        private static void CheckSampleFields(object raw)
        {
            var entries=raw as IEnumerable;
            if(entries==null)return;
            foreach(object entry in entries)
            {
                var fields=entry as IDictionary<string,object>;object kind,points;
                if(fields==null||!fields.TryGetValue("kind",out kind)||!Object.Equals(kind,"sampleRepair")&&!Object.Equals(kind,"redEye"))continue;
                if(fields.Count!=4||!fields.ContainsKey("radius")||!fields.ContainsKey("strength")||
                    !fields.TryGetValue("points",out points)||!(points is IEnumerable))
                    throw new ArgumentException("取样修复只接受原片内的两个位置、半径和强度。");
                foreach(object point in (IEnumerable)points)
                {
                    var xy=point as IDictionary<string,object>;
                    if(xy==null||xy.Count!=2||!xy.ContainsKey("x")||!xy.ContainsKey("y"))
                        throw new ArgumentException("取样位置必须是原片内的横纵坐标。");
                }
            }
        }
        private static Stroke[] Validate(object raw)
        {
            CheckSampleFields(raw);
            var json=new JavaScriptSerializer {MaxJsonLength=MaxMaskBytes,RecursionLimit=12};
            var strokes=raw as Stroke[]??json.ConvertToType<Stroke[]>(raw);
            if(strokes==null||strokes.Length>256)throw new ArgumentException("每份局部配方最多包含 256 笔。");
            ReadStrokeEdges(raw,strokes);
            int points=0;
            foreach(var s in strokes)
            {
                if(s==null||!(new[]{"paint","erase","maskPaint","maskErase","protect","warp","heal","sampleRepair","redEye"}).Contains(s.kind)||s.points==null||s.points.Length==0||
                    ((s.kind=="warp"||s.kind=="sampleRepair")&&s.points.Length!=2)||((s.kind=="heal"||s.kind=="redEye")&&s.points.Length!=1)||
                    !(s.kind=="redEye"?s.radius>=.001&&s.radius<=.08:s.radius>=.005&&s.radius<=.45)||!(s.strength>=0&&s.strength<=1))
                    throw new ArgumentException("无效的局部笔画。");
                points+=s.points.Length;
                if(points>8192||s.points.Any(p=>p==null||!(p.x>=0&&p.x<=1&&p.y>=0&&p.y<=1)))throw new ArgumentException("笔画过长或坐标超出照片。");
                if(s.kind=="sampleRepair"&&s.points[0].x==s.points[1].x&&s.points[0].y==s.points[1].y)
                    throw new ArgumentException("请为污点选择另一处干净纹理。");
            }
            if(strokes.Count(s=>s.kind=="redEye")>64)throw new ArgumentException("每张照片最多可标记 64 只红眼。");
            return strokes;
        }
        public static Dictionary<string,double> Save(object raw)
        {
            var strokes=UnboundStrokes(Validate(raw));
            return Store(strokes.Length==0?null:Encoding.UTF8.GetBytes(MaskJson().Serialize(strokes)));
        }
        private static Dictionary<string,double> Store(byte[] bytes)
        {
            string hash=null;
            if(bytes!=null)
            {
                if(bytes.Length>MaxMaskBytes)throw new InvalidDataException("这张照片的蒙版笔迹和选区过多。");
                hash=Digest(bytes); string dest=Resource(hash); Directory.CreateDirectory(library);
                if(!File.Exists(dest))
                {
                    string tmp=Path.Combine(library,Guid.NewGuid().ToString("N")+".tmp");
                    bool created=false;
                    try
                    {
                        using(var output=new FileStream(tmp,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                        {
                            created=true;
                            output.Write(bytes,0,bytes.Length);
                        }
                        try {File.Move(tmp,dest);} catch(IOException){if(!File.Exists(dest))throw;}
                    }
                    finally {if(created && File.Exists(tmp))File.Delete(tmp);}
                }
                if(Digest(File.ReadAllBytes(dest))!=hash)throw new IOException("蒙版资源校验失败。");
            }
            var patch=new Dictionary<string,double>();
            for(int i=0;i<5;i++)patch["mask_ref"+i]=hash==null?0:Convert.ToInt64(hash.Substring(i*13,i==4?12:13),16);
            return patch;
        }
        public static Stroke[] Read(IDictionary<string,double> values)
        {
            return MaskSet.CopyStrokes(ReadOnlySet(values).primary);
        }

        private static double Clamp(double v,double min,double max){return Math.Max(min,Math.Min(max,v));}
        public static bool HasPaint(IDictionary<string,double> values,int width,int height)
        {
            return HasPaint(values,width,height,0);
        }
        public static bool HasPaint(IDictionary<string,double> values,int width,int height,int slot)
        {
            var set=ReadOnlySet(values);
            if(!set.Exists(slot))return false;
            var strokes=set.Strokes(slot);
            var layer=slot>0?set.Layer(slot):null;
            if(layer!=null&&layer.geometry!=null)
            {
                var gradient=new GradientField(layer.kind,layer.geometry,width,height);
                var corrections=strokes.Length==0?null:CoverageGrid(set,slot,width,height,CancellationToken.None,true);
                if(GradientWeight(gradient,corrections,Clamp(Math.Round(layer.geometry.x*width-.5),0,width-1),
                    Clamp(Math.Round(layer.geometry.y*height-.5),0,height-1))>.001||
                    GradientWeight(gradient,corrections,0,0)>.001||GradientWeight(gradient,corrections,width-1,0)>.001||
                    GradientWeight(gradient,corrections,0,height-1)>.001||GradientWeight(gradient,corrections,width-1,height-1)>.001)return true;
                if(corrections!=null)
                    for(int y=0;y<corrections.H;y++)for(int x=0;x<corrections.W;x++)
                        if(GradientWeight(gradient,corrections,Math.Min(width-1,x*corrections.Step),Math.Min(height-1,y*corrections.Step))>.001)return true;
                return false;
            }
            if((layer==null||layer.alpha==null)&&!strokes.Any(s=>(s.kind=="paint"||s.kind=="maskPaint")&&s.strength>0))return false;
            var grid=CoverageGrid(set,slot,width,height,CancellationToken.None,false);
            for(int i=0;i<grid.Mask.Length;i++)if(grid.Mask[i]*(1-grid.Protect[i])>.001f)return true;
            return false;
        }
        private sealed class Grid
        {
            internal readonly int W,H; internal readonly float[] Mask,WarpMask,Protect,Dx,Dy;
            internal readonly float[] GradientScale;
            internal readonly double Step; private readonly double imageW,imageH;
            internal int Bytes {get{return Mask.Length*sizeof(float)*(4+(WarpMask==Mask?0:1)+(GradientScale==null?0:1));}}
            internal Grid(int w,int h,Stroke[] strokes,CancellationToken cancel=default(CancellationToken),byte[] alpha=null,byte[] guard=null,bool gradientCorrections=false)
            {
                imageW=w;imageH=h;
                Step=Math.Max(.5,Math.Max(Math.Min(w,h)/220.0,Math.Max(w,h)/1000.0));
                W=(int)Math.Ceiling(w/Step)+2;H=(int)Math.Ceiling(h/Step)+2;
                Mask=new float[W*H];Protect=new float[W*H];Dx=new float[W*H];Dy=new float[W*H];
                if(gradientCorrections)
                {
                    GradientScale=new float[W*H];
                    for(int i=0;i<GradientScale.Length;i++)GradientScale[i]=1;
                }
                if(alpha!=null)
                    for(int y=0;y<H;y++)for(int x=0;x<W;x++)
                        Mask[y*W+x]=(float)SamplePrediction(alpha,(x*Step+.5)/w,(y*Step+.5)/h);
                int lastWarp=Array.FindLastIndex(strokes,s=>s.kind=="warp");
                // A later light-only correction edits the tone mask without changing the saved warp selection.
                bool separate=lastWarp>=0 && strokes.Skip(lastWarp+1).Any(s=>s.kind=="maskPaint"||s.kind=="maskErase");
                WarpMask=separate?new float[W*H]:Mask;
                for(int i=0;i<strokes.Length;i++)Raster(strokes[i],separate && (i<=lastWarp || strokes[i].kind=="paint" || strokes[i].kind=="erase"),cancel);
                if(guard!=null)
                    for(int y=0;y<H;y++)for(int x=0;x<W;x++)
                        Protect[y*W+x]=(float)(1-SamplePrediction(guard,(x*Step+.5)/w,(y*Step+.5)/h));
            }
            private void Raster(Stroke s,bool updateWarpMask,CancellationToken cancel)
            {
                if(IsRepair(s.kind)||s.kind=="redEye")return;
                var edge=s as EdgeStroke;
                var weights=new float[W*H];
                double radius=s.radius*Math.Min(imageW,imageH)/Step;
                // Displacements live on a regular lattice and fade to fixed nodes near the mask boundary.
                bool warp=s.kind=="warp";
                int count=warp?1:s.points.Length;
                int minX=W,maxX=0,minY=H,maxY=0;
                for(int i=0;i<count;i++)
                {
                    cancel.ThrowIfCancellationRequested();
                    var a=s.points[i==0?0:i-1];var b=s.points[i];
                    double x0=a.x*(imageW-1)/Step,y0=a.y*(imageH-1)/Step,x1=b.x*(imageW-1)/Step,y1=b.y*(imageH-1)/Step;
                    double dx=x1-x0,dy=y1-y0,len=dx*dx+dy*dy;
                    int left=(int)Math.Max(0,Math.Floor(Math.Min(x0,x1)-radius)),right=(int)Math.Min(W-1,Math.Ceiling(Math.Max(x0,x1)+radius));
                    int top=(int)Math.Max(0,Math.Floor(Math.Min(y0,y1)-radius)),bottom=(int)Math.Min(H-1,Math.Ceiling(Math.Max(y0,y1)+radius));
                    minX=Math.Min(minX,left);maxX=Math.Max(maxX,right);minY=Math.Min(minY,top);maxY=Math.Max(maxY,bottom);
                    for(int y=top;y<=bottom;y++)
                    {
                        cancel.ThrowIfCancellationRequested();
                        for(int x=left;x<=right;x++)
                        {
                            double t=len==0?0:Clamp(((x-x0)*dx+(y-y0)*dy)/len,0,1);
                            double ax=x-x0-t*dx,ay=y-y0-t*dy;
                            double fall=Clamp((1-Math.Sqrt(ax*ax+ay*ay)/radius) * (warp?1:3),0,1);
                            int j=y*W+x; weights[j]=Math.Max(weights[j],(float)(fall*fall*(3-2*fall)*s.strength));
                        }
                    }
                }
                double mx=0,my=0;
                if(warp){mx=(s.points[1].x-s.points[0].x)*(imageW-1);my=(s.points[1].y-s.points[0].y)*(imageH-1);double scale=Math.Min(1,radius*Step*.45/Math.Max(.001,Math.Sqrt(mx*mx+my*my)));mx*=scale;my*=scale;}
                for(int y=minY;y<=maxY;y++)for(int x=minX;x<=maxX;x++)
                {
                    int j=y*W+x;float a=weights[j];if(a==0)continue;
                    if(edge!=null&&!edge.Plane.Contains((x*Step+.5)/imageW,(y*Step+.5)/imageH))continue;
                    if(GradientScale!=null)GradientScale[j]*=1-a;
                    switch(s.kind)
                    {
                        case "paint":case "maskPaint":
                            Mask[j]+=a*(1-Mask[j]);if(updateWarpMask)WarpMask[j]+=a*(1-WarpMask[j]);break;
                        case "erase":case "maskErase":
                            Mask[j]*=1-a;if(updateWarpMask)WarpMask[j]*=1-a;
                            if(s.kind=="erase"){Dx[j]*=1-a;Dy[j]*=1-a;}break;
                        case "protect":Protect[j]+=a*(1-Protect[j]);break;
                        case "warp":Dx[j]-=(float)(mx*a);Dy[j]-=(float)(my*a);break;
                    }
                }
            }
            internal double At(float[] field,double x,double y)
            {
                x=Clamp(x/Step,0,W-2);y=Clamp(y/Step,0,H-2);int ix=(int)x,iy=(int)y,j=iy*W+ix;
                double fx=x-ix,fy=y-iy;
                return (field[j]*(1-fx)+field[j+1]*fx)*(1-fy)+(field[j+W]*(1-fx)+field[j+W+1]*fx)*fy;
            }
        }

        internal static Func<int,int,double> RepairCoverage(int width,int height,Stroke[] marks,bool protect,CancellationToken cancel)
        {
            var grid=new Grid(width,height,marks,cancel);
            var field=protect?grid.Protect:grid.Mask;
            return (x,y)=>grid.At(field,x,y);
        }

        public static BitmapSource Apply(BitmapSource source,IDictionary<string,double> values,CancellationToken cancel)
        {
            var set=ViewportMask(values);if(set==null)return source;
            return ApplySet(CloseRedactions(source,set.areas,null,cancel),values,set,cancel);
        }
        internal static BitmapSource ApplySaved(BitmapSource source,IDictionary<string,double> values,CancellationToken cancel,BitmapSource rangeReference,ToneRegion region=null)
        {
            if(Hash(values)==null)return source;
            return ApplySet(source,values,ReadOnlySet(values),cancel,rangeReference,region);
        }
        public static BitmapSource ApplyDraft(BitmapSource source,IDictionary<string,double> values,object strokes,CancellationToken cancel)
        {
            return ApplyStrokes(source,values,Validate(strokes),cancel);
        }
        // Brush additions and erasures compose into offset + scale * gradient.
        // Keep the geometry analytic through final rendering, even at zero feather.
        private static double CorrectedWeight(double baseWeight,Grid corrections,double x,double y)
        {
            return corrections==null?baseWeight:Clamp(corrections.At(corrections.Mask,x,y)+baseWeight*corrections.At(corrections.GradientScale,x,y),0,1);
        }
        private static double GradientWeight(GradientField gradient,Grid corrections,double x,double y)
        {
            return CorrectedWeight(gradient.At(x,y),corrections,x,y);
        }
        private static BitmapSource ApplyStrokes(BitmapSource source,IDictionary<string,double> values,Stroke[] strokes,CancellationToken cancel,
            MaskSet set=null,int slot=0,GradientField gradient=null,RangeField range=null,BitmapSource rangeReference=null,ToneRegion region=null)
        {
            double ev=Val(values,"local_exposure",0), sat=Val(values,"local_saturation",0)/100, smooth=Val(values,"local_smooth",0)/100;
            double shadows=Val(values,"local_shadows",0),highlights=Val(values,"local_highlights",0);
            double temperature=Val(values,"local_temperature",0);
            double warp=Val(values,"local_warp",100)/100;
            double strength=Val(values,"local_strength",100)/100;
            bool hasColor=strength>0&&(ev!=0||sat!=0||smooth!=0||shadows!=0||highlights!=0||temperature!=0);
            bool hasWarp=warp!=0&&strokes.Any(s=>s.kind=="warp"&&s.strength>0);
            if(strokes.Any(s=>IsRepair(s.kind)&&s.strength>0))source=SpotHealing.Apply(source,strokes,cancel);
            bool analytic=gradient!=null||range!=null;
            if((!analytic&&(slot==0||set.Layer(slot).alpha==null)&&!strokes.Any(s=>(s.kind=="paint"||s.kind=="maskPaint")&&s.strength>0)) ||
                (!hasColor&&!hasWarp))return source;
            // The local tone sliders use the same linear-luminance curve as their global counterparts.
            var tone=ToneFor(shadows,highlights);
            int w=source.PixelWidth,h=source.PixelHeight,stride=checked(w*8);
            var surface=source.Format==PixelFormats.Rgba64?source:new FormatConvertedBitmap(source,PixelFormats.Rgba64,null,0);
            int fullW=region==null?w:region.Width,fullH=region==null?h:region.Height;
            int originX=region==null?0:region.Area.X,originY=region==null?0:region.Area.Y;
            double shiftLimit=Math.Min(fullW,fullH)*.08;
            var grid=analytic&&strokes.Length==0?null:set==null?new Grid(fullW,fullH,strokes,cancel):CoverageGrid(set,slot,fullW,fullH,cancel,analytic);
            var output=new WriteableBitmap(w,h,source.DpiX,source.DpiY,PixelFormats.Rgba64,null);
            double maxShift=hasWarp?Math.Min(shiftLimit,grid.Dy.Select(d=>Math.Abs((double)d)).Max()):0;
            int halo=(int)Math.Ceiling(maxShift)+3,band=Math.Max(1,Math.Min(64,2097152/stride));
            ushort[] dst=new ushort[checked(band*w*4)];
            var options=new ParallelOptions{CancellationToken=cancel,MaxDegreeOfParallelism=Math.Min(6,Environment.ProcessorCount)};
            var exposureTable=ExposureFor(ev);
            ushort[][] gains=temperature==0?null:new[]{ExposureFor(ev+.0085*temperature),exposureTable,ExposureFor(ev-.0085*temperature)};
            BitmapSource reference=null;
            if(range!=null)
            {
                if(rangeReference==null||rangeReference.PixelWidth!=w||rangeReference.PixelHeight!=h)throw new InvalidDataException("取样原片的尺寸与编辑画布不一致。");
                reference=rangeReference.Format==PixelFormats.Rgba64?rangeReference:new FormatConvertedBitmap(rangeReference,PixelFormats.Rgba64,null,0);
            }
            ushort[] sample=range==null?null:new ushort[dst.Length];
            for(int start=0;start<h;start+=band)
            {
                cancel.ThrowIfCancellationRequested();
                int n=Math.Min(band,h-start),top=Math.Max(0,start-halo),bottom=Math.Min(h,start+n+halo);
                var input=new ushort[checked((bottom-top)*w*4)];
                surface.CopyPixels(new Int32Rect(0,top,w,bottom-top),input,stride,0);
                if(reference!=null)reference.CopyPixels(new Int32Rect(0,start,w,n),sample,stride,0);
                Parallel.For(0,n,options,ly=>
                {
                    int y=start+ly;
                    var tonePixel=tone==null?null:new ushort[4];
                    for(int x=0;x<w;x++)
                    {
                        int o=(ly*w+x)*4,original=((y-top)*w+x)*4;
                        int gx=x+originX,gy=y+originY;
                        double unprotected=analytic?1:1-grid.At(grid.Protect,gx,gy);
                        double mask=range!=null?CorrectedWeight(range.At(sample,o),grid,gx,gy):
                            gradient!=null?GradientWeight(gradient,grid,gx,gy):grid.At(grid.Mask,gx,gy)*unprotected;
                        int ix=x,iy=y,j=original;
                        if(hasWarp)
                        {
                            double warpMask=grid.WarpMask==grid.Mask?mask:grid.At(grid.WarpMask,gx,gy)*unprotected;
                            if(!hasColor)mask=warpMask;
                            if(mask<=.00001&&warpMask<=.00001){Array.Copy(input,original,dst,o,4);continue;}
                            double sx=Clamp(gx+Clamp(grid.At(grid.Dx,gx,gy)*warp*warpMask,-shiftLimit,shiftLimit),originX,originX+w-1);
                            double sy=Clamp(gy+Clamp(grid.At(grid.Dy,gx,gy)*warp*warpMask,-shiftLimit,shiftLimit),originY+top,originY+bottom-1);
                            double sourceMask=grid.At(grid.WarpMask,sx,sy)*(1-grid.At(grid.Protect,sx,sy));
                            sx=gx+(sx-gx)*sourceMask;sy=gy+(sy-gy)*sourceMask;
                            ix=(int)sx-originX;iy=(int)sy-originY;j=((iy-top)*w+ix)*4;
                            int k=ix==w-1?j:j+4,j2=iy==bottom-1?j:j+w*4,k2=iy==bottom-1?k:k+w*4;
                            double fx=sx-(int)sx,fy=sy-(int)sy;
                            for(int c=0;c<4;c++)dst[o+c]=(ushort)Math.Round(((1-fx)*input[j+c]+fx*input[k+c])*(1-fy)+((1-fx)*input[j2+c]+fx*input[k2+c])*fy);
                        }
                        else Array.Copy(input,original,dst,o,4);
                        mask*=strength;
                        if(mask<=.00001)continue;
                        double l=.2126*dst[o]+.7152*dst[o+1]+.0722*dst[o+2];
                        for(int c=0;c<3;c++)
                        {
                            double baseValue=dst[o+c],target=baseValue;
                            if(smooth>0&&ix>0&&ix<w-1&&iy>top&&iy<bottom-1)
                            {
                                double mean=(input[j-4+c]+input[j+4+c]+input[j-w*4+c]+input[j+w*4+c])*.25;
                                target+=(mean-baseValue)*.5*smooth*Math.Max(0,1-Math.Abs(mean-baseValue)/4000);
                            }
                            int index=(int)Clamp(l+(target-l)*(1+sat),0,65535);
                            var transfer=gains==null?exposureTable:gains[c];
                            var exposed=transfer==null?(ushort)index:transfer[index];
                            if(tonePixel==null)dst[o+c]=(ushort)Math.Round(Clamp(baseValue+(exposed-baseValue)*mask,0,65535));
                            else tonePixel[c]=exposed;
                        }
                        if(tonePixel!=null)
                        {
                            tone.Apply(tonePixel,0,4);
                            for(int c=0;c<3;c++)dst[o+c]=(ushort)Math.Round(dst[o+c]+(tonePixel[c]-dst[o+c])*mask);
                        }
                    }
                });
                output.WritePixels(new Int32Rect(0,start,w,n),dst,stride,0);
            }
            output.Freeze();return output;
        }
    }
}
