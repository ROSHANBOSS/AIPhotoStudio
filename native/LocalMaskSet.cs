using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Media.Imaging;

namespace AIPhotoStudio
{
    public static partial class LocalEdits
    {
        public const int MaxExtraMasks=7;
        public const int PredictionSide=256;
        internal const int MaxMaskBytes=4*1024*1024;
        internal static readonly string[] ToneNames={"exposure","shadows","highlights","temperature","saturation"};
        internal static string ToneKey(int slot,string tone) { return slot==0?"local_"+tone:"local_m"+slot+"_"+tone; }
        internal static bool ExtraKey(string key) { return key.StartsWith("local_m",StringComparison.Ordinal); }
        internal static int ToneSlot(string key)
        {
            return ExtraKey(key)&&key.Length>8&&key[7]>='1'&&key[7]<='7'&&key[8]=='_'?key[7]-'0':0;
        }
        internal sealed class MaskLayer
        {
            public string id {get;set;}
            public int slot {get;set;}
            public string name {get;set;}
            public string kind {get;set;}
            public string alpha {get;set;}
            public string guard {get;set;}
            public int? person {get;set;}
            public string bodyScene {get;set;}
            public StudioFinish.FaceSample faceSample {get;set;}
            public Stroke[] strokes {get;set;}
            public GradientGeometry geometry {get;set;}
            public RangeSettings range {get;set;}
        }
        internal sealed class MaskSet
        {
            internal string CachedPath;
            public int version {get;set;}
            public string sourceHash {get;set;}
            public string primaryName {get;set;}
            public string primaryId {get;set;}
            public Stroke[] primary {get;set;}
            public List<MaskLayer> layers {get;set;}
            public AreaMark[] areas {get;set;}
            internal bool HasV6Edits {get{return areas!=null&&areas.Length>0||primary!=null&&primary.Any(UsesBrushV6)||
                layers!=null&&layers.Any(l=>l!=null&&l.strokes!=null&&l.strokes.Any(UsesBrushV6));}}
            internal bool HasGradients {get{return layers.Any(l=>GradientKind(l.kind));}}
            internal bool HasV3Layers {get{return layers.Any(l=>RangeKind(l.kind)||l.kind=="skin"||l.kind=="backdrop");}}
            internal bool HasHandDetail {get{return layers.Any(l=>StudioFinish.HandDetail(l.kind));}}
            internal bool HasSampleRepair {get{return primary.Any(s=>s.kind=="sampleRepair");}}
            internal bool HasV7Edits {get{return primary!=null&&primary.Any(s=>s.kind=="redEye");}}
            internal bool Exists(int slot) {return slot==0?primaryName!=null:layers.Any(l=>l.slot==slot);}
            internal string IdFor(int slot) {return !Exists(slot)?null:slot==0?primaryId:Layer(slot).id;}
            internal MaskLayer Layer(int slot)
            {
                var layer=layers.FirstOrDefault(l=>l.slot==slot);
                if(layer==null)throw new ArgumentException("当前照片没有选中的蒙版。");
                return layer;
            }
            internal void RequireEditable()
            {
                if(CachedPath!=null)throw new InvalidOperationException("请在独立草稿中编辑该蒙版。");
            }
            internal MaskSet EditableCopy()
            {
                var copy=(MaskSet)MemberwiseClone();copy.CachedPath=null;
                copy.primary=CopyStrokes(primary);
                if(areas!=null)copy.areas=areas.Select(m=>new AreaMark{kind=m.kind,radius=m.radius,
                    points=m.points.Select(p=>new Mark{x=p.x,y=p.y}).ToArray()}).ToArray();
                copy.layers=new List<MaskLayer>(layers.Count);
                foreach(var layer in layers)
                {
                    var shape=layer.geometry;var range=layer.range;var face=layer.faceSample;
                    copy.layers.Add(new MaskLayer{id=layer.id,slot=layer.slot,name=layer.name,kind=layer.kind,
                        alpha=layer.alpha,guard=layer.guard,person=layer.person,bodyScene=layer.bodyScene,strokes=CopyStrokes(layer.strokes),
                        geometry=shape==null?null:new GradientGeometry{x=shape.x,y=shape.y,dx=shape.dx,dy=shape.dy,feather=shape.feather,invert=shape.invert},
                        range=range==null?null:new RangeSettings{red=range.red,green=range.green,blue=range.blue,low=range.low,high=range.high,tolerance=range.tolerance,feather=range.feather},
                        faceSample=face==null?null:new StudioFinish.FaceSample{x=face.x,y=face.y,rx=face.rx,ry=face.ry}});
                }
                return copy;
            }
            internal static Stroke[] CopyStrokes(Stroke[] strokes)
            {
                return strokes.Select(CopyStroke).ToArray();
            }
            internal Stroke[] Strokes(int slot) { return slot==0?primary:Layer(slot).strokes; }
            internal byte[] Alpha(int slot)
            {
                string data=slot==0?null:Layer(slot).alpha;
                return data==null?null:Convert.FromBase64String(data);
            }
            internal byte[] Guard(int slot)
            {
                string guard=slot==0?null:Layer(slot).guard;
                return guard==null?null:Convert.FromBase64String(guard);
            }
            internal void SetStrokes(int slot,object raw)
            {
                RequireEditable();
                var validated=Validate(raw);
                sourceHash=BoundBrushSource(validated,sourceHash);
                if(slot==0)primary=validated;
                else {ValidateLightStrokes(validated);Layer(slot).strokes=validated;}
            }
            internal MaskLayer SetGradient(string kind,GradientGeometry geometry,bool creating,int selectedSlot)
            {
                geometry.Check(kind);
                var layer=ShapeLayer(kind,kind=="linear"?"线性渐变":"径向渐变",creating,selectedSlot);
                layer.geometry=geometry;
                return layer;
            }
            internal MaskLayer SetRange(string kind,RangeSettings range,string alpha,bool creating,int selectedSlot)
            {
                range.Check(kind);
                var layer=ShapeLayer(kind,kind=="colorRange"?"颜色范围":"亮度范围",creating,selectedSlot);
                layer.range=range;
                layer.alpha=alpha;
                return layer;
            }
            private MaskLayer ShapeLayer(string kind,string stem,bool creating,int selectedSlot)
            {
                RequireEditable();
                if(!creating)
                {
                    var existing=Layer(selectedSlot);
                    if(existing.kind!=kind)throw new InvalidOperationException("所选蒙版与要编辑的选区类型不一致。");
                    return existing;
                }
                int slot=Enumerable.Range(1,MaxExtraMasks).FirstOrDefault(n=>!Exists(n));
                if(slot==0)throw new InvalidOperationException("可添加的独立蒙版已满，请先移除一个蒙版。");
                string name=stem;
                for(int number=2;primaryName==name||layers.Any(l=>l.name==name);number++)name=stem+" "+number;
                var layer=new MaskLayer{id=Guid.NewGuid().ToString("N"),name=name,slot=slot,kind=kind,strokes=new Stroke[0]};
                layers.Add(layer);
                return layer;
            }
        }
        private static Stroke[] UnboundStrokes(Stroke[] marks)
        {
            if(marks.Any(s=>s.kind=="redEye"))throw new InvalidDataException("红眼标记必须绑定原片指纹。");
            if(marks.Any(s=>s.kind=="sampleRepair"))throw new InvalidDataException("取样修复必须绑定原片指纹。");
            if(marks.Any(UsesBrushV6))throw new InvalidDataException("贴边笔迹必须绑定原片指纹。");
            return marks;
        }
        private static MaskSet Legacy(Stroke[] marks)
        {
            return new MaskSet{version=1,primaryName="手绘蒙版 1",primaryId=Guid.Empty.ToString("N"),primary=UnboundStrokes(marks),layers=new List<MaskLayer>()};
        }
        private static JavaScriptSerializer MaskJson() { return new JavaScriptSerializer{MaxJsonLength=MaxMaskBytes,RecursionLimit=16}; }
        internal static string CheckedName(string name)
        {
            name=(name??"").Trim();
            if(name.Length<1||name.Length>40||name.Any(Char.IsControl))throw new ArgumentException("蒙版名称应为 1 至 40 个可见字符。");
            return name;
        }
        private static void ValidateLightStrokes(Stroke[] strokes)
        {
            if(strokes.Any(s=>s.kind!="maskPaint"&&s.kind!="maskErase"))throw new InvalidDataException("独立光影蒙版只接受画笔和擦除。");
        }
        internal static byte[] CheckPrediction(string data)
        {
            if(data==null||data.Length!=4*((PredictionSide*PredictionSide+2)/3))throw new InvalidDataException("识别选区尺寸无效。");
            byte[] bytes=Convert.FromBase64String(data);
            if(bytes.Length!=PredictionSide*PredictionSide||bytes.Count(b=>b>=128)<24||bytes.Count(b=>b<32)<24)
                throw new InvalidDataException("照片中没有可可靠识别的这类区域。");
            return bytes;
        }
        private static void ValidateSet(MaskSet set)
        {
            if(set==null||set.version<1||set.version>7||set.sourceHash==null||set.sourceHash.Length!=44||
                Convert.FromBase64String(set.sourceHash).Length!=32||set.layers==null||set.layers.Count>MaxExtraMasks)
                throw new InvalidDataException("独立蒙版资源或原片指纹无效。");
            Guid guid;
            var ids=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if(set.primaryName!=null)
            {
                set.primaryName=CheckedName(set.primaryName);
                if(!Guid.TryParseExact(set.primaryId,"N",out guid))throw new InvalidDataException("主蒙版身份无效。");
                ids.Add(set.primaryId);
            }
            set.primary=Validate(set.primary);
            BoundBrushSource(set.primary,set.sourceHash);
            if(set.version<5&&set.HasSampleRepair)throw new InvalidDataException("取样修复需要第 5 版蒙版资源。");
            var slots=new HashSet<int>();
            foreach(var layer in set.layers)
            {
                if(layer==null||layer.slot<1||layer.slot>MaxExtraMasks||!slots.Add(layer.slot)||
                    !Guid.TryParseExact(layer.id,"N",out guid)||!ids.Add(layer.id))throw new InvalidDataException("蒙版编号无效或重复。");
                layer.name=CheckedName(layer.name);
                bool predicted=StudioFinish.Predicted(layer.kind);
                if(layer.kind=="skin")
                {
                    if(set.version<3||!layer.person.HasValue||layer.faceSample==null||!layer.faceSample.Valid)
                        throw new InvalidDataException("肤色蒙版缺少可确认的人脸参照。");
                }
                else if(layer.faceSample!=null || layer.kind=="backdrop" && (set.version<3||layer.person.HasValue))
                    throw new InvalidDataException("肤色或背景参照与蒙版类型不符。");
                if(layer.range!=null&&!RangeKind(layer.kind))throw new InvalidDataException("此类蒙版不支持颜色或亮度范围。");
                if(GradientKind(layer.kind))
                {
                    if(set.version<2||layer.geometry==null||layer.alpha!=null)throw new InvalidDataException("渐变资源缺少有效的形状或版本。");
                    layer.geometry.Check(layer.kind);
                }
                else if(layer.geometry!=null)throw new InvalidDataException("此类蒙版不支持渐变形状。");
                else if(RangeKind(layer.kind))
                {
                    if(set.version<3||layer.range==null||layer.alpha==null||layer.alpha.Length!=4*((PredictionSide*PredictionSide+2)/3)||
                        Convert.FromBase64String(layer.alpha).Length!=PredictionSide*PredictionSide)throw new InvalidDataException("范围蒙版数据无效。");
                    layer.range.Check(layer.kind);
                }
                else if(predicted)CheckPrediction(layer.alpha);
                else if(layer.alpha!=null||layer.kind!="brush"&&!(set.version>=4&&StudioFinish.HandDetail(layer.kind)))
                    throw new InvalidDataException("不支持的蒙版来源。");
                if(layer.person.HasValue)
                {
                    if(!predicted||layer.person<0||layer.person>=BodyEdits.MaxPeople||layer.bodyScene==null||
                        layer.bodyScene.Length!=64||!layer.bodyScene.All(Uri.IsHexDigit))throw new InvalidDataException("选区引用的人物身份无效。");
                    CheckPrediction(layer.guard);
                }
                else if(layer.guard!=null||layer.bodyScene!=null)throw new InvalidDataException("选区缺少对应的人物。");
                layer.strokes=Validate(layer.strokes);
                BoundBrushSource(layer.strokes,set.sourceHash);
                ValidateLightStrokes(layer.strokes);
            }
            if(set.version<6&&set.HasV6Edits)throw new InvalidDataException("此选区需要第 6 版蒙版资源。");
            if(set.version<7&&set.HasV7Edits)throw new InvalidDataException("红眼标记需要第 7 版蒙版资源。");
        }
        private static MaskSet Decode(byte[] bytes)
        {
            var json=MaskJson();var raw=json.DeserializeObject(Encoding.UTF8.GetString(bytes));
            if(!(raw is IDictionary<string,object>))return Legacy(Validate(raw));
            object primary;
            var set=json.ConvertToType<MaskSet>(raw);
            if(((IDictionary<string,object>)raw).TryGetValue("primary",out primary)){
                CheckSampleFields(primary);ReadStrokeEdges(primary,set.primary);
            }
            object layers;
            if(set.layers!=null&&((IDictionary<string,object>)raw).TryGetValue("layers",out layers)&&layers is System.Collections.IEnumerable)
            {
                int index=0;
                foreach(object entry in (System.Collections.IEnumerable)layers)
                {
                    var fields=entry as IDictionary<string,object>;object strokes;
                    if(fields!=null&&set.layers[index]!=null&&fields.TryGetValue("strokes",out strokes))ReadStrokeEdges(strokes,set.layers[index].strokes);
                    index++;
                }
            }
            object areas;
            if(((IDictionary<string,object>)raw).TryGetValue("areas",out areas)&&areas!=null)set.areas=ReadAreas(areas);
            ValidateSet(set);
            if(set.HasGradients||set.HasV3Layers)
                foreach(var entry in (System.Collections.IEnumerable)((IDictionary<string,object>)raw)["layers"])
                {
                    var layer=(IDictionary<string,object>)entry;
                    string kind=(string)layer["kind"];
                    if(GradientKind(kind))ReadGradient(kind,layer["geometry"]);
                    if(RangeKind(kind))ReadRange(kind,layer["range"]);
                }
            return set;
        }
        internal static byte[] ResourceBytes(IDictionary<string,double> values)
        {
            string key=Hash(values);
            if(key==null)return null;
            using(var file=OpenMask(Resource(key)))return VerifiedBytes(file,key);
        }
        internal static MaskSet ReadSet(IDictionary<string,double> values)
        {
            var set=ReadOnlySet(values);
            return set.CachedPath==null?set:set.EditableCopy();
        }
        internal static Dictionary<string,double> PreviewWithoutMask(Dictionary<string,double> values,int slot)
        {
            if(slot<0)return values;
            if(!ReadOnlySet(values).Exists(slot))throw new ArgumentException("当前照片没有选中的蒙版。");
            var preview=new Dictionary<string,double>(values);
            if(slot==0)
            {
                // Legacy strokes can also drive repairs, warps and protection for other tools.
                foreach(string tone in ToneNames)preview[ToneKey(0,tone)]=0;
                preview["local_smooth"]=0;
            }
            else foreach(string key in values.Keys)if(ToneSlot(key)==slot)preview[key]=0;
            return preview;
        }
        internal static void CheckLockedTargets(IDictionary<string,double> before,IDictionary<string,double> after,IEnumerable<string> locks)
        {
            var slots=locks.Where(k=>ExtraKey(k)||k=="local_strength"||k=="local_temperature"||k=="local_saturation").Select(ToneSlot).Distinct().ToArray();
            if(slots.Length==0||Hash(before)==Hash(after))return;
            var previous=ReadOnlySet(before);var next=ReadOnlySet(after);
            if(slots.Any(n=>previous.IdFor(n)!=next.IdFor(n)))throw new InvalidOperationException("局部参数已锁定，不能把它转移到另一块蒙版。");
        }
        internal static Dictionary<string,double> SaveSet(MaskSet set,string hash)
        {
            set.RequireEditable();
            if(set.sourceHash!=null&&set.sourceHash!=hash)throw new InvalidOperationException("照片已被替换，已有蒙版属于另一份原片。");
            set.sourceHash=hash;set.version=set.HasV7Edits?7:set.HasV6Edits?6:set.HasSampleRepair?5:set.HasHandDetail?4:set.HasV3Layers?3:set.HasGradients?2:1;ValidateSet(set);
            return Store(Encoding.UTF8.GetBytes(MaskJson().Serialize(set)));
        }
        internal static void ImportSet(byte[] bytes,string expected,int schema=13)
        {
            if(bytes==null||bytes.Length>MaxMaskBytes||Digest(bytes)!=expected)
                throw new InvalidDataException("随附的独立蒙版或指纹无效。");
            var set=Decode(bytes);
            if(set.sourceHash==null)throw new InvalidDataException("随附的独立蒙版缺少原片指纹。");
            if(schema<13&&set.HasV7Edits)throw new InvalidDataException("红眼标记需要第 13 版交换配方。");
            if(schema<12&&set.HasV6Edits)throw new InvalidDataException("此选区需要第 12 版交换配方。");
            if(schema<11&&set.HasSampleRepair)throw new InvalidDataException("取样修复需要第 11 版交换配方。");
            if(schema<10&&set.HasHandDetail)throw new InvalidDataException("镜片和精细衣料蒙版需要第 10 版交换配方。");
            if(schema<8&&set.HasV3Layers)throw new InvalidDataException("颜色、亮度范围和人像修整选区需要第 8 版交换配方。");
            if(schema<7&&set.HasGradients)throw new InvalidDataException("线性与径向渐变需要第 7 版交换配方。");
            Store(bytes);
        }
        internal static string Pack(IDictionary<string,double> values)
        {
            byte[] bytes=ResourceBytes(values);
            using(var output=new MemoryStream())
            {
                using(var gzip=new GZipStream(output,CompressionMode.Compress,true))gzip.Write(bytes,0,bytes.Length);
                return Convert.ToBase64String(output.ToArray());
            }
        }
        internal static void Unpack(string content,string expected,int schema=13)
        {
            byte[] compressed=Convert.FromBase64String(content);
            using(var input=new GZipStream(new MemoryStream(compressed),CompressionMode.Decompress))
            using(var raw=new MemoryStream())
            {
                var block=new byte[8192];int count;
                while((count=input.Read(block,0,block.Length))>0)
                {
                    if(raw.Length+count>MaxMaskBytes)throw new InvalidDataException("解包后的独立蒙版超过大小限制。");
                    raw.Write(block,0,count);
                }
                ImportSet(raw.ToArray(),expected,schema);
            }
        }
        internal static void ValidateSource(MaskSet set,string path)
        {
            if(set.sourceHash!=null&&BodyAssets.SourceDigest(path)!=BitConverter.ToString(Convert.FromBase64String(set.sourceHash)).Replace("-","").ToLowerInvariant())
                throw new InvalidOperationException("原片内容已变化，当前蒙版属于另一份原片。");
        }
        internal static double SamplePrediction(byte[] alpha,double x,double y)
        {
            x=Clamp(x*PredictionSide-.5,0,PredictionSide-1);y=Clamp(y*PredictionSide-.5,0,PredictionSide-1);
            int ix=(int)x,iy=(int)y,x2=Math.Min(PredictionSide-1,ix+1),y2=Math.Min(PredictionSide-1,iy+1);
            double fx=x-ix,fy=y-iy;
            return ((alpha[iy*PredictionSide+ix]*(1-fx)+alpha[iy*PredictionSide+x2]*fx)*(1-fy)+
                (alpha[y2*PredictionSide+ix]*(1-fx)+alpha[y2*PredictionSide+x2]*fx)*fy)/255;
        }
        internal sealed class ToneRegion
        {
            internal readonly int Width,Height;
            internal readonly System.Windows.Int32Rect Area;
            internal readonly Dictionary<int,StudioFinish.SkinReference> SkinReferences;
            internal readonly int MeasuringSlot;
            internal ToneRegion(int w,int h,System.Windows.Int32Rect rect,Dictionary<int,StudioFinish.SkinReference> skinReferences=null,int measuringSlot=0)
            {Width=w;Height=h;Area=rect;SkinReferences=skinReferences;MeasuringSlot=measuringSlot;}
        }
        internal static bool SupportsToneRegion(IDictionary<string,double> values)
        {
            if(Hash(values)==null)return true;
            var set=ReadOnlySet(values);
            if(Val(values,"local_smooth",0)!=0||values.Any(p=>p.Value!=0&&!StudioFinish.FabricGuard(p.Key)&&(StudioFinish.IsControl(p.Key)||StudioFinish.DetailControl(p.Key))))return false;
            return !set.primary.Any(s=>s.strength>0&&(IsRepair(s.kind)||s.kind=="warp"&&Val(values,"local_warp",100)!=0));
        }
        internal static MaskSet ViewportMask(IDictionary<string,double> values)
        {
            return Hash(values)==null?null:ReadOnlySet(values);
        }
        internal static bool SupportsNeighborhood(MaskSet set,IDictionary<string,double> values)
        {
            return set==null||!set.HasV7Edits&&!set.primary.Any(s=>s.strength>0&&IsRepair(s.kind));
        }
        internal static System.Windows.Int32Rect RequiredInput(MaskSet set,IDictionary<string,double> values,
            int width,int height,System.Windows.Int32Rect requested,CancellationToken cancel)
        {
            int shortEdge=Math.Min(width,height);
            int halo=set.primaryName!=null&&Val(values,"local_smooth",0)!=0?1:0;
            foreach(var layer in set.layers)
            {
                if(Strength(values,layer.slot)==0)continue;
                string finishKind=layer.kind=="fabric"?"clothes":layer.kind;
                double finish=Val(values,ToneKey(layer.slot,"finish"),0);
                if(finish!=0)halo+=StudioFinish.FinishRadius(finishKind,shortEdge);
                halo+=PersonalRetouch.BlurRadius(shortEdge,Val(values,ToneKey(layer.slot,"backgroundBlur"),0));
                double broad=Val(values,ToneKey(layer.slot,"broadfold"),0);
                double fine=Val(values,ToneKey(layer.slot,"finefold"),0);
                if(broad!=0||fine!=0)halo+=StudioFinish.FoldRadius(finishKind,shortEdge,broad);
                if(Val(values,ToneKey(layer.slot,"lensglare"),0)!=0||
                    (finish!=0||broad!=0||fine!=0)&&Val(values,ToneKey(layer.slot,"fabricguard"),0)!=0)
                    halo+=StudioFinish.DetailRadius(shortEdge);
            }
            int padX=halo,padY=halo;
            double warp=Val(values,"local_warp",100)/100;
            if(warp>0&&set.primary.Any(s=>s.strength>0&&s.kind=="warp"))
            {
                // Interpolated lattice offsets cannot exceed their largest node. Source-mask feedback
                // only reduces displacement. Include both samples of the final bilinear read.
                var grid=CoverageGrid(set,0,width,height,cancel,false);
                double maxX=0,maxY=0;
                for(int i=0;i<grid.Dx.Length;i++)
                {
                    maxX=Math.Max(maxX,Math.Abs(grid.Dx[i]));maxY=Math.Max(maxY,Math.Abs(grid.Dy[i]));
                }
                padX+=1+(int)Math.Ceiling(Math.Min(shortEdge*.08,maxX*warp));
                padY+=1+(int)Math.Ceiling(Math.Min(shortEdge*.08,maxY*warp));
            }
            int left=Math.Max(0,requested.X-padX),top=Math.Max(0,requested.Y-padY);
            return new System.Windows.Int32Rect(left,top,Math.Min(width,requested.X+requested.Width+padX)-left,
                Math.Min(height,requested.Y+requested.Height+padY)-top);
        }
        internal static BitmapSource ApplySet(BitmapSource source,IDictionary<string,double> values,MaskSet set,CancellationToken cancel,BitmapSource rangeReference=null,ToneRegion region=null)
        {
            var primary=values;
            if(set.primaryName==null)primary=new Dictionary<string,double>{{"local_warp",Val(values,"local_warp",100)}};
            var result=ApplyStrokes(source,primary,set.primary,cancel,set,0,null,null,null,region);
            Func<int,int,double> background=null;
            int outputMode=0;
            double outputStrength=1;
            foreach(var layer in set.layers)
            {
                var beforeLayer=result;
                double strength=Strength(values,layer.slot);
                if(strength==0)continue;
                double finish=Val(values,ToneKey(layer.slot,"finish"),0);
                double fine=Val(values,ToneKey(layer.slot,"finefold"),0),broad=Val(values,ToneKey(layer.slot,"broadfold"),0);
                double skin=Val(values,ToneKey(layer.slot,"skintone"),0);
                double glare=Val(values,ToneKey(layer.slot,"lensglare"),0);
                double blur=Val(values,ToneKey(layer.slot,"backgroundBlur"),0);
                int setting=(int)Val(values,ToneKey(layer.slot,"backgroundOutput"),0);
                if(blur>0||setting>0)
                {
                    int w=region==null?result.PixelWidth:region.Width,h=region==null?result.PixelHeight:region.Height;
                    var coverage=BackdropCoverage(layer,w,h,region,cancel);
                    if(setting>0){background=coverage;outputMode=setting;outputStrength=strength/100;}
                    if(blur>0)result=PersonalRetouch.Blur(result,coverage,PersonalRetouch.BlurRadius(Math.Min(w,h),blur),blur,cancel);
                }
                if(finish!=0||fine!=0||broad!=0||skin!=0||glare!=0)
                {
                    int fullWidth=region==null?result.PixelWidth:region.Width, fullHeight=region==null?result.PixelHeight:region.Height;
                    int x0=region==null?0:region.Area.X,y0=region==null?0:region.Area.Y;
                    Func<int,int,double> coverage;
                    if(StudioFinish.HandDetail(layer.kind))
                    {
                        var brush=new DetailBrush(layer.strokes,fullWidth,fullHeight,cancel);
                        coverage=(x,y)=>brush.At(x+x0,y+y0);
                    }
                    else
                    {
                        var field=CoverageGrid(set,layer.slot,fullWidth,fullHeight,cancel,false);
                        coverage=(x,y)=>field.At(field.Mask,x+x0,y+y0)*(1-field.At(field.Protect,x+x0,y+y0));
                    }
                    int shortEdge=Math.Min(fullWidth,fullHeight);
                    StudioFinish.SkinReference measured=null;
                    if(layer.kind=="skin"&&(finish!=0||skin!=0)&&region!=null&&region.SkinReferences!=null)
                    {
                        if(region.MeasuringSlot==layer.slot)
                        {
                            var bounds=layer.faceSample.Bounds(fullWidth,fullHeight);
                            bounds.X-=x0;bounds.Y-=y0;
                            region.SkinReferences.Add(layer.slot,StudioFinish.MeasureSkin(result,layer.faceSample,bounds,coverage,finish,skin,cancel));
                            return result;
                        }
                        measured=region.SkinReferences[layer.slot];
                    }
                    string finishKind=layer.kind=="fabric"?"clothes":layer.kind;
                    var beforeFolds=result;
                    result=StudioFinish.Apply(result,finishKind,finish,coverage,layer.faceSample,cancel,shortEdge,measured);
                    result=StudioFinish.Refine(result,finishKind,fine,broad,skin,coverage,layer.faceSample,cancel,shortEdge,measured);
                    if(StudioFinish.Garment(layer.kind))
                        result=StudioFinish.SurfaceDetail(beforeFolds,result,Val(values,ToneKey(layer.slot,"fabricguard"),0),coverage,shortEdge,cancel);
                    if(glare!=0)result=StudioFinish.SurfaceDetail(result,null,glare,coverage,shortEdge,cancel);
                }
                var tone=new Dictionary<string,double>();
                foreach(string key in ToneNames)
                {
                    double value=Val(values,ToneKey(layer.slot,key),0);
                    if(value!=0)tone.Add("local_"+key,value);
                }
                if(tone.Count>0)
                {
                    var gradient=layer.geometry==null?null:new GradientField(layer.kind,layer.geometry,region==null?source.PixelWidth:region.Width,region==null?source.PixelHeight:region.Height);
                    var range=layer.range==null?null:new RangeField(layer.kind,layer.range);
                    result=ApplyStrokes(result,tone,layer.strokes,cancel,set,layer.slot,
                        gradient,range,rangeReference??source,region);
                }
                result=LayerStrength(beforeLayer,result,strength,cancel);
            }
            if(background!=null)result=ReplaceBackdrop(result,background,outputMode,outputStrength,cancel);
            return CloseRedactions(result,set.areas,region,cancel);
        }
    }
}
