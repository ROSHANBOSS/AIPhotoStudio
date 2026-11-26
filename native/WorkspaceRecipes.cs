using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace AIPhotoStudio
{
    public sealed partial class StudioWorkspace
    {
        private const string RecipeFormat = "AIPhotoStudio.recipe";
        private const string PresetFormat = "AIPhotoStudio.presets";
        private const int PortableVersion = 14;

        private static bool V14Control(string key){return key=="frame_mirror"||LocalEdits.LayerOutputKey(key);}

        private static bool V13Control(string key){return FrameGeometry.IsPerspectiveKey(key)||PersonalRetouch.IsNewControl(key);}

        private static bool V8Control(string key) { return LocalEdits.NewColorKey(key)||StudioFinish.IsControl(key); }

        private static Dictionary<string,double> PortableParameters(IDictionary<string,double> values,int schema)
        {
            var compatible=schema<7?FrameGeometry.SourceValues(values):new Dictionary<string,double>(values);
            if(schema<8)foreach(string key in compatible.Keys.Where(V8Control).ToArray())compatible.Remove(key);
            if(schema<9)foreach(string key in compatible.Keys.Where(StudioFinish.DetailControl).ToArray())compatible.Remove(key);
            if(schema==9)foreach(string key in compatible.Keys.Where(StudioFinish.RequiresV10).ToArray())compatible.Remove(key);
            if(schema<13)foreach(string key in compatible.Keys.Where(V13Control).ToArray())compatible.Remove(key);
            if(schema<14)foreach(string key in compatible.Keys.Where(V14Control).ToArray())compatible.Remove(key);
            return compatible;
        }

        private object ExportRecipe(string id)
        {
            State s = Current(id);
            List<object> history = new List<object>();
            int schema=6;
            var checkedMasks=new HashSet<string>();
            var checkedFaces=new HashSet<string>();
            bool hasFace=false,hasMask=false,hasMeshes=false,hasFraming=false;
            foreach (object[] h in db.Rows("SELECT label,origin,stamp,operation,parameters,locks FROM history WHERE photo_id=? ORDER BY seq", id))
            {
                var values=StoredValues((string)h[4]);
                var locks=json.Deserialize<string[]>((string)h[5]);
                string mask=LocalEdits.Hash(values);
                if(FrameGeometry.Active(values)||locks.Any(FrameGeometry.IsKey))schema=Math.Max(schema,7);
                if(locks.Any(V13Control)||values.Any(p=>p.Value!=0&&V13Control(p.Key)))schema=Math.Max(schema,13);
                if(locks.Any(V14Control)||values.Any(p=>V14Control(p.Key)&&p.Value!=LocalEdits.LayerDefault(p.Key)))schema=14;
                if(mask!=null&&checkedMasks.Add(mask))
                {
                    var set=LocalEdits.ReadSet(values);
                    if(set.HasV7Edits)schema=Math.Max(schema,13);
                    else if(set.HasV6Edits)schema=Math.Max(schema,12);
                    else if(set.HasSampleRepair)schema=Math.Max(schema,11);
                    else if(set.HasHandDetail)schema=Math.Max(schema,10);
                    else if(set.HasV3Layers)schema=Math.Max(schema,8);
                    else if(set.HasGradients)schema=Math.Max(schema,7);
                }
                if(locks.Any(V8Control)||values.Any(p=>p.Value!=0&&LocalEdits.NewColorKey(p.Key))||StudioFinish.RequiresV8(values))schema=Math.Max(schema,8);
                if(locks.Any(StudioFinish.RequiresV10)||values.Any(p=>p.Value!=0&&StudioFinish.RequiresV10(p.Key)))schema=Math.Max(schema,10);
                else if(locks.Any(StudioFinish.DetailControl)||values.Any(p=>p.Value!=0&&StudioFinish.DetailControl(p.Key)))schema=Math.Max(schema,9);
                hasFace|=PortraitEdits.HasFace(values);
                string mesh=PortraitAssets.Hash(values);
                hasMeshes|=mesh!=null;
                if(mesh!=null&&checkedFaces.Add(mesh)&&PortraitAssets.HasKeepMarks(values))schema=Math.Max(schema,12);
                hasMask|=HasMaskReference(values) || HasSegmentReference(values) || BodyAssets.ResourceHash(values)!=null;
                hasMask|=CameraRawModes.Selected(values)!=0;
                hasFraming|=FrameGeometry.Active(values);
                history.Add(new Dictionary<string, object>
                {
                    { "label", h[0] }, { "source", h[1] }, { "when", h[2] }, { "operation", h[3] },
                    { "parameters", values }, { "locks", locks }
                });
            }
            if(schema<PortableVersion)foreach(Dictionary<string,object> frame in history)
                frame["parameters"]=PortableParameters((Dictionary<string,double>)frame["parameters"],schema);
            var recipe=new Dictionary<string, object>
            {
                { "format", RecipeFormat }, { "schemaVersion", schema }, { "engineVersion", s.Engine },
                { "cursor", s.Cursor }, { "parameters", PortableParameters(s.Values,schema) }, { "locks", OrderedLocks(s.Locks) }, { "history", history }
            };
            if(hasFace || hasMask || hasMeshes || hasFraming)
            {
                recipe["sourceHash"]=(string)db.Scalar("SELECT value FROM settings WHERE name=?","face-source/"+id) ?? OriginalHash(id);
                if(hasFace) recipe["detectorVersion"]=Portrait.DetectorVersion;
                if(hasMeshes)recipe["detectorVersion"]=hasFace?Portrait.DetectorVersion+"+"+PortraitAssets.Model:PortraitAssets.Model;
            }
            return recipe;
        }

        internal string OriginalHash(string id)
        {
            using(var file=WorkspacePaths.OpenRead(PathFor(id),FileShare.Read))
            using(var hash=SHA256.Create()) return Convert.ToBase64String(hash.ComputeHash(file));
        }

        private static bool HasMaskReference(IDictionary<string,double> values)
        {
            return values.Any(p=>p.Key.StartsWith("mask_ref",StringComparison.Ordinal) && p.Value!=0);
        }

        private static bool HasSegmentReference(IDictionary<string,double> values)
        {
            return values.Any(p=>p.Key.StartsWith("segment_ref",StringComparison.Ordinal) && p.Value!=0);
        }

        private static bool HasFrameRegions(IDictionary<string,double> values)
        {
            return PortraitEdits.HasFace(values)||PortraitAssets.Hash(values)!=null||BodyAssets.ResourceHash(values)!=null||
                HasMaskReference(values)||HasSegmentReference(values);
        }
        private static void CheckRawTrackChange(IDictionary<string,double> previous,IDictionary<string,double> next)
        {
            if(CameraRawModes.Selected(previous)!=CameraRawModes.Selected(next)&&(HasFrameRegions(previous)||HasFrameRegions(next)))
                throw new InvalidOperationException("当前人脸、人体、蒙版或前背景选区绑定了感光轨。请先恢复到建立选区之前的历史版本，再切换感光轨。");
        }

        private static bool HasSegmentAdjustment(IDictionary<string,double> values)
        {
            double f,b;
            return (values.TryGetValue("segment_foreground",out f) && f!=0) || (values.TryGetValue("segment_background",out b) && b!=0);
        }

        private void RecordBoundOriginal(string id)
        {
            string record="face-source/"+id;
            if(db.Scalar("SELECT value FROM settings WHERE name=?",record)==null)
                db.Run("INSERT INTO settings(name,value) VALUES(?,?)",record,OriginalHash(id));
        }

        private void RequireSecondRawTrack(string id)
        {
            var photo=Imaging.Inspect(PathFor(id));
            object reference;
            if(!Object.Equals(photo["format"],"CR3")||!photo.TryGetValue("referenceSampleId",out reference)||!Object.Equals(reference,4611)||Convert.ToInt32(photo["frameCount"])!=2)
                throw new InvalidOperationException("所选照片没有经验证的第二条 RAW 感光轨。");
        }

        private static void CheckPacket(Dictionary<string, object> packet, string format)
        {
            long version=IntegerArg(packet,"schemaVersion");
            if (TextArg(packet, "format", 100) != format || version<1 || version>PortableVersion)
                throw new ArgumentException("交换文件的格式或版本不受支持。");
        }

        private sealed class Frame
        {
            internal string Label, Source, When, Operation;
            internal Dictionary<string, double> Values, Changes;
            internal HashSet<string> Locks;
        }

        private sealed class ParamStack
        {
            internal readonly Stack<double> Undo = new Stack<double>();
            internal readonly Stack<double> Redo = new Stack<double>();
        }

        private List<Frame> ReadPortableHistory(Dictionary<string, object> recipe)
        {
            List<Frame> result = new List<Frame>();
            Dictionary<string, double> previous = Defaults();
            HashSet<string> locks = new HashSet<string>(StringComparer.Ordinal);
            Dictionary<string, ParamStack> stacks = controls.Keys.ToDictionary(key => key, key => new ParamStack(), StringComparer.Ordinal);
            foreach (object item in Sequence(Arg(recipe, "history"), 10000))
            {
                Dictionary<string, object> row = Map(item);
                Fields(row, "label", "source", "when", "operation", "parameters", "locks");
                Frame frame = new Frame {
                    Label = TextArg(row, "label", 240), Source = TextArg(row, "source", 80), When = TextArg(row, "when", 80),
                    Operation = TextArg(row, "operation", 40), Values = ParseValues(Arg(row, "parameters"), true), Locks = Keys(Arg(row, "locks"))
                };
                DateTime stamp;
                if (!DateTime.TryParseExact(frame.When, "o", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out stamp) || stamp.Kind != DateTimeKind.Utc)
                    throw new ArgumentException("历史时间需要使用带 UTC 时区的 ISO 格式。");
                frame.Changes = Difference(previous, frame.Values);
                if(frame.Changes.ContainsKey(CameraRawModes.FrameKey))
                {
                    bool restoresRecorded=frame.Operation=="restore"&&result.Any(prior=>Difference(prior.Values,frame.Values).Count==0&&prior.Locks.SetEquals(frame.Locks));
                    if(!restoresRecorded)CheckRawTrackChange(previous,frame.Values);
                }
                CheckMaskSlots(frame.Values);
                CheckDetailLocks(frame.Values,frame.Locks);
                if(frame.Operation=="edit"&&frame.Changes.Keys.Any(k=>k.StartsWith("mask_ref",StringComparison.Ordinal)))
                    LocalEdits.CheckLockedTargets(previous,frame.Values,locks);
                bool locksEqual = locks.SetEquals(frame.Locks);
                if (result.Count == 0)
                {
                    if (frame.Operation != "initial" || !locksEqual || frame.Changes.Count > 0)
                        throw new ArgumentException("配方的初始版本与当前引擎不匹配。");
                }
                else
                {
                    bool valid;
                    switch (frame.Operation)
                    {
                        case "lock":
                            int lockChanges = locks.Except(frame.Locks).Count() + frame.Locks.Except(locks).Count();
                            valid = frame.Changes.Count == 0 && lockChanges == 1; break;
                        case "restore": valid = frame.Changes.Count > 0 || !locksEqual; break;
                        case "face": valid=locksEqual && frame.Changes.Count>0 && PortraitEdits.HasFace(frame.Values) && frame.Changes.Keys.All(k=>controls[k].group=="人脸区域"); break;
                        case "undoParam": case "redoParam": case "resetParam":
                            valid = locksEqual && frame.Changes.Count == 1 && !frame.Changes.Keys.Any(locks.Contains); break;
                        case "edit": case "recipe":
                            valid = locksEqual && frame.Changes.Count > 0 && !frame.Changes.Keys.Any(locks.Contains); break;
                        default: valid = false; break;
                    }
                    if (!valid) throw new ArgumentException("配方中的历史操作与参数或锁定状态不一致。");
                }
                bool importedFace=frame.Operation=="recipe" && PortraitAssets.Hash(frame.Values)!=null && PortraitEdits.HasFace(frame.Values);
                if(frame.Operation!="face" && frame.Operation!="restore" && !importedFace && frame.Changes.Keys.Any(k=>controls[k].group=="人脸区域"))
                    throw new ArgumentException("配方的人脸区域缺少独立的检测选择步骤。");
                if((frame.Operation=="edit" || frame.Operation=="recipe") && frame.Changes.Keys.Any(k=>controls[k].group==PortraitControls.ResourceGroup) &&
                    !PortraitAssets.CanReplace(previous,frame.Values,locks))throw new ArgumentException("重新识别会改变已有调整所对应的人脸。");
                if(frame.Locks.Any(k=>PortraitControls.Index(k)>=0&&!PortraitAssets.CanAdjust(frame.Values,k)))
                    throw new ArgumentException("配方包含未识别人脸的锁定参数。");
                if(frame.Operation=="edit" && frame.Changes.Keys.Any(k=>k.StartsWith("body_ref",StringComparison.Ordinal)) &&
                    !BodyAssets.CanReplace(previous,frame.Values,locks))
                    throw new ArgumentException("配方中的重新识别会把已有美型或锁定设置转移到其他人物。");
                EnsureFaceForEdits(frame.Values);
                foreach (KeyValuePair<string, double> delta in frame.Changes)
                {
                    ParamStack stack = stacks[delta.Key];
                    if (frame.Operation == "undoParam" || frame.Operation == "redoParam")
                    {
                        bool backward = frame.Operation == "undoParam";
                        Stack<double> from = backward ? stack.Undo : stack.Redo;
                        if (from.Count == 0 || from.Pop() != delta.Value) throw new ArgumentException("配方的单项撤销或重做没有对应历史。");
                        (backward ? stack.Redo : stack.Undo).Push(previous[delta.Key]);
                    }
                    else { stack.Undo.Push(previous[delta.Key]); stack.Redo.Clear(); }
                }
                result.Add(frame);
                previous = frame.Values;
                locks = frame.Locks;
            }
            if (result.Count == 0) throw new ArgumentException("配方缺少初始历史。");
            return result;
        }

        private void ImportRecipe(State state, Dictionary<string, object> a)
        {
            string mode = a.ContainsKey("mode") ? TextArg(a, "mode", 20) : "apply";
            if (mode != "apply" && mode != "history") throw new ArgumentException("未知的配方导入方式。");
            Dictionary<string, object> packet = Map(Arg(a, "recipe"));
            Fields(packet, "format", "schemaVersion", "engineVersion", "cursor", "parameters", "locks", "history", "sourceHash", "detectorVersion");
            CheckPacket(packet, RecipeFormat);
            CheckEngine(TextArg(packet, "engineVersion", 100));
            List<Frame> frames = ReadPortableHistory(packet);
            if(IntegerArg(packet,"schemaVersion")<14&&frames.Any(f=>f.Changes.Keys.Concat(f.Locks).Any(V14Control)))
                throw new ArgumentException("水平镜像、整层调整强度与背景替换需要第 14 版交换配方。");
            if(IntegerArg(packet,"schemaVersion")<13&&frames.Any(f=>f.Changes.Keys.Concat(f.Locks).Any(V13Control)))
                throw new ArgumentException("四角拉正、分项降噪和背景虚化需要第 13 版交换配方。");
            if(IntegerArg(packet,"schemaVersion")<10&&frames.Any(f=>f.Changes.Keys.Concat(f.Locks).Any(StudioFinish.RequiresV10)))
                throw new ArgumentException("镜片反光和衣料细节保护需要第 10 版交换配方。");
            if(IntegerArg(packet,"schemaVersion")<9&&frames.Any(f=>f.Changes.Keys.Concat(f.Locks).Any(StudioFinish.DetailControl)))
                throw new ArgumentException("衣物分频修整和肤色过渡需要第 9 版交换配方。");
            if(IntegerArg(packet,"schemaVersion")<7 && frames.Any(f=>FrameGeometry.Active(f.Values)||f.Locks.Any(FrameGeometry.IsKey)))
                throw new ArgumentException("构图参数需要第 7 版交换配方。");
            var meshes=frames.Select(f=>f.Values).Where(v=>PortraitAssets.Hash(v)!=null).GroupBy(v=>PortraitAssets.Hash(v)).Select(g=>g.First()).ToList();
            if(IntegerArg(packet,"schemaVersion")<12&&meshes.Any(PortraitAssets.HasKeepMarks))
                throw new ArgumentException("人脸保留标记需要第 12 版交换配方。");
            if(IntegerArg(packet,"schemaVersion")<6 && (meshes.Count>0 || frames.Any(f=>f.Changes.Keys.Concat(f.Locks).Any(PortraitControls.Bound))))
                throw new ArgumentException("分人脸精修需要第 6 版交换配方。");
            if(meshes.Any(v=>!packet.ContainsKey("sourceHash") || PortraitAssets.Resolve(v).SourceHash!=TextArg(packet,"sourceHash",120)))
                throw new ArgumentException("配方中的人脸关键点与原片摘要不符。");
            var bodies=frames.Where(f=>BodyAssets.ResourceHash(f.Values)!=null).GroupBy(f=>BodyAssets.ResourceHash(f.Values)).Select(g=>g.First()).ToList();
            var namedMasks=frames.Where(f=>HasMaskReference(f.Values)).GroupBy(f=>LocalEdits.Hash(f.Values))
                .Select(g=>LocalEdits.ReadSet(g.First().Values)).Where(set=>set.sourceHash!=null).ToList();
            if(IntegerArg(packet,"schemaVersion")<13&&namedMasks.Any(set=>set.HasV7Edits))
                throw new ArgumentException("红眼标记需要第 13 版交换配方。");
            if(IntegerArg(packet,"schemaVersion")<12&&namedMasks.Any(set=>set.HasV6Edits))
                throw new ArgumentException("此选区需要第 12 版交换配方。");
            if(IntegerArg(packet,"schemaVersion")<11&&namedMasks.Any(set=>set.HasSampleRepair))
                throw new ArgumentException("取样修复需要第 11 版交换配方。");
            if(IntegerArg(packet,"schemaVersion")<10&&namedMasks.Any(set=>set.HasHandDetail))
                throw new ArgumentException("镜片与精细衣料蒙版需要第 10 版交换配方。");
            if(IntegerArg(packet,"schemaVersion")<8&&(namedMasks.Any(set=>set.HasV3Layers)||
                frames.Any(f=>f.Changes.Keys.Concat(f.Locks).Any(V8Control))))
                throw new ArgumentException("此局部调色、范围蒙版或人像修整需要第 8 版交换配方。");
            if(IntegerArg(packet,"schemaVersion")<7 && namedMasks.Any(set=>set.layers.Any(layer=>layer.kind=="linear"||layer.kind=="radial")))
                throw new ArgumentException("渐变蒙版需要第 7 版交换配方。");
            if(IntegerArg(packet,"schemaVersion")<6 && (namedMasks.Count>0||frames.Any(f=>f.Changes.Keys.Concat(f.Locks).Any(LocalEdits.ExtraKey))))
                throw new ArgumentException("独立蒙版需要第 6 版交换配方。");
            if(bodies.Count>0 && IntegerArg(packet,"schemaVersion")<4) throw new ArgumentException("人体资源需要第 4 版交换配方。");
            if(IntegerArg(packet,"schemaVersion")<5 && (bodies.Any(f=>!BodyAssets.IsLegacy(f.Values)) ||
                frames.Any(f=>f.Changes.Keys.Concat(f.Locks).Any(k=>k=="local_shadows" || k=="local_highlights" || BodyEdits.PersonIndex(k)>0))))
                throw new ArgumentException("分人物美型与局部高光、阴影需要第 5 版交换配方。");
            if(frames.Any(f=>f.Values.Any(p=>p.Value!=0 && BodyEdits.BaseKey(p.Key)!=null && !BodyAssets.CanAdjust(f.Values,p.Key))))
                throw new ArgumentException("配方中的人体调整与所选人物的可见部位不匹配。");
            if(IntegerArg(packet,"schemaVersion")==1 && frames.Any(f=>HasSegmentReference(f.Values) || HasSegmentAdjustment(f.Values)))
                throw new ArgumentException("前景与背景分区需要第 2 版交换配方。");
            long cursor = IntegerArg(packet, "cursor");
            if (cursor >= frames.Count) throw new ArgumentException("配方的历史位置无效。");
            Frame active = frames[(int)cursor];
            if(IntegerArg(packet,"schemaVersion")<6&&frames.Any(f=>CameraRawModes.Selected(f.Values)!=0))
                throw new ArgumentException("含感光轨选择的历史需要第 6 版交换配方。");
            if (Difference(active.Values, ParseValues(Arg(packet, "parameters"), true)).Count != 0 ||
                !active.Locks.SetEquals(Keys(Arg(packet, "locks")))) throw new ArgumentException("配方当前状态与历史不一致。");
            if(namedMasks.Any(set=>!packet.ContainsKey("sourceHash")||set.sourceHash!=TextArg(packet,"sourceHash",120)))
                throw new InvalidOperationException("蒙版资源与配方记录的原片指纹不同。");
            if (mode == "apply")
            {
                bool sameFraming=frames.Any(f=>FrameGeometry.Active(f.Values)) && packet.ContainsKey("sourceHash") && TextArg(packet,"sourceHash",120)==OriginalHash(state.Id);
                bool samePortrait=PortraitAssets.Hash(active.Values)!=null && TextArg(packet,"sourceHash",120)==OriginalHash(state.Id);
                bool namedActive=HasMaskReference(active.Values)&&(LocalEdits.ReadSet(active.Values).sourceHash!=null||
                    active.Values.Any(p=>LocalEdits.LayerOutputKey(p.Key)&&p.Value!=LocalEdits.LayerDefault(p.Key)));
                if((samePortrait||namedActive)&&CameraRawModes.Selected(active.Values)!=CameraRawModes.Selected(state.Values))
                    throw new InvalidOperationException("这份配方的人脸或蒙版绑定了另一条感光轨。请先选择对应感光轨。");
                if(namedActive&&TextArg(packet,"sourceHash",120)!=OriginalHash(state.Id))
                    throw new InvalidOperationException("配方中的蒙版绑定了另一份原片，请在同一原片上导入。");
                ApplyRecipe(state,active.Values,"导入编辑配方",samePortrait,namedActive,sameFraming);
                if(samePortrait||namedActive||sameFraming)RecordBoundOriginal(state.Id);
                return;
            }
            if(frames.Any(f=>CameraRawModes.Selected(f.Values)!=0))
            {
                RequireSecondRawTrack(state.Id);
                if(!packet.ContainsKey("sourceHash")||TextArg(packet,"sourceHash",120)!=OriginalHash(state.Id))
                    throw new InvalidOperationException("含感光轨选择的历史需要匹配同一份原片。");
            }
            if(meshes.Count>0 || bodies.Count>0 || frames.Any(f=>FrameGeometry.Active(f.Values) || PortraitEdits.HasFace(f.Values) || HasMaskReference(f.Values) || HasSegmentReference(f.Values)))
            {
                if(!packet.ContainsKey("sourceHash") || !String.Equals(TextArg(packet,"sourceHash",120),OriginalHash(state.Id),StringComparison.Ordinal))
                    throw new InvalidOperationException("此历史包含绑定原片的空间区域，请匹配相同原片后迁入历史。");
                if(bodies.Any(f=>!BodyAssets.MatchesSource(f.Values,TextArg(packet,"sourceHash",120))))
                    throw new InvalidOperationException("人体检测资源与原片不匹配。");
                db.Run("INSERT OR REPLACE INTO settings(name,value) VALUES(?,?)","face-source/"+state.Id,TextArg(packet,"sourceHash",120));
            }
            if (state.Revision != 0 || state.Cursor != 0 ||
                (long)db.Scalar("SELECT count(*) FROM history WHERE photo_id=?", state.Id) != 1)
                throw new InvalidOperationException("仅可在从未编辑的照片上迁入完整历史；可选择追加配方以保留已有编辑。");
            db.Run("DELETE FROM history WHERE photo_id=?", state.Id);
            for (int i = 0; i < frames.Count; i++)
            {
                Frame f = frames[i];
                WriteFrame(state.Id, i, f.Label, f.Source, f.When, f.Operation, f.Values, f.Locks, f.Changes);
            }
            db.Run("UPDATE photos SET revision=revision+1,cursor=? WHERE id=?", cursor, state.Id);
        }

        private void ApplyRecipe(State target, Dictionary<string, double> recipe, string label, bool samePortrait=false,bool sameMasks=false,bool sameFraming=false)
        {
            var merged=new Dictionary<string,double>(target.Values,StringComparer.Ordinal);
            if(samePortrait && !PortraitAssets.CanReplace(target.Values,recipe,target.Locks))
                throw new InvalidOperationException("这份配方的人脸编号与目标已有调整不一致。");
            bool oldFaceOnSameImage=samePortrait&&PortraitEdits.HasFace(recipe);
            if(oldFaceOnSameImage&&PortraitEdits.HasFace(target.Values)&&target.Locks.Any(k=>k.StartsWith("portrait_skin_",StringComparison.Ordinal))&&
                recipe.Any(p=>p.Key.StartsWith("portrait_face_",StringComparison.Ordinal)&&p.Value!=target.Values[p.Key]))
                throw new InvalidOperationException("这份配方会移动目标已经锁定的旧版肤质选区。");
            bool skinRecipe=recipe.Any(p=>p.Key.StartsWith("portrait_skin_",StringComparison.Ordinal) && p.Value!=0);
            bool paintRecipe=recipe.Any(p=>p.Key.StartsWith("local_",StringComparison.Ordinal) && !LocalEdits.ExtraKey(p.Key) && p.Key!="local_warp" && p.Key!="local_strength" && p.Value!=0);
            if(sameMasks&&LocalEdits.Hash(target.Values)!=LocalEdits.Hash(recipe)&&target.Locks.Any(k=>k.StartsWith("local_",StringComparison.Ordinal)))
                throw new InvalidOperationException("照片的局部参数已锁定，无法替换其蒙版。");
            if(paintRecipe && !sameMasks && !HasEffectivePaint(target)) throw new InvalidOperationException("此配方包含手绘区域调色，请先在目标照片上画出并保存蒙版。");
            foreach(var entry in recipe)
            {
                string key=entry.Key,group=controls[key].group;
                if(sameMasks&&(key.StartsWith("mask_ref",StringComparison.Ordinal)||key.StartsWith("local_",StringComparison.Ordinal)))
                {
                    merged[key]=entry.Value;continue;
                }
                if(key==CameraRawModes.FrameKey)continue;
                if(FrameGeometry.IsKey(key) && !sameFraming)continue;
                if(PortraitControls.Bound(key)){if(samePortrait)merged[key]=entry.Value;continue;}
                if(oldFaceOnSameImage&&group=="人脸区域"){merged[key]=entry.Value;continue;}
                if(LocalEdits.ExtraKey(key)||key=="local_strength"||group=="人脸区域" || group=="局部资源" || key=="local_warp" || key.StartsWith("segment_",StringComparison.Ordinal) || key.StartsWith("body_",StringComparison.Ordinal)) continue;
                if(!skinRecipe && key.StartsWith("portrait_skin_",StringComparison.Ordinal)) continue;
                if(!paintRecipe && key.StartsWith("local_",StringComparison.Ordinal)) continue;
                merged[key]=entry.Value;
            }
            EnsureFaceForEdits(merged);
            foreach (string changed in Difference(target.Values, merged).Keys) Unlocked(target, changed);
            Append(target, merged, target.Locks, label, "recipe", "recipe");
        }

        public object CopySettingsToMany(string sourceId,long expectedRevision,IEnumerable<string> targetIds)
        {
            if(targetIds==null) throw new ArgumentNullException("targetIds");
            var ids=targetIds.Distinct(StringComparer.Ordinal).Where(id=>id!=sourceId).ToArray();
            lock(gate) return db.Transaction(true,delegate
            {
                State source=Current(sourceId);
                CheckEngine(source.Engine);
                CheckRevision(source,expectedRevision);
                bool sourceFace=PortraitEdits.HasFace(source.Values);
                bool sourcePaint=HasEffectivePaint(source);
                foreach(string id in ids)
                {
                    State target=Current(id);
                    CheckEngine(target.Engine);
                    bool copyFace=sourceFace && PortraitEdits.HasFace(target.Values);
                    bool copyPaint=sourcePaint && HasEffectivePaint(target);
                    bool protectLut=target.Locks.Any(k=>k=="lut_strength" || controls[k].group=="LUT 资源");
                    var values=new Dictionary<string,double>(target.Values,StringComparer.Ordinal);
                    foreach(var entry in source.Values)
                    {
                        string key=entry.Key;
                        if(key==CameraRawModes.FrameKey || FrameGeometry.IsKey(key))continue;
                        if(PortraitControls.Bound(key))continue;
                        string group=controls[key].group;
                        if(LocalEdits.ExtraKey(key)||key=="local_strength"||target.Locks.Contains(key) || group=="人脸区域" || group=="局部资源" || key=="local_warp" || key.StartsWith("segment_",StringComparison.Ordinal) || key.StartsWith("body_",StringComparison.Ordinal)) continue;
                        if(protectLut && (key=="lut_strength" || group=="LUT 资源")) continue;
                        if((key.StartsWith("portrait_skin_",StringComparison.Ordinal) && !copyFace) ||
                            (key.StartsWith("local_",StringComparison.Ordinal) && !copyPaint)) continue;
                        values[key]=entry.Value;
                    }
                    EnsureFaceForEdits(values);
                    Append(target,values,target.Locks,"批量复制调整","batch","recipe");
                }
                return ReadSnapshot();
            });
        }

        private bool HasEffectivePaint(State state)
        {
            return HasEffectivePaint(state.Id,state.Values);
        }
        private bool HasEffectivePaint(string id,IDictionary<string,double> values,int slot=0)
        {
            if(!HasMaskReference(values)) return false;
            string serialized=(string)db.Scalar("SELECT metadata FROM photos WHERE id=?",id);
            var meta=json.Deserialize<Dictionary<string,object>>(serialized);
            object w,h;
            if(!meta.TryGetValue("width",out w) || !meta.TryGetValue("height",out h)) return false;
            double width=Number(w),height=Number(h);
            if(width<1 || height<1) return false;
            double scale=Math.Min(1,256/Math.Max(width,height));
            return LocalEdits.HasPaint(values,Math.Max(1,(int)Math.Round(width*scale)),Math.Max(1,(int)Math.Round(height*scale)),slot);
        }

        private object ExportPresets()
        {
            List<object> presets = new List<object>();
            int schema=6;
            foreach (object[] p in db.Rows("SELECT name,engine,parameters FROM presets ORDER BY name_key"))
            {
                var values=FrameGeometry.SourceValues(json.Deserialize<Dictionary<string,double>>((string)p[2]));
                if(values.Any(v=>v.Value!=0&&V8Control(v.Key)))schema=Math.Max(schema,8);
                if(values.Any(v=>v.Value!=0&&V13Control(v.Key)))schema=13;
                if(values.Any(v=>v.Value!=0&&StudioFinish.RequiresV10(v.Key)))schema=Math.Max(schema,10);
                else if(values.Any(v=>v.Value!=0&&StudioFinish.DetailControl(v.Key)))schema=Math.Max(schema,9);
                presets.Add(new Dictionary<string,object>{{"name",p[0]},{"engineVersion",p[1]},{"values",values}});
            }
            if(schema<PortableVersion)foreach(Dictionary<string,object> preset in presets)preset["values"]=PortableParameters((Dictionary<string,double>)preset["values"],schema);
            return new Dictionary<string, object> { { "format", PresetFormat }, { "schemaVersion", schema }, { "presets", presets } };
        }

        private void ImportPresets(Dictionary<string, object> a)
        {
            bool replace = a.ContainsKey("replace") && BoolArg(a, "replace");
            Dictionary<string, object> bundle = Map(Arg(a, "bundle"));
            Fields(bundle, "format", "schemaVersion", "presets");
            CheckPacket(bundle, PresetFormat);
            HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (object item in Sequence(Arg(bundle, "presets"), 10000))
            {
                Dictionary<string, object> preset = Map(item);
                Fields(preset, "name", "engineVersion", "values");
                CheckEngine(TextArg(preset, "engineVersion", 100));
                string name = TextArg(preset, "name", 100).Trim();
                if (!names.Add(name)) throw new ArgumentException("交换文件中有同名的预设。");
                var values=ParseValues(Arg(preset,"values"),true);
                if(IntegerArg(bundle,"schemaVersion")<14&&values.Any(p=>V14Control(p.Key)&&p.Value!=LocalEdits.LayerDefault(p.Key)))
                    throw new ArgumentException("此预设需要第 14 版交换配方。");
                if(IntegerArg(bundle,"schemaVersion")<13&&values.Any(p=>p.Value!=0&&V13Control(p.Key)))
                    throw new ArgumentException("此预设需要第 13 版交换配方。");
                if(IntegerArg(bundle,"schemaVersion")<10&&values.Any(p=>p.Value!=0&&StudioFinish.RequiresV10(p.Key)))
                    throw new ArgumentException("此镜片或衣料保护预设需要第 10 版交换配方。");
                if(IntegerArg(bundle,"schemaVersion")<9&&values.Any(p=>p.Value!=0&&StudioFinish.DetailControl(p.Key)))
                    throw new ArgumentException("此修整预设需要第 9 版交换配方。");
                if(IntegerArg(bundle,"schemaVersion")<8&&values.Any(p=>p.Value!=0&&V8Control(p.Key)))
                    throw new ArgumentException("此局部调色预设需要第 8 版交换配方。");
                SavePreset(name, values, replace);
            }
        }
    }
}
