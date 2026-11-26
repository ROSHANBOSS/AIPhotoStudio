using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace AIPhotoStudio
{
    public static partial class Planner
    {
        private static readonly RegexOptions Ignore = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        private const string Numeric = @"[+-]?(?:\d+(?:\.\d+)?|\.\d+)";
        private static readonly Regex HistoryPair = new Regex(@"^(?<key>[a-z][a-z0-9_]*)=(?<a>[-+0-9.eE]+)->(?<b>[-+0-9.eE]+)$", RegexOptions.CultureInvariant);
        private static readonly Dictionary<string, string> Aliases = new Dictionary<string, string> {
            {"exposure","曝光|曝光量|exposure"}, {"contrast","对比度?|反差"}, {"highlights","高光|亮部"},
            {"shadows","阴影|暗部"}, {"whites","白色色阶|白色场|白点"}, {"blacks","黑色色阶|黑色场|黑点"},
            {"temperature","色温|冷暖|暖调|冷调|暖一些|冷一些|暖一点|冷一点|偏暖|偏冷"},
            {"tint","色调偏移|偏色|色调值|洋红偏移"}, {"vibrance","自然饱和度|鲜艳度|鲜活度"},
            {"saturation","饱和度|饱和|褪色"}, {"clarity","清晰度|清晰一些|清晰一点"},
            {"dehaze","去雾|除雾"}, {"sharpness","锐化|锐度"}, {"noise","降噪|减噪"},
            {"noise_chroma","彩色降噪|色彩降噪|色度降噪|彩色噪点|彩噪"},
            {"noise_luma","明暗降噪|亮度降噪|灰色颗粒|明暗噪点|亮度噪点"}
        };
        private static readonly string[] BandKeys = {"red","orange","yellow","green","aqua","blue","purple","magenta"};
        private static readonly string[] BandNames = {"红色","橙色","黄色","绿色","青色","蓝色","紫色","洋红"};

        private sealed class Previous
        {
            internal string Key;
            internal double? Before, After;
        }
        private sealed class Context
        {
            internal Dictionary<string, ControlSpec> Specs;
            internal Dictionary<string, double> Current;
            internal HashSet<string> Allowed, Locked;
            internal List<List<Previous>> History;
            internal Dictionary<string, double> Analysis;
            internal bool PaintRequest, SkinRequest, SegmentRequest;
        }

        internal static bool IsHalfGroup(string text)
        {
            return Regex.IsMatch((text??"").Trim(),@"^(?:请)?(?:把|将)?(?:刚才|刚刚|上次|上一次|上一步)(?:的)?(?:这组|那组|这次|那次|一组)?(?:的)?(?:调色|调整|效果|参数|强度)?(?:的)?(?:强度|效果)?(?:减半|减弱一半|降低一半|减轻一半|减到一半)[。！]?$");
        }

        public static async Task<Dictionary<string, object>> PlanAsync(string channelDataDir, string text, string scope,
            Dictionary<string, double> current, string[] locked, Dictionary<string, object> analysis, string[] history)
        {
            if (String.IsNullOrWhiteSpace(text) || text.Length > 3000) throw new ArgumentException("请输入 1 至 3000 字的修图指令。");
            text = text.Trim().Normalize(NormalizationForm.FormKC).Replace('−', '-');
            if(IsHalfGroup(text))
            {
                var cHalf=MakeContext(scope,current,locked,analysis,history,false,false);
                var last=(history??new string[0]).LastOrDefault();
                var patch=new Dictionary<string,double>();
                foreach(var part in (last??"").Split(';'))
                {
                    var match=HistoryPair.Match(part);string key=match.Groups["key"].Value;
                    double before,after;
                    if(!match.Success||!cHalf.Allowed.Contains(key)||key.StartsWith("portrait_")||key.StartsWith("local_")||key.StartsWith("segment_")
                        ||!TryDouble(match.Groups["a"].Value,out before)||!TryDouble(match.Groups["b"].Value,out after)
                        ||Math.Abs(cHalf.Current[key]-after)>0.000001)
                        throw new InvalidOperationException("上一步没有整组可用的调色记录，或调整范围、锁定状态已经改变。");
                    var spec=cHalf.Specs[key];
                    patch[key]=Math.Round(spec.initial+Math.Round(((before+after)/2-spec.initial)/spec.step,MidpointRounding.AwayFromZero)*spec.step,6);
                }
                return Result(cHalf,patch,"上次调色减半","按上一次调整的实际增量减半，确认后生效。");
            }
            Dictionary<string,object> bodyProposal;
            if(TryBodyPlan(text,scope,current,locked,analysis,history,out bodyProposal)) return bodyProposal;
            Dictionary<string,object> portraitProposal;
            if(TryPortraitPlan(text,scope,current,locked,analysis,history,out portraitProposal))return portraitProposal;
            bool paint=scope=="手绘局部" || Regex.IsMatch(text,@"(?:我(?:刚刚|已经|刚)?画(?:好|过|的)?(?:的)?|(?:手绘|画笔)(?:的)?|(?:当前|已有|现有|刚画|已画|已保存|这个|这块|选中)(?:的)?(?:手绘)?)(?:区域|蒙版|选区)|(?:蒙版|选区)(?:内|里|中)");
            bool repeat=Regex.IsMatch(text,@"^(?:请)?(?:再多一点|再来一点|再强一点|继续一点|恢复上一步|撤销刚才的?调整|再少一点)[。！]?$");
            string lastHistory=history==null ? "" : (history.LastOrDefault()??"");
            string lastKey=lastHistory.Contains(";") ? "" : lastHistory.Split('=')[0];
            if(repeat && lastKey.StartsWith("local_",StringComparison.Ordinal)) paint=true;
            bool segment=Regex.IsMatch(text,"前景|主体|背景");
            bool repeatLight=repeat || Regex.IsMatch(text,@"^(?:请)?(?:再|继续)(?:压暗|提亮|亮|暗|调暗|调亮).{0,7}$");
            if(!segment && repeatLight && lastKey.StartsWith("segment_",StringComparison.Ordinal)) segment=true;
            if(!segment && repeatLight && lastHistory.Contains("segment_foreground=") && lastHistory.Contains("segment_background="))
                throw new InvalidOperationException("上一步同时调整了前景和背景，请指定这次要调整的分区。");
            object segmentState;
            if(segment && (current==null || !current.Any(p=>p.Key.StartsWith("segment_ref",StringComparison.Ordinal) && p.Value!=0) ||
                analysis==null || !analysis.TryGetValue("hasSegment",out segmentState) || !Object.Equals(segmentState,true)))
                throw new InvalidOperationException("请先在画布上标记前景与背景并保存分区，再调整对应区域。");
            if(segment && (paint || Regex.IsMatch(text,"人物|人像|人脸|五官|衣服|衣物|皮肤|头发|天空|草地|墙|桌子|虚化|模糊|柔化|锐化|饱和|色相|色温|换色|换成|偏蓝|更蓝|更红|更绿|变蓝|变红|变绿|去雾")))
                throw new InvalidOperationException("当前分区由你在画布上的标记确定，只支持前景或背景曝光。请单独说明要提亮或压暗的分区。");
            object paintState;
            bool savedPaint=current!=null && current.Any(p=>p.Key.StartsWith("mask_ref",StringComparison.Ordinal) && p.Value!=0) &&
                analysis!=null && analysis.TryGetValue("hasPaintMask",out paintState) && Object.Equals(paintState,true);
            if(paint && !savedPaint) throw new InvalidOperationException("请先画出区域并保存蒙版。液化位移、已擦空的画笔和保护笔迹没有可调色的蒙版像素。");
            if(paint && Regex.IsMatch(text,"全局|整张|全图")) throw new InvalidOperationException("请分开提交手绘区域和整张照片的调整。");
            bool skin=Regex.IsMatch(text,"磨皮|皮肤|肤色|亮肤|祛红|泛红|毛孔|恢复.{0,4}纹理|脸.{0,6}(亮一|提亮)|提亮.{0,4}脸") ||
                (repeat && lastKey.StartsWith("portrait_skin_",StringComparison.Ordinal));
            if (Regex.IsMatch(text, "祛痘|祛斑|去痘|去斑|黑眼圈|五官|瘦脸|下颌|鼻翼|鼻梁|嘴唇|牙齿|眼白|眼睛|眼距|头发|衣服|衣物|美型|液化|瘦腰|肩宽|臀部|腿部|腿长|腿型|大腿|小腿|手臂|拉长腿|脸型|某个人|天空|草地|只.{0,4}(人像|人物)|人物.{0,6}(突出|提亮|变亮)|(?:提亮|压暗|虚化|突出).{0,4}人物") ||
                (!paint && !segment && Regex.IsMatch(text,"蒙版|选区|局部")) ||
                (!skin && Regex.IsMatch(text,"脸部|面部|人脸")))
                throw new InvalidOperationException("这条指令需要人物识别或区域蒙版。当前助手可以调整整张照片的光影和色彩。");
            if(skin && (current==null || !PortraitEdits.HasFace(current))) throw new InvalidOperationException("这条肤质调整需要先识别并选择照片中的人脸。");
            if (Regex.IsMatch(text, "(保存|存为|另存|导出).{0,8}(预设|图片|文件|照片)|删除照片"))
                throw new InvalidOperationException("这项操作请使用预设库或导出菜单，助手仅提出可预览的参数调整。");
            Context c = MakeContext(scope, current, locked, analysis, history,paint,segment);
            c.SkinRequest=skin;
            string[] clauses = Clauses(text);
            if(segment && (clauses.All(Negative) || !Regex.IsMatch(text,"曝光|亮|暗|[档挡]|调高|调低|上调|下调|提高|降低|压低|恢复|撤销|还原|再多|再少|再来|继续|设为|设到")))
                throw new InvalidOperationException("请明确要对前景或背景提亮、压暗或设置多少 EV。");
            if(skin) c.Allowed.IntersectWith(c.Specs.Keys.Where(k=>k.StartsWith("portrait_skin_",StringComparison.Ordinal)).Concat(clauses.SelectMany(clause=>Mentioned(c,clause))).ToArray());
            if(!skin) c.Allowed.RemoveWhere(k=>k.StartsWith("portrait_skin_",StringComparison.Ordinal));
            bool styled=Imaging.Presets.Keys.Any(text.Contains) || Regex.IsMatch(text,"日系清透|黑白风格|自然一|高级一|自动优化");
            var explicitKeys=clauses.Where(clause=>!Negative(clause)).SelectMany(clause=>Mentioned(c,clause)).Distinct().ToArray();
            if(explicitKeys.Length==0 && repeat && c.History.Count>0 && c.History.Last().Count==1) explicitKeys=new[]{c.History.Last()[0].Key};
            if(explicitKeys.Length>0 && !styled) c.Allowed.IntersectWith(explicitKeys);
            ApplyTextLimits(c, clauses);
            Settings config = ReadSettings(channelDataDir);
            if (config.Kind == "rules") return Local(text, clauses, c);
            return await Task.Run(delegate { return Remote(config, text, c); }).ConfigureAwait(false);
        }

        private static bool Visible(ControlSpec spec)
        {
            if(Aliases.ContainsKey(spec.key) || spec.key=="lut_strength") return true;
            if(spec.group=="RGB 曲线" || spec.group=="HSL 八色" || spec.group=="色彩分级" || spec.group=="色彩校准") return true;
            return spec.key=="portrait_skin_light" || spec.key=="portrait_skin_smooth" || spec.key=="portrait_skin_red" ||
                spec.key=="local_exposure" || spec.key=="local_shadows" || spec.key=="local_highlights" || spec.key=="local_saturation" || spec.key=="local_temperature" || spec.key=="local_smooth" ||
                spec.key=="segment_foreground" || spec.key=="segment_background";
        }

        private static Context MakeContext(string scope, Dictionary<string, double> current, string[] locked, Dictionary<string, object> analysis, string[] history, bool paint,bool segment)
        {
            if (current == null) throw new ArgumentNullException("current");
            var specs = Imaging.Controls.Where(Visible).ToDictionary(s => s.key, StringComparer.Ordinal);
            var values = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (ControlSpec spec in specs.Values)
            {
                double v;
                if (!current.TryGetValue(spec.key, out v)) v = spec.initial;
                if (!Finite(v) || v < spec.min || v > spec.max) throw new ArgumentException("当前编辑参数超出了引擎范围。");
                values.Add(spec.key, v);
            }
            var allowed = new HashSet<string>(specs.Values.Where(s => String.IsNullOrEmpty(scope) || scope == "all" || s.group == scope).Select(s => s.key), StringComparer.Ordinal);
            if (allowed.Count == 0) throw new ArgumentException("所选调整模块不可用。");
            var locks = new HashSet<string>(locked ?? new string[0], StringComparer.Ordinal);
            allowed.ExceptWith(locks);
            if(!current.Any(p=>p.Key.StartsWith("lut_ref",StringComparison.Ordinal) && p.Value!=0)) allowed.Remove("lut_strength");
            object allowLegacy;
            if(!PortraitEdits.HasFace(current) || (analysis!=null&&analysis.TryGetValue("allowLegacyFace",out allowLegacy)&&Object.Equals(allowLegacy,false)))
                allowed.RemoveWhere(k=>k.StartsWith("portrait_skin_",StringComparison.Ordinal));
            if(paint) allowed.IntersectWith(new[]{"local_exposure","local_shadows","local_highlights","local_saturation","local_temperature","local_smooth"});
            else allowed.RemoveWhere(k=>k.StartsWith("local_",StringComparison.Ordinal));
            if(segment) allowed.IntersectWith(new[]{"segment_foreground","segment_background"});
            else allowed.RemoveWhere(k=>k.StartsWith("segment_",StringComparison.Ordinal));
            var groups = new List<List<Previous>>();
            string[] entries = history ?? new string[0];
            foreach (string entry in entries.Skip(Math.Max(0, entries.Length - 12)))
            {
                if (String.IsNullOrEmpty(entry)) continue;
                var changes = new List<Previous>();
                foreach (string part in entry.Split(';').Take(specs.Count))
                {
                    Match pair = HistoryPair.Match(part);
                    if (pair.Success)
                    {
                        string key = pair.Groups["key"].Value;
                        double a, b;
                        if (specs.ContainsKey(key) && TryDouble(pair.Groups["a"].Value, out a) && TryDouble(pair.Groups["b"].Value, out b) &&
                            a >= specs[key].min && a <= specs[key].max && b >= specs[key].min && b <= specs[key].max)
                            changes.Add(new Previous { Key = key, Before = a, After = b });
                    }
                    else if (specs.ContainsKey(part)) changes.Add(new Previous { Key = part });
                }
                if (changes.Count > 0) groups.Add(changes);
            }
            return new Context { Specs = specs, Current = values, Allowed = allowed, Locked = locks, History = groups, Analysis = PhotoSummary(analysis), PaintRequest=paint,SegmentRequest=segment };
        }

        private static bool Finite(double number) { return !Double.IsNaN(number) && !Double.IsInfinity(number); }
        private static bool TryDouble(string value, out double result)
        {
            return Double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result) && Finite(result);
        }

        private static bool NumericValue(object value, out double number)
        {
            number = 0;
            if (!(value is int || value is long || value is double || value is decimal || value is float || value is uint || value is ushort || value is byte))
                return false;
            number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            return Finite(number);
        }

        private static Dictionary<string, double> PhotoSummary(Dictionary<string, object> input)
        {
            var result = new Dictionary<string, double>(StringComparer.Ordinal);
            if (input == null) return result;
            foreach (string key in new[] {"medianLuminance","meanLuminance","blackClip","whiteClip"})
            {
                object item; double n;
                if (input.TryGetValue(key, out item) && NumericValue(item, out n) && n >= 0 && n <= 1) result[key] = n;
            }
            object h;
            if (!input.TryGetValue("histogram", out h) || !(h is IEnumerable) || h is string) return result;
            var bins = new List<double>();
            foreach (object item in (IEnumerable)h)
            {
                double n;
                if (bins.Count >= 4096 || !NumericValue(item, out n) || n < 0 || n > 1e15) return result;
                bins.Add(n);
            }
            double total = bins.Sum();
            if (bins.Count < 2 || total <= 0) return result;
            double sum = 0, weighted = 0; int median = -1;
            for (int i = 0; i < bins.Count; i++)
            {
                sum += bins[i]; weighted += i * bins[i];
                if (median < 0 && sum >= total / 2) median = i;
            }
            if (!result.ContainsKey("medianLuminance")) result["medianLuminance"] = (double)median / (bins.Count - 1);
            if (!result.ContainsKey("meanLuminance")) result["meanLuminance"] = weighted / (total * (bins.Count - 1));
            if (!result.ContainsKey("blackClip")) result["blackClip"] = bins[0] / total;
            if (!result.ContainsKey("whiteClip")) result["whiteClip"] = bins[bins.Count - 1] / total;
            return result;
        }

        private static string[] Clauses(string text)
        {
            var clauses=new List<string>();
            foreach(string piece in Regex.Split(text, @"[，,；;。！？!?\r\n]+|(?:并且|同时|然后|但是|但|并)(?=再|把|将|略|稍|提|降|压|加|减|设|调|保|不|别)"))
            {
                int start=0;
                foreach(Match split in Regex.Matches(piece,@"(?:以及|和|并)(?=\s*(?:曝光|对比|高光|阴影|色温|饱和|清晰|锐化|去雾|降噪|把|将))"))
                {
                    string before=piece.Substring(start,split.Index-start);
                    if(!Regex.IsMatch(before,@"\d|提亮|压暗|降低|提高|增加|减少|调低|调高|设为|一点")) continue;
                    clauses.Add(before.Trim()); start=split.Index+split.Length;
                }
                string rest=piece.Substring(start).Trim();
                if(rest.Length>0) clauses.Add(rest);
            }
            return clauses.ToArray();
        }

        private static List<string> Mentioned(Context c, string text)
        {
            var found = c.Specs.Keys.Where(key => Regex.IsMatch(text, @"(?<![a-z0-9_])" + Regex.Escape(key) + @"(?![a-z0-9_])", Ignore)).ToList();
            if (found.Count > 0) return found;
            if(c.SegmentRequest)
            {
                if(Regex.IsMatch(text,"前景|主体")) found.Add("segment_foreground");
                if(text.Contains("背景")) found.Add("segment_background");
                return found;
            }
            if(c.PaintRequest)
            {
                if(Regex.IsMatch(text,"阴影|暗部")) found.Add("local_shadows");
                if(Regex.IsMatch(text,"高光|亮部")) found.Add("local_highlights");
                if(text.Contains("曝光") || (found.Count==0 && Regex.IsMatch(text,"提亮|压暗|调亮|调暗|明暗|亮一|暗一|更亮|更暗|亮度|亮些|暗些"))) found.Add("local_exposure");
                if(Regex.IsMatch(text,"饱和|鲜艳")) found.Add("local_saturation");
                if(Regex.IsMatch(text,"色温|回暖|冷暖|调暖|调冷|变暖|变冷|偏暖|偏冷|暖一|冷一|更暖|更冷|暖些|冷些|升温|降温")) found.Add("local_temperature");
                if(Regex.IsMatch(text,"柔化|平滑|柔和")) found.Add("local_smooth");
                return found;
            }
            if(Regex.IsMatch(text,"磨皮|皮肤.{0,8}(细腻|光滑)|纹理|毛孔") && c.Specs.ContainsKey("portrait_skin_smooth")) found.Add("portrait_skin_smooth");
            if(Regex.IsMatch(text,"祛红|泛红|去红") && c.Specs.ContainsKey("portrait_skin_red")) found.Add("portrait_skin_red");
            if(Regex.IsMatch(text,"亮肤|(?:肤色|皮肤|脸).{0,6}(?:亮|明亮)|提亮.{0,4}(?:肤色|皮肤|脸)") && c.Specs.ContainsKey("portrait_skin_light")) found.Add("portrait_skin_light");
            if(found.Count==0 && c.SkinRequest && !Regex.IsMatch(text,"曝光|全局|整体") && Regex.IsMatch(text,"亮一|暗一|稍亮|稍暗|压暗|提亮")) found.Add("portrait_skin_light");
            if(found.Count>0) return found;
            for (int i = 0; i < BandKeys.Length; i++)
                if (text.Contains(BandNames[i]))
                {
                    string channel = text.Contains("色相") ? "hue" : text.Contains("饱和") ? "sat" : Regex.IsMatch(text, "明度|亮度|提亮|压暗") ? "lum" : null;
                    if (channel != null) found.Add("hsl_" + BandKeys[i] + "_" + channel);
                }
            if (found.Count > 0) return found;
            foreach (var a in Aliases)
                if (c.Specs.ContainsKey(a.Key) && Regex.IsMatch(text, a.Value, Ignore)) found.Add(a.Key);
            if (found.Contains("vibrance") && text.Contains("自然饱和")) found.Remove("saturation");
            if(found.Contains("noise_chroma")||found.Contains("noise_luma"))found.Remove("noise");
            if (found.Count == 0 && Regex.IsMatch(text, "提亮|变亮|亮一|亮些|亮点|明亮|压暗|调暗|变暗|暗一|暗些|暗点|[档挡]|brightness", Ignore))
                found.Add("exposure");
            return found;
        }

        private static bool Negative(string clause)
        {
            return Regex.IsMatch(clause, @"不要|别动|别改|别碰|不动|不调|不改|不变|保留.{0,8}(曝光|高光|阴影|色温|饱和|对比)|(?:维持|保持)原(?:样|状)");
        }
        private static void ApplyTextLimits(Context c, string[] clauses)
        {
            var only = new HashSet<string>(StringComparer.Ordinal);
            var keep = new HashSet<string>(StringComparer.Ordinal);
            foreach (string clause in clauses)
            {
                var keys = Mentioned(c, clause);
                if (Negative(clause)) keep.UnionWith(keys);
                else if (Regex.IsMatch(clause, "仅|只"))
                {
                    only.UnionWith(keys);
                    if(c.PaintRequest && Regex.IsMatch(clause,"区域|蒙版|选区")) only.UnionWith(c.Allowed);
                    foreach (var spec in c.Specs.Values)
                        if (clause.IndexOf(spec.group, StringComparison.OrdinalIgnoreCase) >= 0 || (clause.ToLowerInvariant().Contains("hsl") && spec.key.StartsWith("hsl_")))
                            only.Add(spec.key);
                    if (only.Count == 0 && Regex.IsMatch(clause, "色彩|颜色"))
                        only.UnionWith(c.Specs.Keys.Where(k => k=="temperature" || k=="tint" || k=="saturation" || k=="vibrance" || k.StartsWith("hsl_") || k.StartsWith("grade_") || k.StartsWith("cal_")));
                    if (only.Count == 0 && Regex.IsMatch(clause, "光影|明暗"))
                        only.UnionWith(new[]{"exposure","contrast","shadows","highlights","whites","blacks"});
                    if(only.Count==0 && Regex.IsMatch(clause,"皮肤|肤色")) only.UnionWith(c.Specs.Keys.Where(k=>k.StartsWith("portrait_skin_")));
                    if (only.Count == 0) throw new InvalidOperationException("请说明只调整哪个参数或模块。");
                }
            }
            if (only.Count > 0) c.Allowed.IntersectWith(only);
            c.Allowed.ExceptWith(keep);
            if (c.Allowed.Count == 0) throw new InvalidOperationException("选定范围中的参数都已锁定或要求保持原样。");
        }

        private static Dictionary<string, object> Local(string text, string[] clauses, Context c)
        {
            var patch = new Dictionary<string, double>(StringComparer.Ordinal);
            string style = Imaging.Presets.Keys.FirstOrDefault(text.Contains);
            if (style == null && Regex.IsMatch(text, "日系清透|日系(风格)?$")) style = "日系空气感";
            if (style == null && Regex.IsMatch(text, "转为黑白|改成黑白|黑白风格")) style = "黑白艺术";
            if (style != null && !clauses.Any(s => s.Contains(style) && Negative(s)))
            {
                double strength = Regex.IsMatch(text, "稍微|淡一点|轻一点|一点点") ? .5 : 1;
                foreach (var p in Imaging.Presets[style])
                    if (c.Allowed.Contains(p.Key)) patch[p.Key] = c.Current[p.Key] + (p.Value - c.Current[p.Key]) * strength;
            }
            bool natural = Regex.IsMatch(text, "自然一|自然点|高级一|高级点|改善光影|自动(优化|调整|修图|调色)|修得自然|修得高级|优化整");
            if (natural && style == null)
            {
                double median, black, white;
                if (c.Analysis.TryGetValue("medianLuminance", out median))
                    AddRelative(c, patch, "exposure", Math.Max(-.75, Math.Min(.75, (.44 - median) * 1.9)));
                else AddRelative(c, patch, "exposure", .12);
                c.Analysis.TryGetValue("whiteClip", out white); c.Analysis.TryGetValue("blackClip", out black);
                AddRelative(c, patch, "highlights", white > .015 ? -24 : -10);
                AddRelative(c, patch, "shadows", black > .015 ? 18 : 8);
                AddRelative(c, patch, "vibrance", 5);
            }
            foreach (string clause in clauses)
            {
                if (Negative(clause) || (style != null && (clause.Contains(style) || clause.Contains("日系") || clause.Contains("黑白")))) continue;
                if(Regex.IsMatch(clause,"保留.{0,4}(毛孔|纹理)")) continue;
                List<string> keys = Mentioned(c, clause);
                if(keys.Count==1 && keys[0]=="exposure" && !c.Allowed.Contains("exposure") && c.Allowed.Contains("portrait_skin_light") && Regex.IsMatch(clause,"亮|暗"))
                    keys[0]="portrait_skin_light";
                bool relativeFollow = Regex.IsMatch(clause, "再|刚才|上一步|继续|刚刚|刚调|撤销|恢复|退回|回退");
                if (keys.Count == 0 && relativeFollow && c.History.Count > 0)
                {
                    var last = c.History.Last();
                    if (last.Count == 1 || Regex.IsMatch(clause, "恢复|撤销|回退|上一步"))
                        keys = last.Select(h => h.Key).ToList();
                }
                if (keys.Count == 0) continue;
                foreach (string key in keys)
                {
                    if (!c.Allowed.Contains(key)) throw new InvalidOperationException("参数 " + c.Specs[key].label + " 已锁定或超出了所选调整范围。");
                    ApplyClause(c, patch, key, clause);
                }
            }
            string label = style ?? (natural ? "自然调色" : "调整" + String.Join("、", patch.Keys.Take(3).Select(k => c.Specs[k].label)));
            if(c.PaintRequest) return Result(c,patch,label,"仅在你已保存的手绘蒙版内调整。");
            if(c.SegmentRequest) return Result(c,patch,label,"按你在画布上标记并保存的前景与背景分区调整曝光。");
            return Result(c, patch, label, natural && c.Analysis.ContainsKey("medianLuminance") ?
                "根据本地亮度直方图和当前参数生成。确认后应用到整张照片。" : "根据当前配方生成。可以先查看参数和画面，再确认应用。");
        }

        private static void AddRelative(Context c, Dictionary<string, double> patch, string key, double delta)
        {
            if (!c.Allowed.Contains(key)) return;
            ControlSpec s = c.Specs[key];
            patch[key] = Math.Round(Math.Max(s.min, Math.Min(s.max, c.Current[key] + delta)), 4);
        }

        private static void ApplyClause(Context c, Dictionary<string, double> patch, string key, string clause)
        {
            ControlSpec s = c.Specs[key];
            double active;
            if (!patch.TryGetValue(key, out active)) active = c.Current[key];
            var previous = c.History.SelectMany(group => group).LastOrDefault(h => h.Key == key);
            if (Regex.IsMatch(clause, "恢复默认|还原默认|归零|重置|清零")) { patch[key] = s.initial; return; }
            if(key=="portrait_skin_smooth" && Regex.IsMatch(clause,"恢复.{0,5}纹理|减少.{0,4}假面|磨皮轻一点"))
            { patch[key]=Math.Max(s.min,active-10); return; }
            if (Regex.IsMatch(clause, "恢复|回退|撤销|还原|退回"))
            {
                if (previous == null || !previous.Before.HasValue || !previous.After.HasValue || previous.After.Value != c.Current[key])
                    throw new InvalidOperationException("最近的编辑记录中没有这个参数的可恢复值。");
                patch[key] = Regex.IsMatch(clause, "一点|一些|部分") ? (active + previous.Before.Value) / 2 : previous.Before.Value;
                return;
            }
            string work = Regex.Replace(clause.Replace("两档半","2.5档").Replace("一档半","1.5档").Replace("半档", "0.5档").Replace("一档", "1档").Replace("两档", "2档").Replace("一挡", "1档")
                .Replace("降噪", "去噪处理").Replace("减噪", "去噪处理"), Regex.Escape(key), "", Ignore);
            work=Regex.Replace(Regex.Replace(work,@"负\s*(?=\d)","-"),@"正\s*(?=\d)","+");
            var amount = Regex.Match(work, Numeric + @"(?:\s*/\s*\d+)?", Ignore);
            double magnitude = 0;
            bool exact = amount.Success;
            if (exact)
            {
                string[] parts = amount.Value.Split('/');
                magnitude = Double.Parse(parts[0], CultureInfo.InvariantCulture);
                if (parts.Length == 2) magnitude /= Double.Parse(parts[1], CultureInfo.InvariantCulture);
                if (!Finite(magnitude)) throw new ArgumentException("指令中的数字无效。");
            }
            bool down = Regex.IsMatch(work, "降低|减少|调低|下调|压低|压暗|变暗|调暗|更暗|暗一|暗些|暗点|减弱|减小|收.{0,2}(高光|一点)|冷一|偏冷|更冷|减|降|压|低点|低一|弱一|淡一|往下|少一|减淡|褪色");
            bool up = Regex.IsMatch(work, "提高|提升|增加|增强|调高|上调|提亮|拉高|调亮|变亮|更亮|亮一|亮点|亮些|暖一|偏暖|更暖|多一|加|提高|明显|强一|高一|往上|清晰一");
            if(key=="portrait_skin_smooth" && Regex.IsMatch(work,"细腻|光滑|自然磨皮")) up=true;
            if(key=="portrait_skin_red" && Regex.IsMatch(work,"祛红|去红|减淡泛红|减少泛红")) { up=true; down=false; }
            if(key=="portrait_skin_light" && Regex.IsMatch(work,"亮肤")) up=true;
            if(key=="local_smooth" && Regex.IsMatch(work,"柔化|平滑|柔和")) up=true;
            if((key=="noise_chroma"||key=="noise_luma")&&Regex.IsMatch(clause,"(?:减少|去除|压低|减轻|消除).*(?:噪点|彩噪|灰色颗粒)")){up=true;down=false;}
            if(key=="local_temperature"&&Regex.IsMatch(work,"回暖|调暖|变暖|暖些|升温"))up=true;
            if(key=="local_temperature"&&Regex.IsMatch(work,"调冷|变冷|冷些|降温"))down=true;
            if (Regex.IsMatch(work, "^(?:请)?(?:再|继续)(?:多|来|强|明显)(?:一|些|点)") && previous != null && previous.Before.HasValue && previous.After.HasValue)
            { down = previous.After.Value < previous.Before.Value; up = !down; }
            bool absolute = Regex.IsMatch(work, "设为|设到|调至|调到|改成|改为|改到|设置为|指定为|[=:]") || (exact && !down && !up && !work.Contains("再"));
            if (absolute && !exact) throw new ArgumentException("设置绝对值时请给出数值。");
            if (!absolute && !up && !down && !exact)
            {
                if (Regex.IsMatch(work, "再|继续") && previous != null && previous.Before.HasValue && previous.After.HasValue)
                { down = previous.After.Value < previous.Before.Value; up = !down; }
                else if (Regex.IsMatch(work, "只改|只调|仅改|仅调|只动|保持|保留")) return;
                else throw new ArgumentException("请说明 " + s.label + " 要增加、降低或设为什么数值。");
            }
            if (!exact) magnitude = (key == "exposure" || key=="local_exposure" || key.StartsWith("segment_",StringComparison.Ordinal) ? .3 : 10) *
                (Regex.IsMatch(work, "稍微|一点点|轻微|略微") ? .5 : 1);
            double next = absolute ? magnitude : active + (down ? -Math.Abs(magnitude) : magnitude);
            if (exact && (next < s.min || next > s.max)) throw new ArgumentException(s.label + " 超出允许范围，请缩小调整量。");
            patch[key] = Math.Round(Math.Max(s.min, Math.Min(s.max, next)), 6);
        }

        private static Dictionary<string, object> Result(Context c, Dictionary<string, double> patch, string label, string message)
        {
            foreach (var p in patch)
            {
                ControlSpec s;
                if (!c.Specs.TryGetValue(p.Key, out s) || !c.Allowed.Contains(p.Key) || !Finite(p.Value) || p.Value < s.min || p.Value > s.max)
                    throw new InvalidOperationException("提案包含未知、锁定或越界的参数，整组调整已退回。");
            }
            var changes = patch.Where(p => c.Current[p.Key] != p.Value).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
            if (changes.Count == 0) throw new InvalidOperationException("没有可应用的参数变化。可以说“曝光增加半档”“高光设为 -20”或指定一个预设。");
            return new Dictionary<string, object> { { "values", changes }, { "label", label }, { "message", message } };
        }
    }
}
