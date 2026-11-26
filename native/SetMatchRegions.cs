using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AIPhotoStudio
{
    internal static class SetMatchRegions
    {
        internal const int SampleEdge=480;
        internal const double MinimumRadius=.004;
        internal static int SampleSize(Area[] areas){return areas.Any(a=>a.Hair!=null||Math.Min(a.Rx,a.Ry)<.01||
            a.Opposite!=null&&Math.Min(a.Opposite.Rx,a.Opposite.Ry)<.01)?960:SampleEdge;}
        internal static System.Windows.Int32Rect DetailBox(BitmapSource source,Area cheek)
        {
            int w=source.PixelWidth,h=source.PixelHeight;
            double scale=cheek.Hair==null?12:2.8;
            int size=Math.Min(Math.Min(w,h),Math.Max(42,(int)Math.Ceiling(scale*Math.Max(cheek.Rx*w,cheek.Ry*h))));
            int x=Math.Max(0,Math.Min(w-size,(int)Math.Round(cheek.X*w-size*.5)));
            int y=Math.Max(0,Math.Min(h-size,(int)Math.Round(cheek.Y*h-size*.5)));
            return new System.Windows.Int32Rect(x,y,size,size);
        }
        internal static BitmapSource Detail(BitmapSource source,Area cheek)
        {
            var crop=new CroppedBitmap(source,DetailBox(source,cheek));crop.Freeze();return crop;
        }
        internal static readonly string[] Tones={"exposure","temperature","saturation"};
        private static readonly string[] Roles={"skin","background","hair"};
        internal static string Name(string kind) {return kind=="hair"?"头发":kind=="skin"?"肤色":"背景";}

        internal sealed class Area
        {
            internal string Role,MaskId;
            internal double X,Y,Rx,Ry;
            internal SetMatchFaceEffect Effect;
            internal SetMatchHair Hair;
            internal Area Opposite;
            internal Area Footprint(){return Effect==null?this:Effect.Footprint();}
            internal LocalEdits.GradientGeometry Geometry()
            {
                return Effect!=null?Effect.Geometry():new LocalEdits.GradientGeometry{x=X,y=Y,dx=Rx,dy=Ry,feather=.3};
            }
            internal string Identity(string digest)
            {
                var key="set-match/1/"+digest+"/"+Role+"/"+String.Join("/",new[]{X,Y,Rx,Ry}.Select(v=>v.ToString("F4",CultureInfo.InvariantCulture)));
                if(Effect!=null)key+="/face/"+StudioBridge.Json().Serialize(Effect);
                if(Hair!=null)key+="/"+Hair.bytes;
                using(var sha=SHA256.Create())return new Guid(sha.ComputeHash(Encoding.UTF8.GetBytes(key)).Take(16).ToArray()).ToString("N");
            }
        }

        private static readonly string[] Coordinates={"x","y","rx","ry"};
        private static Area Ellipse(IDictionary<string,object> rect,string role)
        {
            if(rect==null||Coordinates.Any(k=>!rect.ContainsKey(k)))throw new ArgumentException("取样区需要中心和两个半径。");
            Func<string,double> n=k=>Math.Round(StudioWorkspace.Number(rect[k]),4);
            var a=new Area{Role=role,X=n("x"),Y=n("y"),Rx=n("rx"),Ry=n("ry")};
            if(a.X<0||a.X>1||a.Y<0||a.Y>1||a.Rx<MinimumRadius||a.Rx>.5||a.Ry<MinimumRadius||a.Ry>.5)
                throw new ArgumentException("请在照片内选择取样区，两条半径应在对应边长的 0.4% 至 50% 之间。");
            return a;
        }
        internal static Area[] Read(Dictionary<string,object> args)
        {
            object raw;
            if(!args.TryGetValue("regions",out raw))return new Area[0];
            var fields=raw as IDictionary<string,object>;
            if(fields==null||fields.Count==0||fields.Keys.Except(Roles).Any())throw new ArgumentException("请选择有效的校色区域。");
            if(fields.ContainsKey("hair")&&fields.Count!=1)throw new ArgumentException("头发校色需单独选取人物。");
            var areas=new List<Area>();
            foreach(string role in Roles.Where(fields.ContainsKey))
            {
                var rect=fields[role] as IDictionary<string,object>;
                if(role=="hair"){
                    if(rect==null||!rect.ContainsKey("hair")||rect.Keys.Except(Coordinates.Concat(new[]{"hair"})).Any())
                        throw new ArgumentException("请选择本机检测到的人物发区。");
                    areas.Add(SetMatchHair.Read(rect["hair"]).Area());continue;
                }
                if(rect==null||rect.Keys.Except(new[]{"x","y","rx","ry","maskId","effect","opposite"}).Any())
                    throw new ArgumentException("取样区字段无效。");
                var a=Ellipse(rect,role);
                if(rect.ContainsKey("maskId")){
                    Guid checkedId;a.MaskId=rect["maskId"] as string;
                    if(!Guid.TryParseExact(a.MaskId,"N",out checkedId))throw new ArgumentException("要沿用的椭圆蒙版编号无效。");
                }
                if(rect.ContainsKey("effect")){
                    if(role!="skin"||a.MaskId!=null)throw new ArgumentException("额外面部范围只能用于新的肤色推荐。");
                    a.Effect=SetMatchFaceEffect.Read(rect["effect"],a);
                }
                if(rect.ContainsKey("opposite")){
                    var second=rect["opposite"] as IDictionary<string,object>;
                    if(role!="skin"||a.MaskId!=null||second==null||second.Keys.Except(Coordinates).Any())
                        throw new ArgumentException("对侧面颊需要独立的肤色取样圈。");
                    var o=Ellipse(second,role);
                    if(Math.Pow((a.X-o.X)/(a.Rx+o.Rx),2)+Math.Pow((a.Y-o.Y)/(a.Ry+o.Ry),2)<1||
                        a.Effect!=null&&Math.Pow((o.X-a.Effect.x)/a.Effect.rx,2)+Math.Pow((o.Y-a.Effect.y)/a.Effect.ry,2)>1)
                        throw new ArgumentException("两侧面颊应在同一脸内分别取样。");
                    a.Opposite=o;
                }
                areas.Add(a);
            }
            if(areas.Count==2){
                var skin=areas[0].Footprint();var bg=areas[1];
                if(Math.Abs(skin.X-bg.X)<skin.Rx+bg.Rx&&Math.Abs(skin.Y-bg.Y)<skin.Ry+bg.Ry)
                    throw new ArgumentException("肤色与背景的作用范围重叠，请分别框选。");
            }
            return areas.ToArray();
        }

        internal sealed class Samples
        {
            private sealed class Pixel {internal double R,G,B,L,X,Y,Weight=1;}
            private readonly Pixel[] points;
            private readonly int width,height;
            internal readonly double[] Color;
            internal readonly bool Clipped;
            internal readonly Samples Opposite;

            internal Samples(BitmapSource image,Area area,bool optional=false)
            {
                var frame=image.Format==PixelFormats.Rgba64?image:new FormatConvertedBitmap(image,PixelFormats.Rgba64,null,0);
                int w=frame.PixelWidth,h=frame.PixelHeight;
                width=w;height=h;
                int left=Math.Max(0,(int)((area.X-area.Rx)*w)),top=Math.Max(0,(int)((area.Y-area.Ry)*h));
                int right=Math.Min(w,(int)Math.Ceiling((area.X+area.Rx)*w)),bottom=Math.Min(h,(int)Math.Ceiling((area.Y+area.Ry)*h));
                int stride=(right-left)*4;
                var data=new ushort[stride*(bottom-top)];
                frame.CopyPixels(new System.Windows.Int32Rect(left,top,right-left,bottom-top),data,stride*2,0);
                var valid=new List<Pixel>();int clipped=0;
                var hair=area.Hair==null?null:LocalEdits.MatchPrediction(area.Hair.Alpha,w,h);
                int step=Math.Max(1,(int)(Math.Sqrt((right-left)*(bottom-top))/38));
                for(int y=top;y<bottom;y+=step)for(int x=left;x<right;x+=step)
                {
                    double dx=((x+.5)/w-area.X)/area.Rx,dy=((y+.5)/h-area.Y)/area.Ry;
                    double weight=hair==null?1:hair(x,y);
                    if(hair==null?dx*dx+dy*dy>.65*.65:weight<.66)continue;
                    int p=(y-top)*stride+(x-left)*4;
                    if(data[p+3]<49152)continue;
                    double r=data[p]/65535d,g=data[p+1]/65535d,b=data[p+2]/65535d;
                    valid.Add(new Pixel{R=r,G=g,B=b,L=.2126*r+.7152*g+.0722*b,X=x,Y=y,Weight=weight});
                    if(Math.Min(r,Math.Min(g,b))>.965||Math.Max(r,Math.Max(g,b))<.035)clipped++;
                }
                if(valid.Count<12){
                    if(optional)return;
                    throw new InvalidOperationException(Name(area.Role)+"取样范围内可见像素太少，请扩大选区或避开透明处。");
                }
                Clipped=clipped>.30*valid.Count;
                int trim=valid.Count/10;
                points=valid.OrderBy(p=>p.L).Skip(trim).Take(valid.Count-2*trim).ToArray();
                Color=new[]{points.Average(p=>p.R),points.Average(p=>p.G),points.Average(p=>p.B)};
                if(area.Opposite!=null){
                    var secondary=new Samples(frame,area.Opposite,true);
                    if(secondary.Color!=null)Opposite=secondary;
                }
            }

            // Predict only within the opaque core of the radial mask. The final score below is measured
            // again from the complete native render, including pre-existing masks and later adjustments.
            private static double Exposed(double value,double gain)
            {
                int code=(int)Math.Max(0,Math.Min(65535,value*65535));
                double linear=Math.Min(1,ImagingRecipe.Linear[code]*gain);
                return linear<=.0031308?12.92*linear:1.055*Math.Pow(linear,1/2.4)-.055;
            }
            private double Error(double[] goal,double[] values)
            {
                double gain=Math.Pow(2,values[0]),warm=Math.Pow(2,.0085*values[1]),sat=1+values[2]/100;
                var output=new double[3];
                foreach(var p in points)
                {
                    output[0]+=p.R+(Exposed(p.L+(p.R-p.L)*sat,gain*warm)-p.R)*p.Weight;
                    output[1]+=p.G+(Exposed(p.L+(p.G-p.L)*sat,gain)-p.G)*p.Weight;
                    output[2]+=p.B+(Exposed(p.L+(p.B-p.L)*sat,gain/warm)-p.B)*p.Weight;
                }
                for(int i=0;i<3;i++)output[i]/=points.Length;
                return Distance(output,goal);
            }
            internal void Fit(double[] goal,Layer layer,StudioWorkspace.SetPhoto photo,Dictionary<string,double> patch,CancellationToken token)
            {
                if(layer.Area.Effect!=null){
                    var effect=layer.Area.Effect;
                    var coverage=LocalEdits.MatchCoverage(effect.Geometry(),effect.Strokes(),width,height);
                    foreach(var p in points)p.Weight=coverage(p.X,p.Y);
                }
                string[] keys=Tones.Select(t=>LocalEdits.ToneKey(layer.Slot,t)).ToArray();
                var values=keys.Select(k=>photo.Values[k]).ToArray();
                var starts=(double[])values.Clone();
                double best=Error(goal,values);
                double[] coarse={1.1,40,32},steps={.05,1,1},bounds={3,100,100};
                for(int level=0;level<6;level++)for(int i=0;i<3;i++)
                {
                    token.ThrowIfCancellationRequested();
                    if(photo.Locks.Contains(keys[i]))continue;
                    double start=values[i],winner=start,delta=Math.Max(steps[i],coarse[i]/(1<<level));
                    foreach(int sign in new[]{-1,1})
                    {
                        values[i]=Math.Max(-bounds[i],Math.Min(bounds[i],Math.Round((start+sign*delta)/steps[i])*steps[i]));
                        if(layer.Area.Hair!=null){
                            double limit=i==0?.75:i==1?32:42;
                            values[i]=Math.Max(starts[i]-limit,Math.Min(starts[i]+limit,values[i]));
                        }
                        if(Math.Abs(values[i]-start)<1e-8)continue;
                        double error=Error(goal,values);
                        if(error+1e-4<best){best=error;winner=values[i];}
                    }
                    values[i]=winner;
                }
                for(int i=0;i<3;i++)if(Math.Abs(values[i]-photo.Values[keys[i]])>1e-7)patch[keys[i]]=values[i];
            }
        }

        internal static double Distance(double[] left,double[] right)
        {
            double sum=0;for(int i=0;i<3;i++)sum+=Math.Pow(left[i]-right[i],2);
            return 100*Math.Sqrt(sum/3);
        }

        internal sealed class Layer
        {
            internal Area Area;
            internal string Id;
            internal int Slot;
            internal bool Added;
            internal double Before,After;
            internal double? OppositeBefore,OppositeAfter;
            internal bool OppositeClipped;
            internal bool HarmsOpposite {get{return OppositeClipped||OppositeAfter>OppositeBefore+.85;}}
            internal double[] Sample;
            internal bool Clipped;
        }

        internal static object ExistingAreas(IDictionary<string,double> values)
        {
            return LocalEdits.ReadSet(values).layers.Where(l=>l.kind=="radial"&&!l.geometry.invert&&l.geometry.dx>=MinimumRadius&&l.geometry.dx<=.5&&
                l.geometry.dy>=MinimumRadius&&l.geometry.dy<=.5&&!l.strokes.Any(s=>s.kind=="maskPaint")).Select(l=>new {id=l.id,name=l.name,
                    area=new{x=l.geometry.x,y=l.geometry.y,rx=l.geometry.dx,ry=l.geometry.dy,maskId=l.id}}).ToArray();
        }

        private static bool OverlapsLockedEllipse(Area area,LocalEdits.MaskLayer layer,string[] locks,bool faceScope)
        {
            if(layer.kind!="radial"||layer.geometry.invert||!locks.Any(k=>LocalEdits.ExtraKey(k)&&LocalEdits.ToneSlot(k)==layer.slot))return false;
            var g=layer.geometry;
            if(faceScope)return Math.Abs(area.X-g.x)<area.Rx+g.dx&&Math.Abs(area.Y-g.y)<area.Ry+g.dy;
            double dx=(area.X-g.x)/(area.Rx+g.dx),dy=(area.Y-g.y)/(area.Ry+g.dy);
            return dx*dx+dy*dy<.75;
        }

        internal static LocalEdits.MaskSet Draft(IDictionary<string,double> values,Layer[] layers)
        {
            var set=LocalEdits.ReadSet(values);
            foreach(var edit in layers.Where(l=>l.Added))
            {
                if(set.Exists(edit.Slot))throw new InvalidOperationException("所选蒙版编号已改变，请重新拟合。");
                string stem="基准校色 · "+Name(edit.Area.Role),name=stem;
                for(int i=2;set.primaryName==name||set.layers.Any(l=>l.name==name);i++)name=stem+" "+i;
                var hair=edit.Area.Hair;
                set.layers.Add(new LocalEdits.MaskLayer{id=edit.Id,slot=edit.Slot,kind=hair==null?"radial":"hair",name=name,
                    geometry=hair==null?edit.Area.Geometry():null,alpha=hair==null?null:Convert.ToBase64String(hair.Alpha),
                    strokes=edit.Area.Effect==null?new LocalEdits.Stroke[0]:edit.Area.Effect.Strokes()});
            }
            return set;
        }

        internal static Layer[] Fit(StudioWorkspace.SetPhoto photo,string digest,Area[] areas,Dictionary<string,Samples> goals,
            Dictionary<string,double> patch,List<string> reasons,CancellationToken token)
        {
            if(areas.Length!=goals.Count||areas.Any(a=>!goals.ContainsKey(a.Role)))throw new ArgumentException("请在目标片上分别圈出与基准对应的取样区。");
            var before=Imaging.Render(photo.Path,FrameGeometry.SourceValues(photo.Values),SampleSize(areas),token);
            int incomingReasons=reasons.Count;
            var edits=FitOnce(photo,digest,areas,goals,before,patch,reasons,token);
            var cheek=edits.FirstOrDefault(l=>l.Area.Effect!=null&&l.HarmsOpposite);
            if(cheek==null)return edits;
            // Use the actual two-sided render for this photo. Ordinary lighting keeps its existing full-face mask.
            // A restricted mask has its own identity and goes through the same free-slot and overlap/lock checks.
            var limited=cheek.Area.Effect.ProtectOpposite(cheek.Area,cheek.Area.Opposite,before.PixelWidth,before.PixelHeight);
            if(limited!=null){
                cheek.Area.Effect=limited;
                reasons.RemoveRange(incomingReasons,reasons.Count-incomingReasons);
                patch.Clear();
                edits=FitOnce(photo,digest,areas,goals,before,patch,reasons,token);
                cheek=edits.First(l=>l.Area.Role=="skin");
                if(!cheek.HarmsOpposite){
                    reasons.Add("两侧受光不同，已沿面部羽化保护对侧面颊，请核对过渡");
                    return edits;
                }
            }
            foreach(string tone in Tones)patch.Remove(LocalEdits.ToneKey(cheek.Slot,tone));
            cheek.After=cheek.Before;cheek.OppositeAfter=cheek.OppositeBefore;
            reasons.Add("拟合会使对侧肤色偏差增大，已保留原肤色，可改选面颊");
            return edits;
        }

        private static Layer[] FitOnce(StudioWorkspace.SetPhoto photo,string digest,Area[] areas,Dictionary<string,Samples> goals,
            BitmapSource before,Dictionary<string,double> patch,List<string> reasons,CancellationToken token)
        {
            var saved=LocalEdits.ReadSet(photo.Values);
            LocalEdits.ValidateSource(saved,photo.Path);
            var layers=new List<Layer>();
            var free=Enumerable.Range(1,LocalEdits.MaxExtraMasks).Where(s=>!saved.Exists(s)&&!photo.Locks.Any(k=>LocalEdits.ExtraKey(k)&&LocalEdits.ToneSlot(k)==s)).ToList();
            foreach(var area in areas)
            {
                string id=area.MaskId??area.Identity(digest);
                var existing=saved.layers.FirstOrDefault(l=>l.id==id);
                if(area.MaskId!=null&&existing==null)throw new InvalidOperationException("要沿用的蒙版已改变，请重新取样。");
                var footprint=area.Footprint();
                var locked=saved.layers.FirstOrDefault(l=>l!=existing&&OverlapsLockedEllipse(footprint,l,photo.Locks,area.Effect!=null));
                if(locked!=null)throw new InvalidOperationException("选区与已锁定的「"+locked.name+"」重叠。可沿用该椭圆拟合未锁参数，或另外圈选。");
                if(existing==null)
                {
                    if(area.Hair!=null)foreach(var prior in saved.layers.Where(l=>l.kind=="hair")){
                        var alpha=Convert.FromBase64String(prior.alpha);
                        if(area.Hair.SameHead(alpha)&&(SetMatchHair.Owned(prior,alpha,digest)||
                            photo.Locks.Any(k=>LocalEdits.ExtraKey(k)&&LocalEdits.ToneSlot(k)==prior.slot)))
                            throw new InvalidOperationException("此人的已有头发蒙版已锁定或与新范围不同，请到蒙版面板核对后调整。");
                    }
                    if(photo.Locks.Any(k=>k.StartsWith("mask_ref",StringComparison.Ordinal)))throw new InvalidOperationException("蒙版资源已锁定，不能建立新的区域校色蒙版。");
                    if(free.Count==0)throw new InvalidOperationException("此照片没有足够的未锁定空蒙版，请保留需要的选区并释放空间。");
                    layers.Add(new Layer{Area=area,Id=id,Slot=free[0],Added=true});free.RemoveAt(0);
                }
                else
                {
                    var g=existing.geometry;
                    bool changed=area.Hair!=null?
                        existing.kind!="hair"||existing.alpha!=Convert.ToBase64String(area.Hair.Alpha)||existing.guard!=null||existing.strokes.Length!=0:
                        existing.kind!="radial"||g==null||g.invert||Math.Abs(g.x-footprint.X)>.000051||Math.Abs(g.y-footprint.Y)>.000051||
                        Math.Abs(g.dx-footprint.Rx)>.000051||Math.Abs(g.dy-footprint.Ry)>.000051||existing.strokes.Any(s=>s.kind=="maskPaint");
                    if(changed)
                        throw new InvalidOperationException("此前的校色蒙版已移动、反选或补画，请在蒙版面板核对它的范围。");
                    layers.Add(new Layer{Area=area,Id=id,Slot=existing.slot});
                }
            }
            var edits=layers.ToArray();
            var sourceValues=FrameGeometry.SourceValues(photo.Values);
            int sampleSize=SampleSize(areas);
            var baselineValues=new Dictionary<string,double>(sourceValues);
            foreach(var l in edits)foreach(string t in Tones)baselineValues[LocalEdits.ToneKey(l.Slot,t)]=0;
            bool active=edits.Any(l=>Tones.Any(t=>photo.Values[LocalEdits.ToneKey(l.Slot,t)]!=0));
            var baseline=active?Imaging.Render(photo.Path,baselineValues,sampleSize,token):before;
            foreach(var l in edits)
            {
                var goal=goals[l.Area.Role];var sample=new Samples(before,l.Area);
                l.Before=Distance(sample.Color,goal.Color);l.Sample=sample.Color;l.Clipped=sample.Clipped||goal.Clipped;
                if(l.Area.Effect!=null&&sample.Opposite!=null&&!sample.Opposite.Clipped&&goal.Opposite!=null&&!goal.Opposite.Clipped)
                    l.OppositeBefore=Distance(sample.Opposite.Color,goal.Opposite.Color);
                (active?new Samples(baseline,l.Area):sample).Fit(goal.Color,l,photo,patch,token);
                if(l.Area.Effect!=null&&l.Before>8){
                    // A small sample cannot establish that a large cast belongs to the entire face.
                    // Leave some of that skin color in place and show the residual in the real render.
                    double blend=Math.Max(.4,8/l.Before);
                    foreach(string tone in Tones){
                        string key=LocalEdits.ToneKey(l.Slot,tone);double proposed;
                        if(!patch.TryGetValue(key,out proposed))continue;
                        double current=photo.Values[key],step=tone=="exposure"?.05:1;
                        double limited=Math.Round((current+blend*(proposed-current))/step)*step;
                        if(Math.Abs(limited-current)<1e-7)patch.Remove(key);else patch[key]=limited;
                    }
                    reasons.Add("面颊色差较大，已降低肤色校正强度，请核对过渡");
                }
                if(l.Clipped)reasons.Add(Name(l.Area.Role)+"取样处有较多接近纯黑或纯白的像素");
            }
            if(edits.Length==2)
            {
                Func<double[],double> warmth=c=>c[0]-c[2];
                double[] shifts=edits.Select(l=>warmth(goals[l.Area.Role].Color)-warmth(l.Sample)).ToArray();
                if(Math.Abs(shifts[0]-shifts[1])>.16)reasons.Add("肤色和背景相对基准所需的冷暖修正差异大，请核对混合光与取样对象");
            }
            var candidate=new Dictionary<string,double>(sourceValues);foreach(var p in patch)candidate[p.Key]=p.Value;
            var after=patch.Count==0?before:Imaging.Render(photo.Path,candidate,sampleSize,token,Draft(photo.Values,edits));
            foreach(var l in edits)
            {
                var fitted=new Samples(after,l.Area);
                l.After=Distance(fitted.Color,goals[l.Area.Role].Color);
                if(l.OppositeBefore.HasValue){
                    // A clipped preview cannot certify that the second cheek stayed usable.
                    l.OppositeClipped=fitted.Opposite==null||fitted.Opposite.Clipped;
                    if(fitted.Opposite!=null)l.OppositeAfter=Distance(fitted.Opposite.Color,goals[l.Area.Role].Opposite.Color);
                }
                if(l.After+.015>=l.Before)
                {
                    foreach(string t in Tones)patch.Remove(LocalEdits.ToneKey(l.Slot,t));
                    l.After=l.Before;l.OppositeAfter=l.OppositeBefore;l.OppositeClipped=false;
                }
            }
            return edits;
        }
    }
}
