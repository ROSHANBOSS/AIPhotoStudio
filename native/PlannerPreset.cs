using System;
using System.Text.RegularExpressions;

namespace AIPhotoStudio
{
    public static partial class Planner
    {
        private const string RecipeSubject = @"(?:(?:这张(?:照片|图)(?:当前|现在)?的?|当前的?|目前的?|现在的?|这套|这一套|这组|这份|这些|刚才的?|已有的?|现有的?)\s*(?:编辑参数|编辑配方|修图参数|修图配方|调色配方|调色参数|调色设置|调整方案|编辑设置|调整参数|调整|配方|参数|效果|调色|设置))";
        private const string SaveStem = @"(?:(?:(?:把|将)\s*)?(?:"+RecipeSubject+@")?\s*(?:保存|另存|存)\s*(?:为|成|作|作为|到)|(?:保存|另存|存)\s*"+RecipeSubject+@"\s*(?:为|成|作|作为|到))\s*(?:一(?:个|套|份)\s*)?";
        private const string PresetNoun = @"(?:我的|个人|用户|自定义)?预设(?:库)?";
        private const string NameMarker = @"(?:命名(?:为|成)?|取名(?:为|叫)?|起名(?:为|叫)?|名(?:为|叫)|叫作|叫做|叫|名字(?:为|是|叫)|名称(?:为|是|叫))";
        private const string Quoted = @"(?:“(?<quote>[^”\r\n]{1,100})”|‘(?<quote>[^’\r\n]{1,100})’|「(?<quote>[^」\r\n]{1,100})」|『(?<quote>[^』\r\n]{1,100})』|《(?<quote>[^》\r\n]{1,100})》|""(?<quote>[^""\r\n]{1,100})""|'(?<quote>[^'\r\n]{1,100})')";
        private const string Plain = @"(?<plain>[\p{L}\p{N}][^，,。！？?!；;\r\n“”「」『』《》""]{0,99}?)";
        private const string Candidate = @"(?:"+Quoted+"|"+Plain+")";
        private static readonly Regex SavePresetIntent = new Regex(
            @"^(?:(?:请你|麻烦你|请|帮我|替我|麻烦)\s*)*"+SaveStem+
            @"(?:"+PresetNoun+@"(?:\s*(?:[，,：:]\s*)?"+NameMarker+@"\s*"+Candidate+@"|\s*"+Quoted+@"|\s*[:：]\s*"+Candidate+@"|\s+"+Plain+@")?"+
            @"|(?:\s*"+NameMarker+@"\s*"+Candidate+@"(?:的)?|\s*"+Quoted+@"(?:的)?)\s*"+PresetNoun+@")\s*$",
            RegexOptions.CultureInvariant);

        // Recognizing this intent only proposes a library action. A separate native
        // transaction writes the name confirmed by the user for the same photo.
        public static bool TryPresetName(string text,out string suggestedName)
        {
            suggestedName=null;
            if(String.IsNullOrWhiteSpace(text) || text.Length>500) return false;
            string request=text.Trim().TrimEnd('。','！','!').TrimEnd();
            Match intent=SavePresetIntent.Match(request);
            if(!intent.Success) return false;
            bool quoted=intent.Groups["quote"].Success;
            string name=(quoted ? intent.Groups["quote"].Value : intent.Groups["plain"].Value).Trim();
            if(!quoted && name.Length>0 && Regex.IsMatch(name,@"然后|并且|同时|再(?:把|将|应用|导出|覆盖|删除|保存)|并(?:把|将|应用|导出|覆盖|删除|保存)|不要|别再")) return false;
            if(name.Length>0) suggestedName=name;
            return true;
        }
    }
}
