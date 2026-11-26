using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;

namespace AIPhotoStudio
{
    internal sealed class DeliveryHeader
    {
        public string id, root, rootIdentity, created, engine;
        public DeliveryPreset preset;
        public int total;
    }
    internal sealed class DeliveryFile
    {
        public string name, hash, temporary, identity, error;
        public bool done;
    }
    internal sealed class DeliveryPhoto
    {
        public string id, original, source;
        public int index;
        public long? editingRevision;
        public Dictionary<string,double> values;
        public DeliveryFile proof=new DeliveryFile(), final=new DeliveryFile();
    }

    public sealed partial class StudioWorkspace
    {
        private const string DeliveryPresetsKey="delivery/v1/preset/",DeliverySelectedKey="delivery/v1/selected",DeliveryJobsKey="delivery/v1/job/";
        private const string DeliveryLogosKey="delivery/v1/logo/";
        private static string DeliveryRowsKey(string job){return "delivery/v1/rows/"+DeliveryPreset.Key(job)+"/";}

        private T DeliveryRecord<T>(string key)
        {
            string raw=(string)db.Scalar("SELECT value FROM settings WHERE name=?",key);
            if(raw==null)throw new KeyNotFoundException("未找到交付记录，可能已在其他窗口完成。请刷新后重试。");
            return json.Deserialize<T>(raw);
        }

        private List<T> DeliveryRecords<T>(string prefix)
        {
            return db.Rows("SELECT value FROM settings WHERE name GLOB ? ORDER BY name",prefix+"*").Select(row=>json.Deserialize<T>((string)row[0])).ToList();
        }
        private void PutDelivery(string key,object value){db.Run("INSERT OR REPLACE INTO settings(name,value) VALUES(?,?)",key,json.Serialize(value));}

        private object ReadDeliveryShelf()
        {
            var presets=DeliveryRecords<DeliveryPreset>(DeliveryPresetsKey).OrderBy(p=>p.name,StringComparer.CurrentCultureIgnoreCase).ToArray();
            var pending=DeliveryRecords<DeliveryHeader>(DeliveryJobsKey).OrderByDescending(j=>j.created).Select(j=>new{
                id=j.id,root=j.root,name=j.preset.name,total=j.total,created=j.created}).ToArray();
            return new{presets=presets,selected=db.Scalar("SELECT value FROM settings WHERE name=?",DeliverySelectedKey),
                pending=pending,adobeRgbAvailable=File.Exists(Imaging.AdobeRgbProfile)};
        }

        internal object DeliveryShelf(){lock(gate)return db.Transaction(false,ReadDeliveryShelf);}

        internal byte[] DeliveryLogoBytes(string hash)
        {
            string key=DeliveryLogosKey+DeliveryLogoRef.ValidHash(hash),encoded;
            lock(gate)encoded=(string)db.Scalar("SELECT value FROM settings WHERE name=? AND length(value)<=?",key,4*((DeliveryLogo.MaxBytes+2)/3));
            if(encoded==null)throw new InvalidDataException("未找到已保存的 PNG 标志，请重新选择原标志。");
            var bytes=Convert.FromBase64String(encoded);
            if(bytes.Length>DeliveryLogo.MaxBytes || DeliveryLogo.Digest(bytes)!=hash)throw new InvalidDataException("已保存的 PNG 标志校验失败，请重新选择原标志。");
            return bytes;
        }

        private static string LogoOf(DeliveryPreset p){return p.watermark!=null && p.watermark.kind=="png" && p.watermark.logo!=null?p.watermark.logo.hash:null;}
        private void ReleaseUnreferencedLogo(string hash)
        {
            if(hash==null || DeliveryRecords<DeliveryPreset>(DeliveryPresetsKey).Any(p=>LogoOf(p)==hash) ||
                DeliveryRecords<DeliveryHeader>(DeliveryJobsKey).Any(j=>LogoOf(j.preset)==hash))return;
            db.Run("DELETE FROM settings WHERE name=?",DeliveryLogosKey+hash);
        }

        internal object SaveDeliveryPreset(DeliveryPreset preset,DeliveryLogo picked=null)
        {
            lock(gate)return db.Transaction(true,delegate{
                string oldLogo=null;
                if(DeliveryRecords<DeliveryPreset>(DeliveryPresetsKey).Any(p=>p.id!=preset.id && String.Equals(p.name,preset.name,StringComparison.OrdinalIgnoreCase)))
                    throw new ArgumentException("已有同名交付预设，请换一个名称。");
                if(preset.id=="")
                {
                    if(preset.revision!=0)throw new ArgumentException("新预设版本无效。");
                    preset.id=NewId();
                }
                else{
                    var previous=DeliveryRecord<DeliveryPreset>(DeliveryPresetsKey+preset.id);oldLogo=LogoOf(previous);
                    if(previous.revision!=preset.revision)throw new InvalidOperationException("交付预设已在其他窗口更新，请重新选择后编辑。");
                }
                var logo=preset.watermark.logo;
                if(preset.watermark.kind=="png"){
                    if(picked!=null && picked.Info.hash==logo.hash){
                        logo.width=picked.Info.width;logo.height=picked.Info.height;
                        db.Run("INSERT INTO settings(name,value) VALUES(?,?) ON CONFLICT(name) DO UPDATE SET value=excluded.value WHERE value<>excluded.value",
                            DeliveryLogosKey+logo.hash,Convert.ToBase64String(picked.Bytes));
                    }else{
                        var pixels=Imaging.DecodeDeliveryLogo(DeliveryLogoBytes(logo.hash),CancellationToken.None);
                        logo.width=pixels.PixelWidth;logo.height=pixels.PixelHeight;
                    }
                }
                preset.revision=checked(preset.revision+1);PutDelivery(DeliveryPresetsKey+preset.id,preset);
                db.Run("INSERT OR REPLACE INTO settings(name,value) VALUES(?,?)",DeliverySelectedKey,preset.id);
                if(oldLogo!=LogoOf(preset))ReleaseUnreferencedLogo(oldLogo);
                return ReadDeliveryShelf();
            });
        }

        internal object SelectDeliveryPreset(string id)
        {
            lock(gate)return db.Transaction(true,delegate{
                DeliveryRecord<DeliveryPreset>(DeliveryPresetsKey+DeliveryPreset.Key(id));
                db.Run("INSERT OR REPLACE INTO settings(name,value) VALUES(?,?)",DeliverySelectedKey,id);
                return ReadDeliveryShelf();
            });
        }

        internal DeliveryHeader NewDelivery(string[] photos,string presetId,int revision,string directory,CancellationToken cancel)
        {
            string root=SharedDeliveryPause.ResolveRoot(directory),rootIdentity=DeliveryDirectory.RootIdentity(root);
            lock(gate)return db.Transaction(true,delegate{
                var preset=DeliveryRecord<DeliveryPreset>(DeliveryPresetsKey+DeliveryPreset.Key(presetId));
                if(preset.revision!=revision)throw new InvalidOperationException("交付预设已更新，请重新选择后导出。");
                var ids=photos.Distinct(StringComparer.Ordinal).ToArray();
                if(ids.Length==0)throw new ArgumentException("请先选择要交付的照片。");
                var job=new DeliveryHeader{id=NewId(),root=root,rootIdentity=rootIdentity,created=Now(),engine=Imaging.EngineVersion,preset=preset,total=ids.Length};
                PutDelivery(DeliveryJobsKey+job.id,job);
                int index=0;
                foreach(string id in ids)
                {
                    cancel.ThrowIfCancellationRequested();
                    var state=Current(id);CheckEngine(state.Engine);
                    var source=SourceRecord(id);
                    var photo=new DeliveryPhoto{id=id,index=++index,original=Path.GetFileName(source.Path),source=source.Hash,editingRevision=state.Revision,
                        values=state.Values.Where(p=>p.Value!=0).ToDictionary(p=>p.Key,p=>p.Value)};
                    SaveDeliveryPhoto(job.id,photo);
                }
                return job;
            });
        }

        internal DeliveryHeader ReadDelivery(string id)
        {
            lock(gate){
                var job=DeliveryRecord<DeliveryHeader>(DeliveryJobsKey+DeliveryPreset.Key(id));CheckEngine(job.engine);
                job.preset=DeliveryPreset.Read(json.Deserialize<Dictionary<string,object>>(json.Serialize(job.preset)));
                return job;
            }
        }

        internal List<DeliveryPhoto> DeliveryRows(string id){lock(gate)return DeliveryRecords<DeliveryPhoto>(DeliveryRowsKey(id));}

        internal void CheckDeliveryPrivacy(DeliveryPhoto photo)
        {
            Dictionary<string,double> current;
            lock(gate){
                EnsureNoPrivacyDraft(photo.id);
                var version=db.Scalar("SELECT revision FROM photos WHERE id=?",photo.id);
                if(version!=null && photo.editingRevision==(long)version)return;
                current=Current(photo.id).Values;
            }
            string hash=LocalEdits.Hash(current);
            if(hash==null || hash==LocalEdits.Hash(photo.values))return;
            var needed=LocalEdits.ReadSet(current).areas;
            if(needed==null || !needed.Any(m=>m.kind=="redactRect"))return;
            var saved=LocalEdits.ReadSet(photo.values).areas??new LocalEdits.AreaMark[0];
            var covered=saved.Where(m=>m.kind=="redactRect").Select(PrivacyRect).OrderBy(r=>r.Top).ToArray();
            foreach(var box in needed.Where(m=>m.kind=="redactRect")){
                if(!CoveredByJob(PrivacyRect(box),covered))
                    throw new InvalidOperationException("照片的隐私遮挡已经改变。请用当前照片重新发起交付。");
            }
        }

        private static Rect PrivacyRect(LocalEdits.AreaMark box)
        {
            var a=box.points[0];var b=box.points[1];
            return new Rect(new Point(a.x,a.y),new Point(b.x,b.y));
        }

        private static bool CoveredByJob(Rect target,Rect[] saved)
        {
            // Within each vertical strip no rectangle starts or ends. Its sorted
            // y-intervals must cover the target without a gap.
            var stops=saved.SelectMany(r=>new[]{r.Left,r.Right}).Where(x=>x>target.Left&&x<target.Right)
                .Concat(new[]{target.Left,target.Right}).Distinct().OrderBy(x=>x).ToArray();
            for(int i=1;i<stops.Length;i++){
                double bottom=target.Top;
                foreach(var r in saved){
                    if(r.Left>stops[i-1]||r.Right<stops[i])continue;
                    if(r.Top>bottom)break;
                    bottom=Math.Max(bottom,r.Bottom);
                    if(bottom>=target.Bottom)break;
                }
                if(bottom<target.Bottom)return false;
            }
            return true;
        }

        internal void SaveDeliveryPhoto(string job,DeliveryPhoto photo)
        {
            lock(gate)PutDelivery(DeliveryRowsKey(job)+photo.index.ToString("D8",System.Globalization.CultureInfo.InvariantCulture),photo);
        }

        internal IDisposable HoldDeliverySource(DeliveryPhoto photo,CancellationToken cancel,out string path)
        {
            OriginalRecord record;
            lock(gate)record=SourceRecord(photo.id);
            var file=new OriginalFile(record.Path);
            try
            {
                if(!MatchesOriginal(record,file,cancel))throw new IOException("原片已经改变，请先找回正确的原片。");
                string source=record.Hash??file.Digest(cancel);
                if(photo.source!=null && photo.source!=source)throw new IOException("原片与这批交付的原片指纹不一致。");
                photo.source=source;path=WorkspacePaths.ForIO(record.Path);return file;
            }
            catch{file.Dispose();throw;}
        }

        internal void FinishDelivery(string id)
        {
            lock(gate)db.Transaction(true,delegate{
                string logo=LogoOf(DeliveryRecord<DeliveryHeader>(DeliveryJobsKey+id).preset);
                db.Run("DELETE FROM settings WHERE name GLOB ?",DeliveryRowsKey(id)+"*");
                db.Run("DELETE FROM settings WHERE name=?",DeliveryJobsKey+id);ReleaseUnreferencedLogo(logo);return true;
            });
        }
    }
}
