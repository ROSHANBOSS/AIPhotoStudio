using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AIPhotoStudio
{
    internal sealed partial class StudioBridge
    {
        private readonly GalleryPreviewCache galleryCache;
        private async Task<object> LibraryThumbnail(Dictionary<string,object> args)
        {
            string id=TextOf(args,"id");long revision=Convert.ToInt64(args["revision"]);
            string path=work.PathFor(id);
            var file=new FileInfo(path);
            string key=Program.Version+"/"+path+"/"+file.Length+"/"+file.LastWriteTimeUtc.Ticks+"/"+revision;
            int generation;
            string image=galleryCache.Read(id,key,out generation);
            if(image==null)
            {
                var values=work.LibraryValues(id,revision);
                var result=(Dictionary<string,object>)await RenderPreview(path,values,144,false,null,null,true).ConfigureAwait(false);
                image=(string)result["image"];work.VerifyLibraryPhoto(id,revision);
                galleryCache.Save(id,key,image,generation);
            }
            work.VerifyLibraryPhoto(id,revision);
            return new{image};
        }
        private static long FolderBytes(string path)
        {
            if(!Directory.Exists(path)||(File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)return 0;
            long bytes=0;
            foreach(var entry in new DirectoryInfo(path).EnumerateFileSystemInfos())
            {
                if((entry.Attributes&FileAttributes.ReparsePoint)!=0)continue;
                try{bytes+=entry is DirectoryInfo?FolderBytes(entry.FullName):((FileInfo)entry).Length;}
                catch(FileNotFoundException){}catch(DirectoryNotFoundException){}
            }
            return bytes;
        }
        private async Task<object> LibraryAction(string method,Dictionary<string,object> args)
        {
            if(method=="libraryThumbnail")return await Task.Run(()=>LibraryThumbnail(args),closing.Token);
            if(method=="libraryPeople"){object legacy;work.SavePeople(TextOf(args,"id"),TextOf(args,"preference"),args.TryGetValue("onlyIfMissing",out legacy)&&Equals(legacy,true));return true;}
            if(method=="libraryDisk"||method=="libraryClearPreviews")return await Task.Run<object>(delegate{
                long released=method=="libraryClearPreviews"?galleryCache.Clear():0;
                long backups=FolderBytes(Path.Combine(channelDirectory,"Projects","Backups"));
                long edits=FolderBytes(Path.Combine(channelDirectory,"Projects"))-backups+FolderBytes(Path.Combine(channelDirectory,"Presets"));
                long[] originals=work.OriginalSizes();
                return new{edits,backups,previews=galleryCache.Measure(),browser=FolderBytes(Path.Combine(channelDirectory,"Cache","WebView2")),
                    originals=originals[0],unknown=originals[1],released,last=work.LastRestoredRoot(),root=channelDirectory};
            });
            if(method=="libraryBackup")
            {
                using(var pick=new SaveFileDialog{Title=L("保存整库备份","Save library backup"),Filter=L("AIPhotoStudio 图库包|*.aiplibrary","AIPhotoStudio library|*.aiplibrary"),DefaultExt="aiplibrary",FileName=L("AIPhotoStudio-图库-","AIPhotoStudio-Library-")+DateTime.Now.ToString("yyyyMMdd"),AddExtension=true,OverwritePrompt=false})
                {
                    if(pick.ShowDialog(owner)!=DialogResult.OK)return null;
                    await StartOriginalIndex(true);
                    return await Task.Run(()=>work.ExportLibrary(pick.FileName,closing.Token));
                }
            }
            if(method=="libraryRestore")
            {
                using(var open=new OpenFileDialog{Title=L("选择整库备份","Choose a library backup"),Filter=L("AIPhotoStudio 图库包|*.aiplibrary","AIPhotoStudio library|*.aiplibrary"),CheckFileExists=true})
                using(var target=new SaveFileDialog{Title=L("为恢复的图库选择新目录","Choose a new folder for the restored library"),Filter=L("新图库目录|*.*","New library folder|*.*"),FileName=L("AIPhotoStudio-恢复-","AIPhotoStudio-Restored-")+DateTime.Now.ToString("yyyyMMdd-HHmmss"),AddExtension=false,OverwritePrompt=false})
                {
                    if(open.ShowDialog(owner)!=DialogResult.OK||target.ShowDialog(owner)!=DialogResult.OK)return null;
                    return await Task.Run(()=>work.RestoreLibrary(open.FileName,target.FileName,closing.Token));
                }
            }
            string root=work.LastRestoredRoot();
            if(String.IsNullOrEmpty(root)||!Directory.Exists(root)||!File.Exists(Path.Combine(root,Program.Channel,"Projects","studio.sqlite3")))
                throw new IOException("上次恢复的图库目录不存在。");
            Process.Start(new ProcessStartInfo{FileName=Application.ExecutablePath,Arguments="--data-root \""+root+"\"",UseShellExecute=false});
            return true;
        }
    }
    public sealed partial class StudioWorkspace
    {
        internal Dictionary<string,double> LibraryValues(string id,long revision)
        {
            lock(gate){var state=Current(id);CheckRevision(state,revision);CheckEngine(state.Engine);return state.Values;}
        }
        internal void VerifyLibraryPhoto(string id,long revision)
        {
            lock(gate)if(!Equals(db.Scalar("SELECT p.revision FROM photos p WHERE p.id=? AND "+VisiblePhotos,id),revision))
                throw new InvalidOperationException("图库照片已更改，请重新打开。");
        }
        internal long[] OriginalSizes()
        {
            lock(gate)
            {
                long total=0,unknown=0;var paths=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach(var r in db.Rows("SELECT p.path,p.metadata,s.bytes FROM photos p LEFT JOIN photo_sources s ON s.photo_id=p.id WHERE "+VisiblePhotos))
                {
                    if(!paths.Add((string)r[0]))continue;
                    object raw;long size;
                    if(r[2]!=null)total+=(long)r[2];
                    else if(json.Deserialize<Dictionary<string,object>>((string)r[1]).TryGetValue("fileBytes",out raw)&&Int64.TryParse(Convert.ToString(raw),out size)&&size>=0)total+=size;
                    else unknown++;
                }
                return new[]{total,unknown};
            }
        }
        internal string PeopleSetting(string id){lock(gate)return (string)db.Scalar("SELECT value FROM settings WHERE name=?","people/v1/"+id);}
        internal void SavePeople(string id,string preference,bool onlyIfMissing)
        {
            Guid guid;
            if(!Guid.TryParseExact(id,"N",out guid)||preference.Length>16384)throw new ArgumentException("无效的人物偏好。");
            var record=StudioBridge.Json().Deserialize<Dictionary<string,object>>(preference);
            Fields(record,"active","hidden");
            Action<object> validateIdentity=raw=>{
                var item=Map(raw);Fields(item,"body","face");if(item.Count==0)throw new ArgumentException("缺少人物位置。");
                foreach(var part in item.Values){var evidence=Map(part);Fields(evidence,"index","box");
                    long index=IntegerArg(evidence,"index");if(index<0||index>64)throw new ArgumentException("人物编号超出范围。");
                    var box=Map(Arg(evidence,"box"));Fields(box,"x","y","w","h");
                    foreach(string k in new[]{"x","y","w","h"}){double n=Number(Arg(box,k));if(n<0||n>1.001)throw new ArgumentException("人物位置超出范围。");}
                }
            };
            object active;if(record.TryGetValue("active",out active)&&active!=null)validateIdentity(active);
            foreach(object item in Sequence(Arg(record,"hidden"),48))validateIdentity(item);
            lock(gate)
            {
                if(db.Scalar("SELECT 1 FROM photos WHERE id=?",id)==null)return;
                string sql=onlyIfMissing?"INSERT OR IGNORE INTO settings(name,value) VALUES(?,?)":
                    "INSERT INTO settings(name,value) VALUES(?,?) ON CONFLICT(name) DO UPDATE SET value=excluded.value WHERE value<>excluded.value";
                db.Run(sql,"people/v1/"+id,preference);
            }
        }
    }
}
