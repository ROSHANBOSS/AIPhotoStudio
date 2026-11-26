using System;
using System.Collections.Generic;
using System.Linq;

namespace AIPhotoStudio
{
    public sealed partial class StudioWorkspace
    {
        private const string DraftPrefix="editing-draft/1/";
        private readonly Dictionary<string,string> draftVersions=new Dictionary<string,string>();
        private static string DraftKey(string id,string kind)
        {
            if(kind!="crop"&&kind!="privacy"&&kind!="brush-structure"&&
                !(kind.Length==7&&kind.StartsWith("brush-",StringComparison.Ordinal)&&kind[6]>='0'&&kind[6]<='7'))
                throw new ArgumentException("未知草稿类型。");
            return DraftPrefix+id+"/"+kind;
        }
        internal string[] DraftIndex()
        {
            lock(gate)return db.Rows("SELECT name FROM settings WHERE name GLOB ?",DraftPrefix+"*")
                .Select(row=>((string)row[0]).Substring(DraftPrefix.Length)).ToArray();
        }
        internal object EditDraft(string id,string kind,string content,bool write)
        {
            lock(gate)return db.Transaction<object>(write,delegate{
                RequirePhoto(id);
                string key=DraftKey(id,kind);
                if(!write){
                    string stored=(string)db.Scalar("SELECT value FROM settings WHERE name=?",key);
                    if(stored==null){draftVersions.Remove(key);return null;}
                    draftVersions[key]=stored.Substring(0,32);
                    return stored.Substring(32);
                }
                string current=(string)db.Scalar("SELECT substr(value,1,32) FROM settings WHERE name=?",key),expected;
                draftVersions.TryGetValue(key,out expected);
                if(current!=expected)throw new InvalidOperationException("另一窗口已改变这个草稿。请重开照片工作区，核对较新的草稿。");
                if(String.IsNullOrEmpty(content)){db.Run("DELETE FROM settings WHERE name=?",key);draftVersions.Remove(key);}
                else{
                    string version=NewId();db.Run("INSERT OR REPLACE INTO settings(name,value) VALUES(?,?)",key,version+content);
                    draftVersions[key]=version;
                }
                return true;
            });
        }
        internal void EnsureNoPrivacyDraft(string id)
        {
            lock(gate){
                if(db.Scalar("SELECT 1 FROM settings WHERE name=?",DraftKey(id,"privacy"))!=null)
                    throw new InvalidOperationException("先处理隐私遮挡。请返回对应照片，应用或放弃草稿后再分享。");
            }
        }
        internal void EnsureNoPrivacyDrafts(IEnumerable<string> ids)
        {
            lock(gate){
                var pending=new HashSet<string>(db.Rows("SELECT name FROM settings WHERE name GLOB ?",DraftPrefix+"*/privacy")
                    .Select(row=>(string)row[0]),StringComparer.Ordinal);
                if(ids.Any(id=>pending.Contains(DraftKey(id,"privacy"))))
                    throw new InvalidOperationException("先处理隐私遮挡。请返回对应照片，应用或放弃草稿后再分享。");
            }
        }
    }
}
