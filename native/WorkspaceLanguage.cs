using System;
using System.Collections.Generic;

namespace AIPhotoStudio
{
    internal sealed class LanguagePreference
    {
        public string language {get;set;}
        public bool askOnStartup {get;set;}
    }

    public sealed partial class StudioWorkspace
    {
        private const string LanguageKey="interface/language/v1";
        internal LanguagePreference ReadLanguage()
        {
            lock(gate){
                var value=(string)db.Scalar("SELECT value FROM settings WHERE name=?",LanguageKey);
                if(value==null||value.Length>512)return new LanguagePreference();
                try{
                    var pref=json.Deserialize<LanguagePreference>(value);
                    if(pref!=null&&(pref.language=="zh-CN"||pref.language=="en"))return pref;
                }catch(ArgumentException){}catch(InvalidOperationException){}catch(FormatException){}
                return new LanguagePreference();
            }
        }
        internal LanguagePreference SaveLanguage(Dictionary<string,object> args)
        {
            object ask;
            string language=StudioBridge.TextOf(args,"language");
            if((language!="en"&&language!="zh-CN")||!args.TryGetValue("askOnStartup",out ask)||!(ask is bool))
                throw new ArgumentException("请选择简体中文或英语，并指定是否在启动时询问。");
            var pref=new LanguagePreference{language=language,askOnStartup=(bool)ask};
            lock(gate)db.Run("INSERT OR REPLACE INTO settings(name,value) VALUES(?,?)",LanguageKey,json.Serialize(pref));
            return pref;
        }
    }
    internal sealed partial class StudioBridge
    {
        private volatile bool english;
        private string L(string zh,string en){return english?en:zh;}
        private string LocalMessage(string message,string method=null){return english?NativeMessages.Translate(message,method):message;}

        private object SetLanguagePreference(Dictionary<string,object> args)
        {
            return ApplyLanguagePreference(work.SaveLanguage(args));
        }
        private LanguagePreference ApplyLanguagePreference(LanguagePreference pref)
        {
            english=pref.language=="en";
            var window=owner as StudioWindow;
            if(window!=null)window.ApplyLanguage(english);
            return pref;
        }
    }
}
