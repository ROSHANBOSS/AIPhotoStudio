using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AIPhotoStudio
{
    internal sealed partial class StudioBridge
    {
        private string faceId;
        private long faceRevision;
        private List<Dictionary<string,double>> faceBoxes;
        private async Task<object> LocalPreview(Dictionary<string,object> args)
        {
            var values=LocalEdits.PreviewWithoutMask(ReadPreviewValues(args,true),BypassedMask(args));
            if(TextOf(args,"frameView")=="source")values=FrameGeometry.SourceValues(values);
            var set=LocalEdits.ReadSet(values);
            object raw;
            if(args.ContainsKey("areaTool"))UpdateAreas(set,args);
            else if(args.TryGetValue("gradient",out raw))
            {
                var draft=Map(raw);
                string kind=TextOf(draft,"kind");
                object creating,shape;
                if(!draft.TryGetValue("creating",out creating)||!(creating is bool)||!draft.TryGetValue("geometry",out shape))
                    throw new ArgumentException("渐变预览参数不完整。");
                bool isNew=(bool)creating;
                var layer=set.SetGradient(kind,LocalEdits.ReadGradient(kind,shape),isNew,isNew?0:MaskSlot(args));
                if(args.TryGetValue("strokes",out raw))set.SetStrokes(layer.slot,raw);
            }
            else if(args.TryGetValue("rangeMask",out raw))
            {
                var draft=Map(raw);string kind=TextOf(draft,"kind");
                if(!(draft["creating"] is bool))throw new ArgumentException("范围预览参数不完整。");
                bool creating=(bool)draft["creating"];
                var layer=set.SetRange(kind,LocalEdits.ReadRange(kind,draft["range"]),null,creating,creating?0:MaskSlot(args));
                if(args.TryGetValue("strokes",out raw))set.SetStrokes(layer.slot,raw);
            }
            else set.SetStrokes(MaskSlot(args),args["strokes"]);
            bool interactive=InteractivePreview(args);
            string id=TextOf(args,"id");
            var response=await RenderPreview(work.PathFor(id),values,PreviewEdge(args,interactive),interactive,set,id,false,clipping:ClippingDisplay.Flags(args)).ConfigureAwait(false);
            work.VerifyPreviewRevision(id,Convert.ToInt64(args["revision"]));
            return response;
        }
        private object SaveLocal(Dictionary<string,object> args)
        {
            Selected(args);
            object raw;
            string id=TextOf(args,"id");
            int slot=MaskSlot(args);
            var set=LocalEdits.ReadSet(work.ValuesFor(id));
            var previous=set.Strokes(slot);
            set.SetStrokes(slot,args["strokes"]);
            var patch=LocalEdits.SaveSet(set,work.OriginalHash(id));
            string label="更新手绘蒙版和网格";
            if(slot==0&&(set.primary.Skip(previous.Length).Any(s=>s.kind=="sampleRepair")||
                previous.Skip(set.primary.Length).Any(s=>s.kind=="sampleRepair")))label="调整取样修复污点";
            if(slot==0&&(set.primary.Skip(previous.Length).Any(s=>s.kind=="redEye")||
                previous.Skip(set.primary.Length).Any(s=>s.kind=="redEye")))label="调整单眼红眼修正";
            if(args.TryGetValue("values",out raw) && raw!=null)
            {
                foreach(var pair in Map(raw))
                {
                    bool tone=LocalEdits.ToneNames.Any(key=>pair.Key==LocalEdits.ToneKey(slot,key));
                    tone|=pair.Key==LocalEdits.ToneKey(slot,"strength")||slot>0&&set.Layer(slot).kind=="backdrop"&&pair.Key==LocalEdits.ToneKey(slot,"backgroundOutput");
                    bool detail=slot>0&&LocalEdits.ToneSlot(pair.Key)==slot&&
                        (StudioFinish.IsControl(pair.Key)||StudioFinish.DetailControl(pair.Key))&&StudioFinish.Allows(set.Layer(slot).kind,pair.Key);
                    if(!tone&&!detail)throw new ArgumentException("只能同时保存当前蒙版支持的调整。");
                    patch.Add(pair.Key,StudioWorkspace.Number(pair.Value));
                }
                label=patch.Keys.Any(k=>StudioFinish.IsControl(k)||StudioFinish.DetailControl(k))?"绘制蒙版并调整局部修整":"绘制蒙版并调整局部光影";
            }
            return CommitPatch(args,patch,label);
        }
        private async Task<object> MatchColor(Dictionary<string,object> args)
        {
            var doc=Selected(args);
            string id=TextOf(args,"id"),path=work.PathFor(id);
            using(var open=new OpenFileDialog{Title=L("选择用于仿色的参考照片","Choose a reference photo for color matching"),Filter=L("参考照片|*.jpg;*.jpeg;*.png;*.tif;*.tiff;*.heic;*.heif","Reference photos|*.jpg;*.jpeg;*.png;*.tif;*.tiff;*.heic;*.heif"),CheckFileExists=true})
            {
                if(open.ShowDialog(owner)!=DialogResult.OK)return null;
                string reference=open.FileName;
                var current=work.ValuesFor(id);
                var patch=await Task.Run(()=>ColorMatch.Fit(path,reference,current,Strings(doc["locks"]),closing.Token));
                Selected(args);
                if(patch.Count==0)throw new InvalidOperationException("现有可调参数未找到更接近参考图的组合。");
                return work.IssueProposal(args,patch,"参考图仿色","按参考图的亮度分布和色彩统计拟合，请预览后确认。");
            }
        }
        private object ImportLut(Dictionary<string,object> args)
        {
            Selected(args);
            using(var open=new OpenFileDialog{Title=L("导入 3D LUT","Import 3D LUT"),Filter=L("Cube 调色文件|*.cube","Cube LUT|*.cube"),CheckFileExists=true})
            {
                if(open.ShowDialog(owner)!=DialogResult.OK)return Snapshot();
                var patch=Imaging.ImportCube(open.FileName,Path.Combine(channelDirectory,"Presets","LUT"));
                return CommitPatch(args,patch,"导入 LUT · "+Path.GetFileNameWithoutExtension(open.FileName));
            }
        }
        private async Task<object> AnalyzeFaces(Dictionary<string,object> args)
        {
            Selected(args);
            string id=TextOf(args,"id");
            string path=work.PathFor(id);
            var image=await Render(path,work.NeutralFor(id,path,Convert.ToInt64(args["revision"])),800);
            var boxes=await Portrait.AnalyzeAsync(image);
            Selected(args);
            faceId=id;faceRevision=Convert.ToInt64(args["revision"]);faceBoxes=boxes;
            return new {id=id,revision=faceRevision,faces=boxes};
        }
        private object SelectFace(Dictionary<string,object> args)
        {
            Selected(args);
            int index=Convert.ToInt32(args["index"]);
            if(faceId!=TextOf(args,"id")||faceRevision!=Convert.ToInt64(args["revision"])||faceBoxes==null||index<0||index>=faceBoxes.Count)
                throw new InvalidOperationException("请重新检测并选择实际人脸。");
            return Snapshot(work.SetDetectedFace(faceId,faceRevision,Portrait.FacePatch(faceBoxes[index])));
        }

        // Package immutable masks and LUTs referenced by any history frame, including redo entries.
        internal object PackageRecipe(string id)
        {
            var recipe=Map(work.Dispatch("exportRecipe",new Dictionary<string,object>{{"id",id}}));
            var resources=new Dictionary<string,object>();
            foreach(var slice in ((IEnumerable)recipe["history"]).Cast<object>().Select(Map))
            {
                var values=Json().ConvertToType<Dictionary<string,double>>(slice["parameters"]);
                string mask=LocalEdits.Hash(values),cube=Imaging.CubeHash(values);
                string segment=SegmentAssets.Hash(values);
                string body=BodyAssets.ResourceHash(values);
                string face=PortraitAssets.Hash(values);
                if(face!=null && !resources.ContainsKey("face:"+face))
                    resources["face:"+face]=new {type="face",content=Convert.ToBase64String(PortraitAssets.Read(values))};
                if(body!=null && !resources.ContainsKey("body:"+body))
                    resources["body:"+body]=new {type="body",content=Convert.ToBase64String(BodyAssets.Read(values))};
                if(segment!=null&&!resources.ContainsKey("segment:"+segment))
                    resources["segment:"+segment]=new {type="segment",content=Convert.ToBase64String(SegmentAssets.Read(values))};
                if(mask!=null&&!resources.ContainsKey(mask))
                {
                    var set=LocalEdits.ReadSet(values);
                    if(set.sourceHash==null)resources[mask]=new {type="mask",strokes=set.primary};
                    else resources[mask]=new {type="namedMasks",encoding="gzip",content=LocalEdits.Pack(values)};
                }
                if(cube!=null&&!resources.ContainsKey(cube))
                {
                    string file=Path.Combine(channelDirectory,"Presets","LUT",cube+".cube");
                    if(!File.Exists(file))throw new FileNotFoundException("缺少此配方使用的 LUT。");
                    resources[cube]=new {type="cube",content=Convert.ToBase64String(File.ReadAllBytes(file))};
                }
            }
            if(resources.Count>256)throw new InvalidDataException("完整历史引用了超过 256 份资源，无法写出可再次导入的配方文件。");
            return new {format="AIPhotoStudio portable",version=1,recipe=recipe,resources=resources};
        }

        internal object UnpackRecipe(object raw)
        {
            var packet=Map(raw);
            if(TextOf(packet,"format")!="AIPhotoStudio portable")return raw;
            if(!Object.Equals(packet["version"],1))throw new InvalidDataException("不支持的配方文件版本。");
            var resources=Map(packet["resources"]);
            var schema=Convert.ToInt32(Map(packet["recipe"])["schemaVersion"]);
            if(resources.Count>256)throw new InvalidDataException("随附的资源过多。");
            foreach(var pair in resources)
            {
                var item=Map(pair.Value);
                string hash;
                if(TextOf(item,"type")=="mask")
                {
                    if(schema<5 && ((IEnumerable)item["strokes"]).Cast<object>().Select(s=>TextOf(Map(s),"kind"))
                        .Any(kind=>kind=="maskPaint" || kind=="maskErase"))
                        throw new InvalidDataException("光影蒙版的独立画笔和擦除需要第 5 版交换配方。");
                    hash=LocalEdits.Hash(LocalEdits.Save(item["strokes"]));
                }
                else if(TextOf(item,"type")=="namedMasks")
                {
                    string content=TextOf(item,"content");
                    if(schema<6||pair.Key.Length!=64||content.Length>4*((LocalEdits.MaxMaskBytes+2)/3))
                        throw new InvalidDataException("独立蒙版需要第 6 版交换配方与有效资源。");
                    if(TextOf(item,"encoding")!="gzip")throw new InvalidDataException("独立蒙版压缩格式无效。");
                    LocalEdits.Unpack(content,pair.Key,schema);hash=pair.Key;
                }
                else if(TextOf(item,"type")=="segment")
                {
                    string content=TextOf(item,"content");
                    if(content.Length!=21848)throw new InvalidDataException("前背景分区编码长度无效。");
                    hash="segment:"+SegmentAssets.Hash(SegmentAssets.Save(Convert.FromBase64String(content)));
                }
                else if(TextOf(item,"type")=="body")
                {
                    string content=TextOf(item,"content");
                    if(!pair.Key.StartsWith("body:",StringComparison.Ordinal) || pair.Key.Length!=69 || content.Length<22424 ||
                        content.Length>4*((BodyAssets.MaxBytes+2)/3) || content.Length%4!=0)
                        throw new InvalidDataException("人体资源指纹或编码长度无效。");
                    hash="body:"+BodyAssets.ResourceHash(BodyAssets.Import(Convert.FromBase64String(content),pair.Key.Substring(5)));
                }
                else if(TextOf(item,"type")=="face")
                {
                    string content=TextOf(item,"content");
                    if(schema<6 || !pair.Key.StartsWith("face:",StringComparison.Ordinal) || pair.Key.Length!=69 ||
                        content.Length>4*((PortraitAssets.MaxBytes+2)/3))
                        throw new InvalidDataException("人脸资源指纹、版本或长度无效。");
                    byte[] bytes=Convert.FromBase64String(content);
                    if(schema<12&&bytes.Length>=4&&bytes[0]=='P'&&bytes[1]=='S'&&bytes[2]=='L'&&bytes[3]=='3')
                        throw new InvalidDataException("人脸保留标记需要第 12 版交换配方。");
                    hash="face:"+PortraitAssets.Hash(PortraitAssets.Store(bytes,pair.Key.Substring(5)));
                }
                else if(TextOf(item,"type")=="cube")
                {
                    string lib=Path.Combine(channelDirectory,"Presets","LUT");
                    Directory.CreateDirectory(lib);
                    string temp=Path.Combine(lib,Guid.NewGuid().ToString("N")+".import.cube");
                    try
                    {
                        byte[] bytes=Convert.FromBase64String(TextOf(item,"content"));
                        using(var output=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None))output.Write(bytes,0,bytes.Length);
                        hash=Imaging.CubeHash(Imaging.ImportCube(temp,lib));
                    }
                    finally {if(File.Exists(temp))File.Delete(temp);}
                }
                else throw new InvalidDataException("未知的配方资源。");
                if(!String.Equals(hash,pair.Key,StringComparison.Ordinal))throw new InvalidDataException("随附资源摘要不一致。");
            }
            foreach(var slice in ((IEnumerable)Map(packet["recipe"])["history"]).Cast<object>().Select(Map))
            {
                var values=Json().ConvertToType<Dictionary<string,double>>(slice["parameters"]);
                foreach(var hash in new[]{LocalEdits.Hash(values),Imaging.CubeHash(values)})
                    if(hash!=null&&!resources.ContainsKey(hash))throw new InvalidDataException("配方文件缺少引用的资源。");
                string segment=SegmentAssets.Hash(values);
                if(segment!=null&&!resources.ContainsKey("segment:"+segment))throw new InvalidDataException("配方文件缺少前背景分区。");
                string body=BodyAssets.ResourceHash(values);
                if(body!=null&&!resources.ContainsKey("body:"+body))throw new InvalidDataException("配方文件缺少人体资源。");
                string face=PortraitAssets.Hash(values);
                if(face!=null&&!resources.ContainsKey("face:"+face))throw new InvalidDataException("配方文件缺少人脸关键点。");
            }
            return packet["recipe"];
        }
    }
}
