using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace AIPhotoStudio
{
    public static partial class Planner
    {
        private static readonly string[] PortraitWords={
            "亮肤|(?:皮肤|肤色|脸部).{0,4}(?:提亮|亮一|明亮)|提亮.{0,4}(?:肤色|皮肤|脸部)",
            "磨皮|皮肤.{0,6}(?:细腻|光滑|有点假)|恢复.{0,4}纹理","泛红|祛红|去红","均匀.{0,4}肤色|肤色.{0,4}均匀",
            "黑眼圈|眼下暗沉","眼睛.{0,4}(?:提亮|明亮|亮一)|提亮.{0,3}眼睛","眼睛.{0,4}(?:清晰|清楚|锐化)","眼白","牙齿|美白牙",
            "瘦脸|脸部宽度|脸.{0,2}瘦","下颌|下颚","下巴.{0,3}(?:长|短)","下巴.{0,3}(?:宽|窄|尖)","额头",
            "眼睛.{0,4}(?:大|小)|放大.{0,3}眼睛|大眼","眼距","鼻梁","鼻翼","嘴唇|唇厚"
        };
        private static readonly string[] ChineseSubjectNumbers={"一","二","三","四","五","六","七","八","九","十","十一","十二","十三","十四","十五","十六"};
        internal static int SubjectNumber(string text)
        {
            int number;
            return Int32.TryParse(text,NumberStyles.None,CultureInfo.InvariantCulture,out number)?number:
                Array.IndexOf(ChineseSubjectNumbers,text.Replace("两","二"))+1;
        }
        private static bool TryPortraitPlan(string text,string scope,Dictionary<string,double> values,string[] locked,
            Dictionary<string,object> analysis,string[] history,out Dictionary<string,object> proposal)
        {
            proposal=null;
            if(PortraitAssets.Hash(values)==null)return false;
            if(Regex.IsMatch(text,"[去祛]痘|[去祛]斑|痘印|皮肤小点|小瑕疵"))
                throw new InvalidOperationException("请在「人像」中点「查找小瑕疵」，勾选想修复的位置后预览。");
            text=Regex.Replace(text,"(?:脸|下巴)(?:不要|别)(?:那么|太)尖","下巴宽一点");
            var people=PortraitAssets.Describe(values);
            object raw;
            int index=analysis!=null&&analysis.TryGetValue("faceIndex",out raw)?Convert.ToInt32(raw):people.Length==1?0:-1;
            int numberShown=analysis!=null&&analysis.TryGetValue("subjectNumber",out raw)?Convert.ToInt32(raw):index+1;
            bool follow=Regex.IsMatch(text,@"^(?:请)?(?:再多一点|再强一点|再来一点|再少一点|撤销刚才的调整|恢复上一步)[。！]?$");
            Match last=HistoryPair.Match(history==null?"":history.LastOrDefault()??"");
            var lastKey=last.Groups["key"].Value;
            if(follow&&PortraitControls.Index(lastKey)>=0){
                if(PortraitControls.Index(lastKey)!=index)throw new InvalidOperationException("上一步调整了另一张脸，请先选中对应的人脸。");
                double before,after;
                if(!TryDouble(last.Groups["a"].Value,out before)||!TryDouble(last.Groups["b"].Value,out after))return false;
                int keyIndex=Array.IndexOf(PortraitControls.Keys,PortraitControls.Suffix(lastKey));
                double delta=after-before;
                text=PortraitControls.Labels[keyIndex]+"设为"+(Regex.IsMatch(text,"撤销|恢复")?before:values[lastKey]+(text.Contains("少")?-delta:delta)).ToString(CultureInfo.InvariantCulture);
            }
            if(!PortraitWords.Any(pattern=>Regex.IsMatch(text,pattern)))return false;
            if(index<0||index>=people.Length)throw new InvalidOperationException("请先在「人像」面板中点选要精修的人脸。");
            if(!String.IsNullOrEmpty(scope)&&scope!="all"&&scope!="人像")throw new InvalidOperationException("这条人脸调整超出了选定模块。");
            const string numeral=@"([0-9]+|[零一二两三四五六七八九十百]+)";
            const string numberPattern=@"(?:人脸|人物)\s*"+numeral+@"|第\s*"+numeral+@"\s*(?:位|张|个)(?:人脸|脸|人物|人)";
            foreach(Match subject in Regex.Matches(text,numberPattern)){
                string n=subject.Groups[1].Success?subject.Groups[1].Value:subject.Groups[2].Value;
                int person=SubjectNumber(n);
                if(person!=numberShown)throw new InvalidOperationException("指令的人脸编号和当前选择不同，请先点选对应的人脸。");
            }
            text=Regex.Replace(text,numberPattern,"");
            if(Regex.IsMatch(text,"所有|全部|每个人|每张脸|背景|衣服|头发|整体曝光|蒙版|祛痘|祛斑"))
                throw new InvalidOperationException("请单独说明所选人脸的肤质或五官调整。");
            var patch=new Dictionary<string,double>();
            var protectedKeys=new HashSet<string>(locked??new string[0]);
            foreach(string rawClause in Regex.Split(text,@"[，,。；;！!?？\r\n]+|同时|并且|然后")){
                string clause=Regex.Replace(rawClause,@"(?:但|并)?(?:请)?保留(?:皮肤)?(?:纹理|毛孔)|(?:不要|别)改变(?:原来|原有)?的?五官(?:特征)?","").Trim();
                if(clause.Length==0)continue;
                int[] matches=Enumerable.Range(0,PortraitWords.Length).Where(k=>Regex.IsMatch(clause,PortraitWords[k])||clause.Contains(PortraitControls.Labels[k])).ToArray();
                if(matches.Length!=1)throw new ArgumentException("请为每个部位单独写出方向或数值，可用逗号分开。");
                int number=matches[0];string key=PortraitControls.Key(index,PortraitControls.Keys[number]);
                if(Regex.IsMatch(clause,"不要|别动|保持|不变|保留")){protectedKeys.Add(key);continue;}
                if(!PortraitAssets.CanAdjust(values,key))throw new InvalidOperationException("这张脸的相应部位无法可靠定位。");
                if(Regex.IsMatch(clause,"(?:恢复|撤销|还原)(?:一下)?(?:刚才|上次|之前|上一)")){
                    var previous=(history??new string[0]).Reverse().SelectMany(row=>row.Split(';')).Select(item=>HistoryPair.Match(item))
                        .FirstOrDefault(pair=>pair.Success&&pair.Groups["key"].Value==key);
                    double before,after;
                    if(previous==null||!TryDouble(previous.Groups["a"].Value,out before)||!TryDouble(previous.Groups["b"].Value,out after)||after!=values[key])
                        throw new InvalidOperationException("没有找到这张脸该参数的对应前值。");
                    if(before!=values[key])patch[key]=before;
                    continue;
                }
                if(Regex.Matches(clause,Numeric).Count>1)throw new ArgumentException("每个部位请只指定一个数值。");
                Match numeric=Regex.Match(clause,Numeric);
                bool absolute=Regex.IsMatch(clause,"设为|设到|设置为|调到|调至|改为|[=:]");
                bool reset=Regex.IsMatch(clause,"还原|重置|恢复原");
                bool decrease=Regex.IsMatch(clause,"减弱|减少|降低|调低|变短|缩短|短一|小一|缩小|变窄|窄一|收窄|收小|变薄|薄一|有点假|恢复.{0,4}纹理");
                if(number==9||number==10)decrease=Regex.IsMatch(clause,"放宽|加宽|宽一|减少|减弱");
                bool directed=decrease||Regex.IsMatch(clause,"增|提高|拉长|长一|加宽|宽一|加厚|厚一|变大|大一|放大|瘦|收|亮|细腻|光滑|清晰|美白|均匀|淡化|减淡|祛红|去红|磨皮");
                if(absolute&&!numeric.Success || (!directed&&!numeric.Success&&!reset))throw new ArgumentException("请说明目标，例如「眼距设为 12」或「下巴缩短一点」。");
                double amount=numeric.Success?Double.Parse(numeric.Value,CultureInfo.InvariantCulture):Regex.IsMatch(clause,"稍|轻微|一点")?6:12;
                double next=reset?0:absolute?amount:values[key]+(decrease?-Math.Abs(amount):amount);
                if(next!=Math.Truncate(next)||next<(number<9?0:-100)||next>100)throw new ArgumentException("这项人脸调整超出了滑块范围。");
                if(next!=values[key])patch[key]=next;
            }
            if(patch.Keys.Any(protectedKeys.Contains))throw new InvalidOperationException("所选人脸的这项调整已锁定或要求保留。");
            if(patch.Count==0)throw new InvalidOperationException("此次没有需要改变的人脸参数。");
            proposal=new Dictionary<string,object>{{"values",patch},{"label","人脸 "+numberShown+" · 人像精修"},{"message","已按所选人脸生成预览，确认后写入历史。"}};
            return true;
        }
    }
}
