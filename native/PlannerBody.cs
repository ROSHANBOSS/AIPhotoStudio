using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace AIPhotoStudio
{
    public static partial class Planner
    {
        private static readonly Dictionary<string,string> BodyWords = new Dictionary<string,string> {
            {"body_shoulders","肩宽|收肩|宽肩|肩膀|肩部|窄肩"}, {"body_arms","手臂|胳膊|瘦臂"},
            {"body_waist","瘦腰|收腰|腰围|腰部|腰身"}, {"body_abdomen","腹部|收腹|小腹|肚子"},
            {"body_hips","臀部|收臀|臀围|臀型"}, {"body_thighs","大腿"}, {"body_calves","小腿"},
            {"body_legshape","腿型|腿形"}, {"body_leglength","腿长|长腿|拉长.{0,3}腿|腿.{0,2}拉长"},
            {"body_proportion","腰线|身材比例|身体比例|上下身比例"}
        };

        private static bool TryBodyPlan(string text,string scope,Dictionary<string,double> values,string[] locked,
            Dictionary<string,object> analysis,string[] history,out Dictionary<string,object> proposal)
        {
            proposal=null;
            object selected;
            int index=analysis!=null && analysis.TryGetValue("bodyPersonIndex",out selected)
                ? Convert.ToInt32(selected,CultureInfo.InvariantCulture) : 0;
            if(Regex.IsMatch(text,@"^(?:请)?(?:再多一点|再少一点|再强一点|再来一点|撤销刚才的调整|恢复上一步)[。！]?$"))
            {
                Match step=HistoryPair.Match(history==null?"":history.LastOrDefault()??"");
                string key=step.Groups["key"].Value;
                string baseKey=BodyEdits.BaseKey(key);
                if(step.Success && baseKey!=null && BodyWords.ContainsKey(baseKey) && values!=null && values.ContainsKey(key))
                {
                    if(BodyEdits.PersonIndex(key)!=index)throw new InvalidOperationException("上一步调整的是另一位人物，请选中那个人，或明确这次要调的部位。");
                    double before,after;
                    if(!TryDouble(step.Groups["a"].Value,out before) || !TryDouble(step.Groups["b"].Value,out after))return false;
                    double change=after-before;
                    double next=Regex.IsMatch(text,"恢复|撤销")?before:values[key]+(text.Contains("少")?-change:change);
                    text=BodyWords[baseKey].Split('|')[0]+"设为"+next.ToString(CultureInfo.InvariantCulture);
                }
            }
            if(!BodyWords.Values.Any(pattern=>Regex.IsMatch(text,pattern))) return false;
            if(values==null || !BodyAssets.Supported(values) || analysis==null || !analysis.ContainsKey("hasBody") || !Object.Equals(analysis["hasBody"],true))
                throw new InvalidOperationException("请打开「人体美型」，先识别照片中的人物。");
            if(index<0)throw new InvalidOperationException("请先点选要调整的人物，再说明部位和力度。");
            const string personPattern=@"(?:人物\s*(?<n>[0-9零一二两三四五六七八九十]+)|第\s*(?<n>[0-9零一二两三四五六七八九十]+)\s*(?:位(?:人物?)?|个人物?|人))";
            foreach(Match subject in Regex.Matches(text,personPattern))
            {
                if(SubjectNumber(subject.Groups["n"].Value)!=index+1)throw new InvalidOperationException("指令与选中的人物不同，请先选择对应的人物编号。");
            }
            text=Regex.Replace(text,personPattern,"");
            if(!String.IsNullOrEmpty(scope) && scope!="all" && scope!="人体美型") throw new InvalidOperationException("人体调整超出了当前选择的模块。");
            var changes=new Dictionary<string,double>(StringComparer.Ordinal);
            var protectedKeys=new HashSet<string>(locked??new string[0],StringComparer.Ordinal);
            foreach(string clause in Regex.Split(text,@"[，,。；;！!?？\r\n]+|同时|并且|然后"))
            {
                if(String.IsNullOrWhiteSpace(clause))continue;
                var keys=BodyWords.Where(p=>Regex.IsMatch(clause,p.Value)).Select(p=>BodyEdits.KeyFor(index,p.Key)).ToArray();
                if(Negative(clause) || Regex.IsMatch(clause,"不要|保持|不变|别动|保留"))
                { foreach(string key in keys) protectedKeys.Add(key); continue; }
                if(keys.Length==0 || Regex.IsMatch(clause,"曝光|对比|高光|亮度|阴影|色温|饱和|肤色|人脸|五官|背景|锐化|磨皮|删除|导出"))
                    throw new ArgumentException("请分开提交人体美型与其他编辑指令。");
                if(Regex.Matches(clause,Numeric).Count>1 || (keys.Length>1 && Regex.IsMatch(clause,"和|并|、|且") && Regex.IsMatch(clause,"增|减|收|拉|细|粗|宽|窄")))
                    throw new ArgumentException("请用逗号分开说明每个部位的方向与力度。");
                bool reset=Regex.IsMatch(clause,"还原|复原|重置|恢复原");
                bool reverse=Regex.IsMatch(clause,"加宽|放宽|增宽|变宽|宽一|变粗|增粗|粗一|放大|缩短|降低|减弱|减少|下调|调低");
                bool stronger=Regex.IsMatch(clause,"收|瘦|拉长|变细|细一|缩小|窄一|提高|上调|增加|提升|调高|修直|拉直|纤细|变窄");
                Match number=Regex.Match(clause,Numeric);
                bool absolute=Regex.IsMatch(clause,"设为|设到|设置为|调到|调至|改为|改成|[=:]");
                if(absolute&&!number.Success)throw new ArgumentException("请给出此人体参数的目标数值。");
                if(!reset && !reverse && !stronger && !number.Success)throw new ArgumentException("请说明调整方向，例如「稍微收腰」或「腿型设为 15」。");
                double amount=number.Success?Double.Parse(number.Value,CultureInfo.InvariantCulture):(Regex.IsMatch(clause,"轻微|略微|稍|一点点")?5:10);
                foreach(string key in keys)
                {
                    if(!BodyAssets.CanAdjust(values,key))throw new InvalidOperationException("所选人物的这个部位没有可靠入镜，请选择面板中可见的部位。");
                    double current=values[key];
                    double next=reset?0:absolute || (number.Success&&!stronger&&!reverse)?amount:current+(reverse?-Math.Abs(amount):amount);
                    if(!Finite(next)||next < -100 || next > 100 || next!=Math.Truncate(next))throw new ArgumentException("人体美型参数需要 -100 至 100 范围内的整数。");
                    if(next!=current)changes[key]=next;
                }
            }
            if(changes.Keys.Any(protectedKeys.Contains))throw new InvalidOperationException("提案涉及锁定或要求保持的部位。");
            if(changes.Count==0)throw new InvalidOperationException("此次没有需要改变的人体参数。");
            proposal=new Dictionary<string,object>{{"values",changes},{"label","人物 "+(index+1)+" · 人体美型"},{"message","已按所选人物的姿态与轮廓预览，确认后写入编辑历史。"}};
            return true;
        }
    }
}
