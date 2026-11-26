using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace AIPhotoStudio
{
    public sealed partial class StudioWorkspace
    {
        private static void CheckDetailLocks(IDictionary<string,double> values,IEnumerable<string> locks)
        {
            var detail=locks.Where(k=>StudioFinish.DetailControl(k)||LocalEdits.LayerOutputKey(k)).ToArray();
            if(detail.Length==0)return;
            if(LocalEdits.Hash(values)==null)throw new InvalidOperationException("请先保存需要锁定的蒙版。");
            var set=LocalEdits.ReadSet(values);
            foreach(string key in detail)
            {
                int slot=LocalEdits.ToneSlot(key);
                if(!set.Exists(slot)||!LocalEdits.StrengthKey(key)&&(LocalEdits.BackdropKey(key)?set.Layer(slot).kind!="backdrop":!StudioFinish.Allows(set.Layer(slot).kind,key)))
                    throw new InvalidOperationException("请先选择与此修整参数对应的蒙版，再锁定强度。");
            }
        }
        private static void CheckMaskSlots(IDictionary<string,double> values)
        {
            int[] slots=values.Where(p=>p.Value!=LocalEdits.LayerDefault(p.Key)&&(LocalEdits.ExtraKey(p.Key)||p.Key=="local_strength")).Select(p=>LocalEdits.ToneSlot(p.Key)).Distinct().ToArray();
            if(slots.Length==0)return;
            if(LocalEdits.Hash(values)==null)throw new InvalidOperationException("请先保存需要调整的蒙版。");
            var set=LocalEdits.ReadSet(values);
            if(slots.Any(slot=>!set.Exists(slot)))throw new InvalidOperationException("请先创建对应的独立蒙版。");
            var outputs=values.Where(p=>p.Value!=0&&LocalEdits.BackdropKey(p.Key)).ToArray();
            if(outputs.Length>1)throw new InvalidOperationException("请先将另一块背景蒙版的背景替换设为保留原图。");
            if(outputs.Any(p=>set.Layer(LocalEdits.ToneSlot(p.Key)).kind!="backdrop"||set.sourceHash==null))
                throw new InvalidOperationException("请先在当前照片上确认背景蒙版。");
            foreach(var entry in values.Where(p=>p.Value!=0&&(StudioFinish.IsControl(p.Key)||StudioFinish.DetailControl(p.Key))))
                if(!StudioFinish.Allows(set.Layer(LocalEdits.ToneSlot(entry.Key)).kind,entry.Key))
                    throw new InvalidOperationException("该修整参数与当前蒙版的类型不符。");
        }
    }
    internal sealed partial class StudioBridge
    {
        private sealed class MaskTicket
        {
            internal string Id,Hash,Token,Kind,Scene,Alpha,Guard;
            internal GarmentDivision Garment;
            internal int Person=-1;
            internal long Revision;
            internal BodyGeometry Subject;
        }
        private MaskTicket maskTicket;
        private static int MaskSlot(Dictionary<string,object> args,string key="maskSlot")
        {
            object raw;
            if(!args.TryGetValue(key,out raw))return 0;
            if(!(raw is int)||((int)raw)<0||((int)raw)>LocalEdits.MaxExtraMasks)throw new ArgumentException("蒙版编号无效。");
            return (int)raw;
        }
        private static int BypassedMask(Dictionary<string,object> args)
        {
            return args.ContainsKey("bypassMaskSlot")?MaskSlot(args,"bypassMaskSlot"):-1;
        }
        private object MaskData(Dictionary<string,object> args)
        {
            Selected(args);
            var set=LocalEdits.ReadSet(work.ValuesFor(TextOf(args,"id")));
            LocalEdits.ValidateSource(set,work.PathFor(TextOf(args,"id")));
            var masks=new List<object>();
            if(set.primaryName!=null)masks.Add(new {slot=0,name=set.primaryName,kind="brush"});
            foreach(var layer in set.layers)masks.Add(new {slot=layer.slot,name=layer.name,kind=layer.kind,person=layer.person});
            if(args.ContainsKey("summary")&&Object.Equals(args["summary"],true))return new {masks=masks,hasPrimaryStrokes=set.primary.Length>0};
            int slot=MaskSlot(args);
            bool structure=args.ContainsKey("structure")&&Object.Equals(args["structure"],true);
            if(structure)slot=0;
            else if(!set.Exists(slot))slot=set.primaryName!=null?0:set.layers.Select(l=>l.slot).DefaultIfEmpty(-1).First();
            var strokes=slot<0?new LocalEdits.Stroke[0]:set.Strokes(slot);
            return new {masks=masks,slot=slot,strokes=strokes,alpha=slot>0?set.Layer(slot).alpha:null,guard=slot>0?set.Layer(slot).guard:null,
                geometry=slot>0?set.Layer(slot).geometry:null,range=slot>0?set.Layer(slot).range:null,structure=set.primary};
        }

        private async Task<Dictionary<string,object>> PlanSelectedMask(Dictionary<string,object> args,Dictionary<string,double> values,
            string[] locks,Dictionary<string,object> analysis,string[] history,int width,int height)
        {
            string text=TextOf(args,"text"),scope=TextOf(args,"scope");
            int slot=MaskSlot(args);
            bool namedScope=scope=="独立蒙版";
            var set=LocalEdits.ReadSet(values);
            var layer=slot>0&&set.Exists(slot)?set.Layer(slot):null;
            // "Warm the face" may only address a mask identified as that face.
            // Without a matching mask it must never fall through to global white balance.
            var face=Regex.Match(text,"左脸|右脸|脸部|面部|人脸|脸(?!颈|型)");
            if(face.Success&&Regex.IsMatch(text,"色温|饱和|回暖|调暖|调冷|变暖|变冷|偏暖|偏冷|暖一|冷一|更暖|更冷|暖些|冷些"))
            {
                string name=slot==0?set.primaryName:layer==null?null:layer.name;
                bool faceMask=name!=null&&(text.Contains(name)&&name.Contains(face.Value)||
                    Regex.IsMatch(name,"^(脸|脸部|面部|人脸)(蒙版|选区)?$")&&face.Value!="左脸"&&face.Value!="右脸");
                if(!faceMask)throw new InvalidOperationException("请先画出脸部蒙版，命名为「脸」并选中它，再调整脸部冷暖或饱和度。");
                text=text.Replace(text.Contains(name)?name:face.Value,"当前蒙版");
            }
            if(slot==0&&set.primaryName!=null&&set.primaryName.Length>1)text=text.Replace(set.primaryName,"当前蒙版");
            bool skinRegion=Regex.IsMatch(text,"脸颈[与和]手臂|(?:肤色|皮肤)(?:选区|蒙版)");
            bool backdrop=text.Contains("背景布");
            bool explicitRegion=skinRegion||backdrop||Regex.IsMatch(text,"蒙版|选区|手绘|画的区域|渐变|椭圆|圈的区域|头发|发丝|衣服|衣物|服装|颜色范围|亮度范围");
            string repairText=Regex.Replace(text.Trim(),@"^(?:请)?(?:把)?(?:这块)?(?:镜片|眼镜)的?反光(?:减弱|淡化|减轻|压低)(?:一点)?","淡化镜片反光");
            var repair=Regex.Match(repairText,@"^(?:请)?(?:把)?(?:当前|这块|选中)?(?:蒙版|选区)?(?:的)?(淡化衣褶|衣服去皱|淡化细褶|淡化粗褶|柔和肤色过渡|淡化背景布折痕|背景布去皱|背景虚化|虚化背景|协调脸颈与手臂肤色|协调脸颈肤色|统一脸颈肤色|(?:淡化|减弱|减轻)(?:镜片|眼镜)反光|保护(?:织纹|蕾丝)(?:与|和)亮片)(?:强度)?\s*(?:设为|调到|到)?\s*(\d{1,3})?\s*%?[。！]?$");
            if(repair.Success)
            {
                string action=repair.Groups[1].Value;
                bool fold=action=="淡化细褶"||action=="淡化粗褶";
                bool glare=action.Contains("反光"),fabricGuard=action.StartsWith("保护",StringComparison.Ordinal);
                string required=glare?"lens":action.Contains("肤色")?"skin":action.Contains("背景")||fold&&layer!=null&&layer.kind=="backdrop"?"backdrop":"clothes";
                if(scope!=""&&scope!="all"&&scope!="手绘局部"&&!namedScope)throw new InvalidOperationException("请选择独立蒙版或全部参数范围。");
                if(layer==null||(required=="clothes"?!StudioFinish.Garment(layer.kind):layer.kind!=required))
                    throw new InvalidOperationException("请先在蒙版中选中"+StudioFinish.Name(required)+"选区。");
                if(glare&&!LocalEdits.HasPaint(values,width,height,slot))throw new InvalidOperationException("请先涂出镜片上的轻度反光并保存笔迹。");
                if(layer.person.HasValue&&analysis.ContainsKey("subjectNumber")&&(int)analysis["subjectNumber"]>0&&(int)analysis["bodyPersonIndex"]!=layer.person.Value)
                    throw new InvalidOperationException("请先选中这个蒙版对应的人物。");
                string suffix=action.Contains("虚化")?"backgroundBlur":glare?"lensglare":fabricGuard?"fabricguard":fold?(action=="淡化细褶"?"finefold":"broadfold"):action=="柔和肤色过渡"?"skintone":"finish";
                string key=LocalEdits.ToneKey(slot,suffix);
                if(locks.Contains(key))throw new InvalidOperationException("该蒙版的修整强度已锁定。");
                int amount=repair.Groups[2].Success?Int32.Parse(repair.Groups[2].Value):45;
                if(amount>100)throw new ArgumentException("修整强度范围为 0 到 100。");
                return new Dictionary<string,object>{{"label",layer.name+" · "+action},{"message","已按当前蒙版生成预览。"},{"values",new Dictionary<string,double>{{key,amount}}}};
            }
            if(Regex.IsMatch(text,"(?:镜片|眼镜).*反光"))
                throw new InvalidOperationException("请先涂好镜片蒙版，再输入「淡化镜片反光到 45%」。");
            bool byName=layer!=null&&layer.name.Length>1&&text.Contains(layer.name);
            bool repeat=layer!=null&&history.Length>0&&history.Last().StartsWith("local_m"+slot+"_",StringComparison.Ordinal)&&
                Regex.IsMatch(text,@"^(?:请)?(?:再多一点|再来一点|再强一点|继续一点|恢复上一步|撤销刚才的?调整|再少一点)[。！]?$");
            if(layer==null||!(namedScope||scope=="手绘局部"||explicitRegion||byName||repeat))
            {
                if((namedScope&&(slot!=0||!set.Exists(0)))||skinRegion||backdrop||Regex.IsMatch(text,"渐变|椭圆|圈的区域|颜色范围|亮度范围"))
                    throw new InvalidOperationException("请先在蒙版面板中选中要调整的独立蒙版。");
                analysis["hasPaintMask"]=LocalEdits.HasPaint(values,width,height);
                return await Planner.PlanAsync(channelDirectory,text,namedScope?"手绘局部":scope,values,locks,analysis,history);
            }
            if(scope!="all"&&scope!=""&&!namedScope&&scope!="手绘局部")
                throw new InvalidOperationException("请在全部参数或独立蒙版范围内调整这个选区。");
            if(layer.person.HasValue && analysis.ContainsKey("subjectNumber") && (int)analysis["subjectNumber"]>0 &&
                (int)analysis["bodyPersonIndex"]!=layer.person.Value)
                throw new InvalidOperationException("此蒙版属于另一位人物，请先选择对应的人物及蒙版。");
            bool hair=Regex.IsMatch(text,"头发|发丝"),clothes=Regex.IsMatch(text,"衣服|衣物|服装");
            if((hair&&layer.kind!="hair")||(clothes&&!StudioFinish.Garment(layer.kind)))
                throw new InvalidOperationException("请在蒙版面板中选中对应的头发或衣服蒙版，再调整已识别的区域。");
            if(skinRegion&&layer.kind!="skin"||backdrop&&layer.kind!="backdrop")
                throw new InvalidOperationException("请先在蒙版面板中选中对应的人物肤色或背景布蒙版。");
            if(Regex.IsMatch(text,"柔化|磨皮|色相|换色"))throw new InvalidOperationException("此蒙版支持局部曝光、阴影、高光、色温和饱和度。");
            string request=text.Replace(layer.name,"当前蒙版");
            if(Regex.IsMatch(request,"线性渐变|直线渐变|拉的渐变")&&layer.kind!="linear"||
                Regex.IsMatch(request,"径向渐变|椭圆|圈的区域")&&layer.kind!="radial"||
                request.Contains("渐变")&&!LocalEdits.GradientKind(layer.kind))
                throw new InvalidOperationException("请先在蒙版面板中选中对应的渐变。");
            if(LocalEdits.GradientKind(layer.kind))
                request=Regex.Replace(request,"线性渐变|直线渐变|径向渐变|拉的渐变|渐变|椭圆|圈的区域","当前蒙版");
            if(request.Contains("颜色范围")&&layer.kind!="colorRange"||request.Contains("亮度范围")&&layer.kind!="luminanceRange")
                throw new InvalidOperationException("请在蒙版面板中选中相应的颜色或亮度范围。");
            request=Regex.Replace(request,"颜色范围|亮度范围","当前蒙版");
            const string personPattern=@"人物\s*(\d+)|第\s*(\d+)\s*(?:位|个)人";
            foreach(Match match in Regex.Matches(request,personPattern))
            {
                string number=match.Groups[1].Success?match.Groups[1].Value:match.Groups[2].Value;
                int target;
                if(!Int32.TryParse(number,out target)||!layer.person.HasValue||layer.person.Value+1!=target)
                    throw new InvalidOperationException("指令中的人物与选中蒙版不一致，请先切换到对应的人物蒙版。");
            }
            request=Regex.Replace(request,personPattern,"");
            request=Regex.Replace(request,"头发|发丝|衣服|衣物|服装|脸颈[与和]手臂(?:的?肤色)?|(?:肤色|皮肤)(?:选区|蒙版)|背景布","当前蒙版");
            var aliases=new Dictionary<string,double>(values);
            var scopedLocks=new HashSet<string>(locks.Where(k=>!k.StartsWith("local_",StringComparison.Ordinal)));
            scopedLocks.Add("local_smooth");
            foreach(string tone in LocalEdits.ToneNames)
            {
                string actual=LocalEdits.ToneKey(slot,tone),alias=LocalEdits.ToneKey(0,tone);
                aliases[alias]=values[actual];
                if(locks.Contains(actual))scopedLocks.Add(alias);
            }
            string prefix="local_m"+slot+"_";
            var scopedHistory=history.Select(item=>String.Join(";",item.Split(';').Where(p=>p.StartsWith(prefix,StringComparison.Ordinal))
                .Select(p=>"local_"+p.Substring(prefix.Length)))).Where(item=>item.Length>0).ToArray();
            analysis["hasPaintMask"]=LocalEdits.HasPaint(values,width,height,slot);
            var plan=await Planner.PlanAsync(channelDirectory,request,"手绘局部",aliases,scopedLocks.ToArray(),analysis,scopedHistory);
            var result=Json().ConvertToType<Dictionary<string,double>>(plan["values"]);
            if(result.Keys.Any(k=>!LocalEdits.ToneNames.Any(t=>k==LocalEdits.ToneKey(0,t))))throw new InvalidOperationException("提案超出了选中蒙版的光影范围。");
            plan["values"]=result.ToDictionary(p=>prefix+p.Key.Substring("local_".Length),p=>p.Value);
            plan["label"]=layer.name+" · "+Convert.ToString(plan["label"]);
            plan["message"]="仅调整「"+layer.name+"」覆盖的范围。";
            return plan;
        }
        private async Task<object> MaskInput(Dictionary<string,object> args)
        {
            Selected(args);
            string id=TextOf(args,"id"),kind=TextOf(args,"kind");
            if(!StudioFinish.Predicted(kind))throw new ArgumentException("请选择支持识别的区域。");
            var ticket=new MaskTicket {Id=id,Revision=Convert.ToInt64(args["revision"]),Kind=kind,Token=Guid.NewGuid().ToString("N")};
            maskTicket=ticket;
            string path=work.PathFor(id);
            ticket.Hash=await Task.Run(()=>work.OriginalHash(id));
            object requested;
            if(args.TryGetValue("personIndex",out requested)&&requested!=null)
            {
                var values=work.ValuesFor(id);
                ticket.Person=BodyPersonIndex(args,values,true);
                ticket.Subject=BodyAssets.ResolvePerson(values,ticket.Person);
                if(ticket.Subject==null||!ticket.Subject.Available.Any(v=>v)||!BodyAssets.MatchesSource(values,ticket.Hash))
                    throw new InvalidOperationException("请先在人体美型中识别当前原片的这个人物。");
                ticket.Scene=BodyAssets.ResourceHash(values);
            }
            if(kind=="skin")StudioFinish.FaceSample.From(ticket.Subject);
            if(args.ContainsKey("garmentPart"))
            {
                if(kind!="clothes")throw new ArgumentException("分衣物只适用于衣服选区。");
                ticket.Garment=GarmentDivision.From(ticket.Subject,TextOf(args,"garmentPart"),
                    args.ContainsKey("garmentLine")?(double?)StudioWorkspace.Number(args["garmentLine"]):null);
            }
            if(kind=="backdrop"&&ticket.Subject!=null)throw new ArgumentException("背景布选区需要使用整张照片。");
            var data=Map(await EncodePreview(await Render(path,work.NeutralFor(id,path,Convert.ToInt64(args["revision"])),768)));
            Selected(args);
            if(ticket!=maskTicket||ticket.Hash!=await Task.Run(()=>work.OriginalHash(id)))throw new InvalidOperationException("照片或选区识别请求已经变化。");
            return new {token=ticket.Token,image=data["image"],box=ticket.Subject==null?null:ticket.Subject.Box};
        }
        private MaskTicket CurrentMaskTicket(Dictionary<string,object> args)
        {
            Selected(args);
            var ticket=maskTicket;
            if(ticket==null||ticket.Id!=TextOf(args,"id")||ticket.Token!=TextOf(args,"token")||
                ticket.Revision!=Convert.ToInt64(args["revision"])||ticket.Hash!=work.OriginalHash(ticket.Id))
                throw new InvalidOperationException("请重新识别当前原片并确认选区。");
            return ticket;
        }
        private object MaskPreview(Dictionary<string,object> args)
        {
            var ticket=CurrentMaskTicket(args);
            if(ticket.Alpha!=null)throw new InvalidOperationException("此识别预览已经生成，请重新识别后再替换。");
            var alpha=LocalEdits.CheckPrediction(TextOf(args,"alpha"));
            if(ticket.Subject!=null)
            {
                int side=LocalEdits.PredictionSide,visible=0;
                var guard=new byte[side*side];
                for(int y=0;y<side;y++)for(int x=0;x<side;x++)
                {
                    int i=y*side+x;double u=(x+.5)/side,v=(y+.5)/side;
                    var subject=ticket.Subject;
                    double own=BodyAssets.Probability(subject.Mask,u,v);
                    double other=subject.OtherPeople==null?0:BodyAssets.Probability(subject.OtherPeople,u,v);
                    // Ambiguous contours belong to neither selection. This gate remains in force for later brush refinements.
                    double coverage=Math.Max(0,Math.Min(1,(own-other-.055)/.69))*Math.Max(0,Math.Min(1,(.28-other)/.19));
                    if(ticket.Garment!=null)coverage*=ticket.Garment.At(u,v);
                    guard[i]=(byte)Math.Round(255*coverage);
                    if(guard[i]==0)alpha[i]=0;
                    if(alpha[i]*coverage>=128)visible++;
                }
                if(visible<24)throw new InvalidOperationException("这个人物的该区域太小或被遮挡，请使用画笔手动选择。");
                ticket.Guard=Convert.ToBase64String(guard);
            }
            ticket.Alpha=Convert.ToBase64String(alpha);
            return new {alpha=ticket.Alpha,guard=ticket.Guard};
        }
        private object ManageMask(Dictionary<string,object> args)
        {
            var doc=Selected(args);
            string id=TextOf(args,"id"),operation=TextOf(args,"operation"),hash=work.OriginalHash(id);
            var values=work.ValuesFor(id);
            var set=LocalEdits.ReadSet(values);
            string requestedKind=TextOf(args,"kind");
            bool newGradient=operation=="add"&&LocalEdits.GradientKind(requestedKind);
            bool newRange=operation=="add"&&LocalEdits.RangeKind(requestedKind);
            int slot=newGradient||newRange?0:MaskSlot(args);string label;
            var reset=new Dictionary<string,double>();
            if(newGradient||operation=="gradient")
            {
                object raw;
                if(!args.TryGetValue("geometry",out raw))throw new ArgumentException("请在照片上画出渐变。");
                var geometry=LocalEdits.ReadGradient(requestedKind,raw);
                var layer=set.SetGradient(requestedKind,geometry,newGradient,slot);
                slot=layer.slot;
                label=(newGradient?"新建蒙版 · ":"编辑渐变 · ")+layer.name;
            }
            else if(newRange||operation=="range")
            {
                var ticket=CheckedRangeTicket(args);
                if(ticket.Hash!=hash)throw new InvalidOperationException("原片内容已变化，请重新取样。");
                var range=LocalEdits.ReadRange(requestedKind,args["range"]);
                int covered;
                string alpha=ticket.Sampler.Preview(requestedKind,range,out covered);
                if(covered==0)throw new InvalidOperationException("当前范围没有可见覆盖，请重新取样或扩大范围。");
                var layer=set.SetRange(requestedKind,range,alpha,newRange,slot);
                slot=layer.slot;label=(newRange?"新建蒙版 · ":"编辑范围 · ")+layer.name;
            }
            else if(operation=="add")
            {
                string kind=TextOf(args,"kind");
                string alpha=null,guard=null,scene=null,garmentName=null;int? person=null;
                StudioFinish.FaceSample faceSample=null;
                bool cutout=kind=="segmentCutout";
                bool fromSegment=cutout||kind=="segmentBackdrop";
                if(fromSegment)
                {
                    var foreground=SegmentAssets.Read(values);
                    if(foreground==null)throw new InvalidOperationException("请先点选并应用前景与背景分区。");
                    alpha=SubjectSelection.BackgroundSelection(foreground,LocalEdits.PredictionSide);
                    LocalEdits.CheckPrediction(alpha);
                    kind="backdrop";
                }
                else if(StudioFinish.Predicted(kind))
                {
                    var ticket=CurrentMaskTicket(args);
                    if(ticket.Kind!=kind||ticket.Alpha==null)
                        throw new InvalidOperationException("请重新识别当前原片并确认选区。");
                    alpha=ticket.Alpha;guard=ticket.Guard;scene=ticket.Scene;
                    if(ticket.Garment!=null)garmentName=ticket.Garment.Name;
                    if(kind=="skin")faceSample=StudioFinish.FaceSample.From(ticket.Subject);
                    if(ticket.Person>=0)person=ticket.Person;
                }
                else if(kind!="brush"&&!StudioFinish.HandDetail(kind))throw new ArgumentException("蒙版类型无效。");
                bool reusePrimary=kind=="brush"&&set.primaryName==null&&set.primary.Length==0;
                slot=reusePrimary?0:Enumerable.Range(1,LocalEdits.MaxExtraMasks).FirstOrDefault(n=>!set.Exists(n));
                if(!reusePrimary&&slot==0)throw new InvalidOperationException("可添加的独立蒙版已满，请先移除一个蒙版。");
                string stem=cutout?"抠图背景":fromSegment?"虚化背景":kind=="brush"?"手绘蒙版":garmentName??StudioFinish.Name(kind);
                if(person.HasValue)stem="人物 "+(person.Value+1)+" · "+stem;
                string name=stem+(kind=="brush"?" "+(slot+1):"");
                var names=set.layers.Select(l=>l.name).Concat(new[]{set.primaryName}).ToList();
                for(int n=2;names.Contains(name);n++)name=stem+" "+n;
                string identity=Guid.NewGuid().ToString("N");
                if(reusePrimary){set.primaryName=name;set.primaryId=identity;}
                else set.layers.Add(new LocalEdits.MaskLayer{id=identity,slot=slot,name=name,kind=kind,alpha=alpha,guard=guard,person=person,bodyScene=scene,faceSample=faceSample,strokes=new LocalEdits.Stroke[0]});
                if(kind=="fabric")reset[LocalEdits.ToneKey(slot,"fabricguard")]=100;
                if(cutout)reset[LocalEdits.ToneKey(slot,"backgroundOutput")]=1;
                else if(fromSegment)reset[LocalEdits.ToneKey(slot,"backgroundBlur")]=40;
                label="新建蒙版 · "+name;
            }
            else
            {
                if(!set.Exists(slot))throw new InvalidOperationException("选中的蒙版已经变化。");
                var layer=slot==0?null:set.Layer(slot);
                string name=slot==0?set.primaryName:layer.name;
                if(operation=="rename")
                {
                    string next=LocalEdits.CheckedName(TextOf(args,"name"));
                    if((slot!=0&&set.primaryName==next)||set.layers.Any(l=>l.slot!=slot&&l.name==next))
                        throw new ArgumentException("此照片已有同名蒙版。");
                    if(slot==0)set.primaryName=next;else layer.name=next;
                    label="重命名蒙版 · "+next;
                }
                else if(operation=="remove")
                {
                    var keys=LocalEdits.ToneNames.Select(key=>LocalEdits.ToneKey(slot,key)).ToList();
                    keys.Add(LocalEdits.ToneKey(slot,"strength"));
                    if(slot>0)keys.Add(LocalEdits.ToneKey(slot,"backgroundOutput"));
                    if(slot==0)keys.Add("local_smooth");
                    else keys.AddRange(values.Keys.Where(k=>(StudioFinish.IsControl(k)||StudioFinish.DetailControl(k))&&LocalEdits.ToneSlot(k)==slot));
                    if(keys.Intersect(Strings(doc["locks"])).Any())throw new InvalidOperationException("此蒙版有已锁定的参数，请先解锁再移除。");
                    foreach(string key in keys)if(values[key]!=LocalEdits.LayerDefault(key))reset[key]=LocalEdits.LayerDefault(key);
                    if(slot==0)
                    {
                        set.primaryName=null;set.primaryId=null;
                        if(!set.primary.Any(s=>s.kind=="warp"||s.kind=="redEye"||LocalEdits.IsRepair(s.kind)))set.primary=set.primary.Where(s=>s.kind=="protect").ToArray();
                    }
                    else set.layers.Remove(layer);
                    label="移除蒙版 · "+name;
                    slot=set.primaryName!=null?0:set.layers.Select(l=>l.slot).DefaultIfEmpty(-1).First();
                }
                else throw new ArgumentException("不支持的蒙版操作。");
            }
            var patch=LocalEdits.SaveSet(set,hash);
            foreach(var pair in reset)patch.Add(pair.Key,pair.Value);
            var response=Map(CommitPatch(args,patch,label));
            response["maskSlot"]=slot;
            maskTicket=null;
            return response;
        }
    }
}
