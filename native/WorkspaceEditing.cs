using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace AIPhotoStudio
{
    public sealed partial class StudioWorkspace
    {
        public object Dispatch(string method, Dictionary<string, object> args)
        {
            return Dispatch(method,args,null);
        }

        internal object Dispatch(string method,Dictionary<string,object> args,string catalogBase)
        {
            if (args == null) throw new ArgumentNullException("args");
            lock (gate)
            {
                if (method == "removedList")
                {
                    Fields(args);
                    return db.Transaction(false, delegate { return db.Rows(CatalogColumns+" JOIN removed_photos r ON r.photo_id=p.id ORDER BY r.rowid DESC").Select(CatalogPhoto).ToList(); });
                }
                if (method == "exportRecipe")
                {
                    Fields(args, "id");
                    return db.Transaction(false, delegate { return ExportRecipe(TextArg(args, "id", 100)); });
                }
                if (method == "exportPresets")
                {
                    Fields(args);
                    return db.Transaction(false, ExportPresets);
                }
                if (method == "previewProposal")
                {
                    Fields(args, "id", "revision", "values", "allowedKeys", "module", "engineVersion");
                    return db.Transaction(false, delegate
                    {
                        State s = Editable(args);
                        Dictionary<string, double> patch;
                        var parameters=PreviewParameters(s,args,out patch);
                        return new Dictionary<string, object> { { "id", s.Id }, { "revision", s.Revision }, { "parameters", parameters } };
                    });
                }
                return db.Transaction(true, delegate
                {
                    string changedPhoto=null;
                    if(method=="commit"&&catalogBase!=null&&catalogBase==issuedCatalogToken&&catalogBase==CatalogToken())
                    {
                        string id=TextArg(args,"id",100);
                        if(Equals(id,db.Scalar("SELECT value FROM settings WHERE name='selected'")))changedPhoto=id;
                    }
                    switch (method)
                    {
                        case "select":
                            Fields(args, "id"); SetSelected(TextArg(args, "id", 100)); break;
                        case "removePhoto": case "restorePhoto":
                            SetLibraryMembership(method, args); break;
                        case "setPhotoNote": SetPhotoNote(args); break;
                        case "favorite":
                            Fields(args, "id", "value");
                            string favoriteId = TextArg(args, "id", 100);
                            RequirePhoto(favoriteId);
                            db.Run("UPDATE photos SET favorite=? WHERE id=?", BoolArg(args, "value"), favoriteId);
                            break;
                        case "savePreset":
                            Fields(args, "id", "name", "replace");
                            State presetState = Current(TextArg(args, "id", 100));
                            CheckEngine(presetState.Engine);
                            SavePreset(TextArg(args, "name", 100), presetState.Values, args.ContainsKey("replace") && BoolArg(args, "replace"));
                            break;
                        case "commit": Commit(args); break;
                        case "createProject": case "renameProject": case "setProjectPhoto": case "deleteProject":
                            ProjectCommand(method, args); break;
                        case "batchCommit":
                            Fields(args, "items");
                            List<object> items = Sequence(Arg(args, "items"), 1000);
                            if (items.Count == 0) throw new ArgumentException("批处理需要至少一张照片。");
                            foreach (object item in items) Commit(Map(item));
                            break;
                        case "undo":
                        case "redo":
                            Fields(args, "id", "revision"); MoveCursor(Editable(args), method == "undo" ? -1 : 1); break;
                        case "restore":
                            Fields(args, "id", "revision", "index"); Restore(Editable(args), IndexArg(args)); break;
                        case "resetParam":
                        case "undoParam":
                        case "redoParam":
                            if (method == "resetParam") Fields(args, "id", "revision", "key", "index");
                            else Fields(args, "id", "revision", "key");
                            ChangeOne(method, Editable(args), args); break;
                        case "lock":
                            Fields(args, "id", "revision", "key", "locked"); ChangeLock(Editable(args), args); break;
                        case "copyRecipe":
                            Fields(args, "id", "revision", "fromId");
                            State to = Editable(args);
                            State from = Current(TextArg(args, "fromId", 100));
                            CheckEngine(from.Engine);
                            ApplyRecipe(to, from.Values, "复制编辑配方");
                            break;
                        case "importRecipe":
                            Fields(args, "id", "revision", "recipe", "mode"); ImportRecipe(Editable(args), args); break;
                        case "importPresets":
                            Fields(args, "bundle", "replace"); ImportPresets(args); break;
                        default: throw new ArgumentException("未知的图库命令。");
                    }
                    return changedPhoto==null?ReadSnapshot():ReadCommitSnapshot(changedPhoto,catalogBase);
                });
            }
        }

        private static void Fields(Dictionary<string, object> args, params string[] allowed)
        {
            foreach (string name in args.Keys)
                if (Array.IndexOf(allowed, name) == -1) throw new ArgumentException("出现未定义的字段 " + name + "。");
        }

        private static object Arg(Dictionary<string, object> args, string key)
        {
            object value;
            if (!args.TryGetValue(key, out value) || value == null) throw new ArgumentException("缺少字段 " + key + "。");
            return value;
        }

        private static string TextArg(Dictionary<string, object> args, string key, int maxLength)
        {
            string value = Arg(args, key) as string;
            if (String.IsNullOrWhiteSpace(value) || value.Length > maxLength || value.IndexOf('\0') >= 0)
                throw new ArgumentException("字段 " + key + " 的文字无效或过长。");
            return value;
        }

        private static bool BoolArg(Dictionary<string, object> args, string key)
        {
            object value = Arg(args, key);
            if (!(value is bool)) throw new ArgumentException("字段 " + key + " 必须为布尔值。");
            return (bool)value;
        }

        internal static double Number(object value)
        {
            if (!(value is double || value is float || value is decimal || value is int || value is long ||
                  value is short || value is byte || value is uint || value is ushort || value is sbyte || value is ulong))
                throw new ArgumentException("调整值必须为数字。");
            double number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            if (Double.IsNaN(number) || Double.IsInfinity(number)) throw new ArgumentException("调整值必须为有限数。");
            return number == 0 ? 0 : number;
        }

        private static long IntegerArg(Dictionary<string, object> args, string key)
        {
            double n = Number(Arg(args, key));
            if (n < 0 || n > 9007199254740991d || Math.Truncate(n) != n)
                throw new ArgumentException("字段 " + key + " 需要有效的非负整数。");
            return (long)n;
        }

        private static int IndexArg(Dictionary<string, object> args)
        {
            long index = IntegerArg(args, "index");
            if (index > Int32.MaxValue) throw new ArgumentException("历史索引超出范围。");
            return (int)index;
        }

        private static Dictionary<string, object> Map(object value)
        {
            Dictionary<string, object> direct = value as Dictionary<string, object>;
            if (direct != null) return direct;
            IDictionary source = value as IDictionary;
            if (source == null) throw new ArgumentException("需要具名字段组成的对象。");
            Dictionary<string, object> fields = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (DictionaryEntry field in source)
            {
                if (!(field.Key is string)) throw new ArgumentException("字段名称必须是文字。");
                fields.Add((string)field.Key, field.Value);
            }
            return fields;
        }

        private static List<object> Sequence(object value, int max)
        {
            IEnumerable list = value as IEnumerable;
            if (list == null || value is string || value is IDictionary) throw new ArgumentException("需要数组。");
            List<object> items = new List<object>();
            foreach (object item in list)
            {
                if (items.Count >= max) throw new ArgumentException("数组超出可接受的长度。");
                items.Add(item);
            }
            return items;
        }

        private ControlSpec Spec(string key)
        {
            ControlSpec spec;
            if (!controls.TryGetValue(key, out spec)) throw new ArgumentException("不支持的调整参数 " + key + "。");
            return spec;
        }

        private Dictionary<string, double> ParseValues(object input, bool complete)
        {
            Dictionary<string, double> result = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, object> p in Map(input))
            {
                ControlSpec spec = Spec(p.Key);
                double n = Number(p.Value);
                if (n < spec.min || n > spec.max) throw new ArgumentOutOfRangeException(p.Key, "调整值超出允许范围。");
                if((p.Key=="frame_mirror"||LocalEdits.LayerOutputKey(p.Key))&&n!=Math.Truncate(n))throw new ArgumentException("镜像、整层强度与背景选项必须使用整数。");
                if(PortraitControls.Bound(p.Key)&&n!=Math.Truncate(n))throw new ArgumentException("人脸调整和资源指纹需要整数。");
                if(p.Key==CameraRawModes.FrameKey&&n!=Math.Truncate(n))throw new ArgumentException("感光轨编号必须为整数。");
                if((p.Key=="frame_turn" || p.Key=="frame_ratio") && n!=Math.Truncate(n))throw new ArgumentException("旋转方向和裁切比例必须使用整数编号。");
                if ((spec.group == "LUT 资源" || spec.group=="局部资源" || spec.group=="分区资源" || spec.group=="人体资源") && Math.Truncate(n) != n) throw new ArgumentException("资源指纹必须为整数。");
                result.Add(p.Key, n);
            }
            if (complete)
            {
                foreach(ControlSpec control in controls.Values)
                    if(OptionalNeutral(control) && !result.ContainsKey(control.key)) result.Add(control.key,control.initial);
                if(result.Count != controls.Count) throw new ArgumentException("配方缺少此引擎所需的参数。");
                FrameGeometry.CheckPerspective(result);
            }
            return result;
        }

        private HashSet<string> Keys(object input)
        {
            HashSet<string> keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (object item in Sequence(input, controls.Count))
            {
                string key = item as string;
                if (key == null) throw new ArgumentException("参数名称需要使用文字。");
                Spec(key);
                if (!keys.Add(key)) throw new ArgumentException("参数名称重复。");
            }
            return keys;
        }

        private State Editable(Dictionary<string, object> args)
        {
            State state = Current(TextArg(args, "id", 100));
            CheckEngine(state.Engine);
            CheckRevision(state, IntegerArg(args, "revision"));
            return state;
        }

        private static void CheckRevision(State state, long expected)
        {
            if (expected != state.Revision) throw new InvalidOperationException("照片已有新的编辑版本，请根据最新参数重新生成或提交调整。");
        }

        private Dictionary<string,double> PreviewParameters(State state,Dictionary<string,object> args,out Dictionary<string,double> patch)
        {
            ParsePatch(args,out patch);
            RejectAssistantResources(patch);
            return Merged(state,patch);
        }

        private void Commit(Dictionary<string, object> a)
        {
            Fields(a, "id", "revision", "values", "label", "source", "requestId", "allowedKeys", "module", "engineVersion");
            string id = TextArg(a, "id", 100);
            long revision = IntegerArg(a, "revision");
            string request = TextArg(a, "requestId", 128);
            string source = a.ContainsKey("source") ? TextArg(a, "source", 80) : "manual";
            string label = a.ContainsKey("label") ? TextArg(a, "label", 240) : "手动调整";
            Dictionary<string, double> patch;
            string[] allowed = ParsePatch(a, out patch);
            bool assistant=source.StartsWith("ai",StringComparison.OrdinalIgnoreCase) || source.Equals("assistant",StringComparison.OrdinalIgnoreCase) ||
                source.Equals("provider",StringComparison.OrdinalIgnoreCase) || source.Contains("助手");
            if(assistant) RejectAssistantResources(patch);
            if(!assistant && request.StartsWith(ProposalPrefix,StringComparison.Ordinal))
                throw new InvalidOperationException("提案只能作为助手调整确认。");
            string fingerprint;
            object[] normalized = { id, revision, label, source, a.ContainsKey("module") ? a["module"] : null, allowed,
                patch.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new object[] { p.Key, p.Value.ToString("R", CultureInfo.InvariantCulture) }).ToArray() };
            using (SHA256 hash = SHA256.Create())
                fingerprint = Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(json.Serialize(normalized))));
            string previous = (string)db.Scalar("SELECT fingerprint FROM requests WHERE photo_id=? AND request_id=?", id, request);
            if (previous != null)
            {
                if (previous != fingerprint) throw new InvalidOperationException("此请求标识已用于不同的调整。");
                return;
            }
            State state = Current(id);
            CheckEngine(state.Engine);
            CheckRevision(state, revision);
            if(assistant) CheckIssuedSelection(state,request,patch);
            var merged=Merged(state,patch);
            if(patch.ContainsKey(CameraRawModes.FrameKey)&&CameraRawModes.Selected(merged)!=0)
            {
                RequireSecondRawTrack(state.Id);
                RecordBoundOriginal(state.Id);
            }
            // A protection setting may be saved on a new, empty fabric mask. It changes no pixels by itself.
            foreach(int slot in patch.Where(p=>p.Value!=LocalEdits.LayerDefault(p.Key)&&(LocalEdits.ExtraKey(p.Key)||p.Key=="local_strength")&&!StudioFinish.FabricGuard(p.Key)).Select(p=>LocalEdits.ToneSlot(p.Key)).Distinct())
                if(!HasEffectivePaint(state.Id,merged,slot))throw new InvalidOperationException("此蒙版没有可调整的覆盖区域。");
            if(patch.ContainsKey("mask_ref0")&&HasMaskReference(merged))
            {
                var maskSet=LocalEdits.ReadSet(merged);
                if(maskSet.sourceHash!=null)
                {
                    string bound=(string)db.Scalar("SELECT value FROM settings WHERE name=?","face-source/"+state.Id);
                    if(maskSet.sourceHash!=OriginalHash(state.Id)||(bound!=null&&bound!=maskSet.sourceHash))
                        throw new InvalidOperationException("此蒙版与记录的原片指纹不同。");
                }
            }
            if(patch.ContainsKey("portrait_ref0")){
                var scene=PortraitAssets.Resolve(merged);
                if(scene==null||scene.SourceHash!=OriginalHash(state.Id)||!PortraitAssets.CanReplace(state.Values,merged,state.Locks))
                    throw new InvalidOperationException("所选人脸与原片或已有调整不符。");
                RecordBoundOriginal(state.Id);
            }
            if(patch.Any(p=>p.Value!=0 && (p.Key=="local_shadows" || p.Key=="local_highlights" || p.Key=="local_temperature" ||
                (p.Key=="local_exposure" && patch.ContainsKey("mask_ref0")))) && !HasEffectivePaint(state.Id,merged))
                throw new InvalidOperationException("请先在照片上画出要调整的蒙版。");
            if(patch.ContainsKey("body_ref0") && BodyAssets.ResourceHash(merged)!=null)
            {
                if(!BodyAssets.MatchesSource(merged, OriginalHash(state.Id))) throw new InvalidOperationException("人体资源与原片不匹配。");
                RecordBoundOriginal(state.Id);
            }
            if((patch.ContainsKey("mask_ref0") && HasMaskReference(merged)) || (patch.ContainsKey("segment_ref0") && HasSegmentReference(merged))) RecordBoundOriginal(state.Id);
            if(patch.Keys.Any(FrameGeometry.IsKey) && FrameGeometry.Active(merged))RecordBoundOriginal(state.Id);
            Append(state, merged, state.Locks, label, source, "edit");
            db.Run("INSERT INTO requests(photo_id,request_id,fingerprint) VALUES(?,?,?)", id, request, fingerprint);
        }

        private string[] ParsePatch(Dictionary<string, object> a, out Dictionary<string, double> patch)
        {
            string module = a.ContainsKey("module") ? TextArg(a, "module", 80) : null;
            if (a.ContainsKey("engineVersion")) CheckEngine(TextArg(a, "engineVersion", 100));
            HashSet<string> allowed = a.ContainsKey("allowedKeys") ? Keys(Arg(a, "allowedKeys")) : null;
            patch = ParseValues(Arg(a, "values"), false);
            if (patch.Count == 0) throw new ArgumentException("调整方案中没有参数。");
            CheckResourceGroup(patch,"lut_ref");
            CheckResourceGroup(patch,"mask_ref");
            CheckResourceGroup(patch,"segment_ref");
            CheckResourceGroup(patch,"body_ref");
            CheckResourceGroup(patch,"portrait_ref");
            foreach (string key in patch.Keys)
            {
                if (Spec(key).group == "人脸区域") throw new InvalidOperationException("人脸区域只能从本机识别结果中选择。");
                if ((allowed != null && !allowed.Contains(key)) || (module != null && Spec(key).group != module))
                    throw new InvalidOperationException("调整参数 " + key + " 超出了本次选择的范围。");
            }
            return allowed == null ? null : OrderedLocks(allowed);
        }

        private static void CheckResourceGroup(Dictionary<string,double> patch,string prefix)
        {
            int count=0;
            for(int n=0;n<5;n++) if(patch.ContainsKey(prefix+n)) count++;
            if(count!=0 && count!=5) throw new ArgumentException("资源指纹的五个组成部分必须一起提交。");
        }

        private static Dictionary<string, double> Merged(State state, Dictionary<string, double> patch)
        {
            var values = new Dictionary<string, double>(state.Values, StringComparer.Ordinal);
            foreach (KeyValuePair<string, double> p in patch)
            {
                Unlocked(state, p.Key);
                values[p.Key] = p.Value;
            }
            if(patch.ContainsKey(CameraRawModes.FrameKey))CheckRawTrackChange(state.Values,values);
            if(patch.Keys.Any(FrameGeometry.IsPerspectiveKey))FrameGeometry.CheckPerspective(values);
            EnsureFaceForEdits(values);
            if(patch.Keys.Any(k=>LocalEdits.ExtraKey(k)||k=="local_strength")||patch.ContainsKey("mask_ref0"))
                CheckMaskSlots(values);
            if(patch.ContainsKey("mask_ref0"))LocalEdits.CheckLockedTargets(state.Values,values,state.Locks);
            if(patch.Keys.Any(k=>k.StartsWith("body_",StringComparison.Ordinal)) && BodyAssets.ResourceHash(values)!=null && !BodyAssets.Supported(values))
                throw new InvalidOperationException("请重新识别并保存这张照片中的人体。");
            if(patch.Any(p=>p.Value!=0 && BodyEdits.BaseKey(p.Key)!=null && !BodyAssets.CanAdjust(values,p.Key)))
                throw new InvalidOperationException("所选人物的这个部位无法可靠定位，请选择面板中可调的部位。");
            if(patch.Keys.Any(k=>k.StartsWith("segment_",StringComparison.Ordinal)) && HasSegmentAdjustment(values) && !SegmentAssets.Supported(values))
                throw new InvalidOperationException("请先在这张照片上标记并保存可读取的前景与背景分区。");
            return values;
        }

        private void RejectAssistantResources(Dictionary<string,double> patch)
        {
            foreach(string key in patch.Keys)
            {
                string group=Spec(key).group;
                if(key==CameraRawModes.FrameKey)throw new InvalidOperationException("请在照片信息中选择 RAW 感光轨。");
                if(group==PortraitControls.ResourceGroup)throw new InvalidOperationException("助手不能写入人脸关键点。");
                if(group=="人脸区域" || group=="局部资源" || group=="LUT 资源" || group=="分区资源" || group=="人体资源")
                    throw new InvalidOperationException("助手不能写入检测坐标、蒙版或 LUT 资源指纹。");
            }
        }

        private static void EnsureFaceForEdits(Dictionary<string,double> values)
        {
            if(values.Any(p=>p.Value!=0&&PortraitControls.Index(p.Key)>=0&&!PortraitAssets.CanAdjust(values,p.Key)))
                throw new InvalidOperationException("请先识别人脸，再选择可见的部位。");
            if(values.Any(p=>p.Key.StartsWith("body_",StringComparison.Ordinal) && !p.Key.StartsWith("body_ref",StringComparison.Ordinal) && p.Value!=0) && BodyAssets.ResourceHash(values)==null)
                throw new InvalidOperationException("请先识别并选择照片中的人物，再调整美型参数。");
            if(HasSegmentAdjustment(values) && !HasSegmentReference(values))
                throw new InvalidOperationException("分区曝光依赖当前照片已保存的分区。");
            if(values.Any(p=>p.Key.StartsWith("portrait_face_",StringComparison.Ordinal) && p.Value!=0) && !PortraitEdits.HasFace(values))
                throw new InvalidOperationException("人脸区域不完整或超出照片范围。");
            if(values.Any(p=>p.Key.StartsWith("portrait_skin_",StringComparison.Ordinal) && p.Value!=0) && !PortraitEdits.HasFace(values))
                throw new InvalidOperationException("请先识别并选择此照片中的人脸，再应用肤质调整。");
        }

        public object SetDetectedFace(string id, long revision, Dictionary<string,double> patch)
        {
            if(patch==null || patch.Count!=4 || !PortraitEdits.HasFace(patch) ||
                !new[]{"portrait_face_x","portrait_face_y","portrait_face_w","portrait_face_h"}.All(patch.ContainsKey))
                throw new ArgumentException("请选择此照片的本机识别结果。");
            lock(gate) return db.Transaction(true,delegate
            {
                State s=Current(id); CheckEngine(s.Engine); CheckRevision(s,revision);
                var values=new Dictionary<string,double>(s.Values,StringComparer.Ordinal);
                foreach(var pair in patch) values[pair.Key]=pair.Value;
                RecordBoundOriginal(id);
                Append(s,values,s.Locks,"选择识别人脸","portrait","face");
                return ReadSnapshot();
            });
        }

        private static void Unlocked(State s, string key)
        {
            if (s.Locks.Contains(key)) throw new InvalidOperationException("参数 " + key + " 已锁定，请先解锁。");
        }

        private static Dictionary<string, double> Difference(Dictionary<string, double> before, Dictionary<string, double> after)
        {
            return after.Where(p => before[p.Key] != p.Value).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        }

        private void Append(State s, Dictionary<string, double> values, HashSet<string> locks, string label, string source, string operation)
        {
            Dictionary<string, double> changes = Difference(s.Values, values);
            if(changes.Keys.Any(FrameGeometry.IsPerspectiveKey))FrameGeometry.CheckPerspective(values);
            if (changes.Count == 0 && s.Locks.SetEquals(locks)) return;
            int next = checked(s.Cursor + 1);
            db.Run("DELETE FROM history WHERE photo_id=? AND seq>?", s.Id, s.Cursor);
            WriteFrame(s.Id, next, label, source, Now(), operation, values, locks, changes);
            db.Run("UPDATE photos SET revision=revision+1,cursor=? WHERE id=?", next, s.Id);
        }

        private void MoveCursor(State s, int direction)
        {
            int next = checked(s.Cursor + direction);
            if (next < 0 || db.Scalar("SELECT 1 FROM history WHERE photo_id=? AND seq=?", s.Id, next) == null)
                throw new InvalidOperationException(direction < 0 ? "已经是最早的版本。" : "已经是最新的版本。");
            db.Run("UPDATE photos SET revision=revision+1,cursor=? WHERE id=?", next, s.Id);
        }

        private object[] HistoryRow(State s, int index)
        {
            List<object[]> rows = db.Rows("SELECT parameters,locks FROM history WHERE photo_id=? AND seq=?", s.Id, index);
            if (rows.Count == 0) throw new ArgumentException("历史版本不存在。");
            return rows[0];
        }

        private void Restore(State s, int index)
        {
            object[] h = HistoryRow(s, index);
            Dictionary<string, double> values = StoredValues((string)h[0]);
            HashSet<string> locks = new HashSet<string>(json.Deserialize<string[]>((string)h[1]), StringComparer.Ordinal);
            Append(s, values, locks, "恢复到版本 " + index, "manual", "restore");
        }

        private void ChangeLock(State s, Dictionary<string, object> a)
        {
            string key = TextArg(a, "key", 100);
            ControlSpec spec = Spec(key);
            if(spec.group==PortraitControls.ResourceGroup || (PortraitControls.Index(key)>=0&&!PortraitAssets.CanAdjust(s.Values,key)))
                throw new InvalidOperationException("这项人脸参数需要在识别之后锁定。");
            if(spec.group=="人脸区域") throw new InvalidOperationException("人脸区域由识别结果统一管理。");
            bool locked = BoolArg(a, "locked");
            if(locked&&(StudioFinish.DetailControl(key)||LocalEdits.LayerOutputKey(key)))CheckDetailLocks(s.Values,new[]{key});
            if(locked&&LocalEdits.ExtraKey(key)&&V8Control(key)&&!LocalEdits.ReadSet(s.Values).Exists(LocalEdits.ToneSlot(key)))
                throw new InvalidOperationException("请先选择对应的蒙版，再锁定它的参数。");
            HashSet<string> locks = new HashSet<string>(s.Locks, StringComparer.Ordinal);
            if (locked) locks.Add(key); else locks.Remove(key);
            Append(s, s.Values, locks, (locked ? "锁定" : "解锁") + spec.label, "manual", "lock");
        }

        private void ChangeOne(string method, State s, Dictionary<string, object> a)
        {
            string key = TextArg(a, "key", 100);
            ControlSpec spec = Spec(key);
            if(spec.group==PortraitControls.ResourceGroup || spec.group=="人脸区域" || spec.group=="LUT 资源" || spec.group=="局部资源" || spec.group=="分区资源" || spec.group=="人体资源")
                throw new InvalidOperationException("请通过整体撤销或恢复完整历史来还原资源。");
            Unlocked(s, key);
            double next;
            long sourceFrame=-1;
            string verb;
            if (method == "resetParam")
            {
                if(a.ContainsKey("index"))sourceFrame=IndexArg(a);
                next = sourceFrame>=0 ? StoredValues((string)HistoryRow(s,(int)sourceFrame)[0])[key] : spec.initial;
                verb = "恢复";
            }
            else
            {
                // Rebuild this one control's undo stacks only when that command is used.
                // Ordinary drags never copy or persist an ever-growing set of stacks.
                var undo=new Stack<KeyValuePair<long,double>>();
                var redo=new Stack<KeyValuePair<long,double>>();
                double current = spec.initial;
                foreach (object[] h in db.Rows("SELECT operation,changes,seq FROM history WHERE photo_id=? AND seq<=? ORDER BY seq", s.Id, s.Cursor))
                {
                    double changed;
                    if (!json.Deserialize<Dictionary<string, double>>((string)h[1]).TryGetValue(key, out changed)) continue;
                    string op = (string)h[0];
                    var prior=new KeyValuePair<long,double>((long)h[2]-1,current);
                    if (op == "undoParam") { undo.Pop(); redo.Push(prior); }
                    else if (op == "redoParam") { redo.Pop(); undo.Push(prior); }
                    else { undo.Push(prior); redo.Clear(); }
                    current = changed;
                }
                bool backward = method == "undoParam";
                var stack = backward ? undo : redo;
                if (stack.Count == 0) throw new InvalidOperationException("此参数没有可" + (backward ? "撤销" : "重做") + "的调整。");
                next = stack.Peek().Value;
                sourceFrame=stack.Peek().Key;
                verb = backward ? "撤销" : "重做";
            }
            Dictionary<string, double> values = new Dictionary<string, double>(s.Values, StringComparer.Ordinal);
            values[key] = next;
            if(key==CameraRawModes.FrameKey)CheckRawTrackChange(s.Values,values);
            if(next!=0&&sourceFrame>=0&&(key.StartsWith("local_")||key.StartsWith("portrait_")||key.StartsWith("segment_")||key.StartsWith("body_"))&&
                CameraRawModes.Selected(StoredValues((string)HistoryRow(s,(int)sourceFrame)[0]))!=CameraRawModes.Selected(s.Values))
                throw new InvalidOperationException("这项局部记录来自另一条感光轨，请恢复完整历史版本。");
            EnsureFaceForEdits(values);
            if(next!=spec.initial&&sourceFrame>=0&&(LocalEdits.ExtraKey(key)||key=="local_strength"||key=="local_temperature"||key=="local_saturation"))
            {
                int slot=LocalEdits.ToneSlot(key);
                var old=LocalEdits.ReadSet(StoredValues((string)HistoryRow(s,(int)sourceFrame)[0]));
                string current=LocalEdits.ReadSet(values).IdFor(slot);
                if(current==null||current!=old.IdFor(slot))throw new InvalidOperationException("该记录属于已移除的蒙版，请从历史面板恢复完整版本。");
            }
            if(next!=0 && PortraitControls.Index(key)>=0){
                var previous=StoredValues((string)HistoryRow(s,(int)sourceFrame)[0]);
                var refs=previous.Where(p=>p.Key.StartsWith("portrait_ref",StringComparison.Ordinal)).ToDictionary(p=>p.Key,p=>p.Value);
                if(!PortraitAssets.CanReplace(refs,values,new[]{key}))
                    throw new InvalidOperationException("这条记录属于另一张脸，请恢复完整历史。");
            }
            if(next!=0 && BodyEdits.PersonIndex(key)>=0)
            {
                var source=StoredValues((string)HistoryRow(s,(int)sourceFrame)[0]);
                var refs=source.Where(p=>p.Key.StartsWith("body_ref",StringComparison.Ordinal)).ToDictionary(p=>p.Key,p=>p.Value);
                if(!BodyAssets.CanAdjust(values,key) || !BodyAssets.CanReplace(refs,values,new[]{key}))
                    throw new InvalidOperationException("该记录对应的人物或部位已改变。请在历史面板恢复完整版本。");
            }
            if(LocalEdits.LayerOutputKey(key))CheckMaskSlots(values);
            Append(s, values, s.Locks, verb + spec.label, "manual", method);
        }

        private void SavePreset(string name, Dictionary<string, double> values, bool replace)
        {
            values=values.ToDictionary(p=>p.Key,p=>
                p.Key==CameraRawModes.FrameKey || FrameGeometry.IsKey(p.Key) ||
                PortraitControls.Bound(p.Key) || p.Key=="local_strength" ||
                LocalEdits.ExtraKey(p.Key)||Spec(p.Key).group=="人脸区域" || Spec(p.Key).group=="局部资源" || p.Key=="local_warp" || p.Key.StartsWith("segment_",StringComparison.Ordinal) || p.Key.StartsWith("body_",StringComparison.Ordinal) ? Spec(p.Key).initial : p.Value,StringComparer.Ordinal);
            name = PresetName(name);
            string key = name.ToUpperInvariant();
            List<object[]> existing = db.Rows("SELECT engine,parameters FROM presets WHERE name_key=?", key);
            if (existing.Count > 0 && !replace)
            {
                if ((string)existing[0][0] == Imaging.EngineVersion &&
                    Difference(StoredValues((string)existing[0][1]), values).Count == 0) return;
                throw new InvalidOperationException("此名称的预设已存在，请另取名称或确认覆盖。");
            }
            db.Run("INSERT OR REPLACE INTO presets(name_key,name,engine,parameters) VALUES(?,?,?,?)", key, name, Imaging.EngineVersion, json.Serialize(values));
        }

        private static string PresetName(string input)
        {
            if(String.IsNullOrWhiteSpace(input)) throw new ArgumentException("请输入预设名称。");
            string name=input.Trim();
            if(name.Length>100 || name.Any(Char.IsControl)) throw new ArgumentException("预设名称不能超过 100 个字符，也不能包含控制字符。");
            return name;
        }

        public object SavePlannedPreset(string photoId,long revision,string name)
        {
            lock(gate) return db.Transaction(true,delegate
            {
                string confirmed=PresetName(name);
                string selected=(string)db.Scalar("SELECT value FROM settings WHERE name='selected'");
                if(!String.Equals(selected,photoId,StringComparison.Ordinal)) throw new InvalidOperationException("当前照片已切换，请在要保存的照片上重新提出申请。");
                State s=Current(photoId);
                CheckEngine(s.Engine); CheckRevision(s,revision);
                if(db.Scalar("SELECT 1 FROM presets WHERE name_key=?",confirmed.ToUpperInvariant())!=null)
                    throw new InvalidOperationException("已存在同名预设，请换一个名称。");
                SavePreset(confirmed,s.Values,false);
                return ReadSnapshot();
            });
        }
    }
}
