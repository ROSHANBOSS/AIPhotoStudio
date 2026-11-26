using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Windows.Media.Imaging;

namespace AIPhotoStudio
{
    internal sealed partial class StudioBridge
    {
        // The chooser holds at most one unsaved logo. Only a saved preset commits its bytes to this channel.
        private DeliveryLogo pickedLogo;
        private async Task<object> Delivery(string method,Dictionary<string,object> args)
        {
            switch(method)
            {
                case "deliveryPresets":return work.DeliveryShelf();
                case "deliveryPhotoIds":return work.DeliveryRows(TextOf(args,"jobId")).Select(p=>p.id).ToArray();
                case "deliverySave":
                    var definition=DeliveryPreset.Read(args);var picked=pickedLogo;
                    return await Task.Run(()=>work.SaveDeliveryPreset(definition,picked));
                case "deliverySelect":return work.SelectDeliveryPreset(TextOf(args,"presetId"));
                case "deliveryPickLogo":
                    using(var open=new OpenFileDialog{Title=L("选择 PNG 标志","Choose a PNG logo"),Filter=L("PNG 图形标志|*.png","PNG logo|*.png"),CheckFileExists=true,Multiselect=false}){
                        if(open.ShowDialog(owner)!=DialogResult.OK)return null;
                        string logoPath=open.FileName;
                        var selected=await Task.Run(()=>DeliveryLogo.Import(logoPath,closing.Token));pickedLogo=selected;return selected.Info;
                    }
                case "deliveryLogoPreview":
                    string hash=DeliveryLogoRef.ValidHash(TextOf(args,"hash"));
                    var draft=pickedLogo;
                    if(draft!=null && draft.Info.hash==hash)return draft.Thumbnail;
                    return await Task.Run(()=>Imaging.DeliveryLogoThumbnail(Imaging.DecodeDeliveryLogo(work.DeliveryLogoBytes(hash),closing.Token)));
                case "deliveryStart":
                    var batch=BeginBatch(Strings(args["ids"]),TextOf(args,"batchToken"));
                    try
                    {
                        using(var picker=new FolderBrowserDialog{Description=L("选择交付根目录，小样和成片将写入各自的子目录","Choose the delivery folder. Proofs and final images will be saved in separate subfolders."),ShowNewFolderButton=true})
                        {
                            if(picker.ShowDialog(owner)!=DialogResult.OK)return null;
                            string directory=picker.SelectedPath,presetId=TextOf(args,"presetId");
                            int revision=DeliveryPreset.Integer(args,"revision",1,Int32.MaxValue);
                            lock(batchGate){batch.Started=true;batch.File=L("本批照片清单","Batch photo list");}
                            return await Task.Run(()=>{
                                DeliveryHeader job;
                                try{job=work.NewDelivery(batch.Photos,presetId,revision,directory,batch.Stop.Token);}
                                catch(OperationCanceledException){if(!batch.Stop.IsCancellationRequested)throw;
                                    return (object)new{id="",root=directory,total=batch.Total,paired=0,partial=0,count=0,paths=new string[0],errors=new string[0],cancelled=true};}
                                return WriteDelivery(batch,job.id);
                            });
                        }
                    }
                    finally{EndBatch(batch);}
                case "deliveryResume":
                    string id=DeliveryPreset.Key(TextOf(args,"jobId"));
                    work.EnsureNoPrivacyDrafts(work.DeliveryRows(id).Select(row=>row.id));
                    var resume=BeginBatch(null,work.ReadDelivery(id).total,TextOf(args,"batchToken"));
                    try{return await Task.Run(()=>WriteDelivery(resume,id));}
                    finally{EndBatch(resume);}
                default:throw new ArgumentException("未知交付操作。");
            }
        }

        internal object WriteDelivery(BatchRun batch,string id)
        {
            string leasePath=Path.Combine(channelDirectory,"Projects","delivery-"+DeliveryPreset.Key(id)+".lock");
            using(new FileStream(leasePath,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None,256,FileOptions.DeleteOnClose))
            {
                lock(batchGate){batch.Started=true;batch.File=L("本批照片清单","Batch photo list");}
                var job=work.ReadDelivery(id);var rows=work.DeliveryRows(id);
                var cancel=batch.Stop.Token;
                string pauseReason=null;
                try{job.root=SharedDeliveryPause.ResolveRoot(job.root);}
                catch(SharedDeliveryPause paused){pauseReason=paused.Message;}
                var mark=job.preset.watermark;
                if(mark.kind=="png")mark.picture=new Lazy<BitmapSource>(()=>Imaging.DecodeDeliveryLogo(work.DeliveryLogoBytes(mark.logo.hash),cancel));
                foreach(var photo in rows)
                {
                    if(pauseReason!=null || cancel.IsCancellationRequested)break;
                    string path=null;IDisposable original=null;
                    var source=new Lazy<string>(()=>{original=work.HoldDeliverySource(photo,cancel,out path);return path;});
                    var render=new Lazy<BitmapSource>(()=>Imaging.Render(source.Value,photo.values,0,cancel));
                    try
                    {
                        WriteDeliveryFile(job,photo,true,batch,source,render);
                        WriteDeliveryFile(job,photo,false,batch,source,render);
                    }
                    catch(SharedDeliveryPause paused){pauseReason=paused.Message;break;}
                    catch(OperationCanceledException){if(!cancel.IsCancellationRequested)throw;break;}
                    finally{if(original!=null)original.Dispose();}
                    lock(batchGate){if(photo.proof.done && photo.final.done)batch.Written++;else batch.Failed++;}
                }
                if(pauseReason!=null)rows=work.DeliveryRows(id);
                int pairs=rows.Count(p=>p.proof.done && p.final.done);
                if(pairs==rows.Count && pauseReason==null && !cancel.IsCancellationRequested)work.FinishDelivery(id);
                var paths=new List<string>();var errors=new List<string>();
                foreach(var row in rows)
                {
                    if(row.proof.done)paths.Add(Path.Combine(job.root,job.preset.proof.folder,row.proof.name));
                    if(row.final.done)paths.Add(Path.Combine(job.root,job.preset.final.folder,row.final.name));
                    if(pauseReason==null && row.proof.error!=null)errors.Add(row.original+L(" · 小样 · "," · Proof · ")+LocalMessage(row.proof.error,"deliveryStart"));
                    if(pauseReason==null && row.final.error!=null)errors.Add(row.original+L(" · 成片 · "," · Final · ")+LocalMessage(row.final.error,"deliveryStart"));
                }
                return new{id=id,root=job.root,total=rows.Count,paired=pairs,partial=rows.Count(r=>r.proof.done!=r.final.done),
                    count=paths.Count,paths=paths,errors=errors,pauseReason=LocalMessage(pauseReason),cancelled=cancel.IsCancellationRequested};
            }
        }

        private void WriteDeliveryFile(DeliveryHeader job,DeliveryPhoto photo,bool isProof,BatchRun batch,Lazy<string> source,Lazy<BitmapSource> render)
        {
            var cancel=batch.Stop.Token;cancel.ThrowIfCancellationRequested();
            var spec=isProof?job.preset.proof:job.preset.final;
            var file=isProof?photo.proof:photo.final;
            lock(batchGate)batch.File=photo.original+(isProof?" · 小样":" · 成片");
            try
            {
                work.CheckDeliveryPrivacy(photo);
                using(var dir=new DeliveryDirectory(job.root,job.rootIdentity,spec.folder,isProof?job.preset.final.folder:job.preset.proof.folder))
                {
                    string stagedIdentity=file.done?null:file.identity;
                    dir.RecoverStage(file);
                    // A not-yet-committed file can only be claimed when the recorded staging identity follows its rename.
                    if((file.done || !String.IsNullOrEmpty(stagedIdentity)) && !String.IsNullOrEmpty(file.hash) &&
                        !String.IsNullOrEmpty(file.name) && Path.GetFileName(file.name)==file.name &&
                        DeliveryDirectory.Matches(Path.Combine(dir.PathName,file.name),file.hash,stagedIdentity,cancel))
                    {
                        file.done=true;file.error=null;file.temporary=null;file.identity=null;work.SaveDeliveryPhoto(job.id,photo);return;
                    }
                    file.done=false;file.hash=null;file.error=null;file.temporary=null;file.identity=null;
                    string stem=spec.Stem(Path.GetFileNameWithoutExtension(photo.original),photo.index);
                    string path=source.Value;
                    while(true)
                    {
                        cancel.ThrowIfCancellationRequested();
                        string target=ChooseNewName(dir.PathName,stem,spec.Extension);
                        file.name=Path.GetFileName(target);
                        try
                        {
                            Imaging.WriteOutput(path,target,()=>render.Value,cancel,spec.longEdge,spec.quality,isProof?job.preset.watermark:null,spec.colorSpace,
                                (temporary,stream)=>{
                                    file.temporary=Path.GetFileName(temporary);file.identity=DeliveryDirectory.Identity(stream.SafeFileHandle);
                                    work.SaveDeliveryPhoto(job.id,photo);
                                },stream=>{
                                    file.hash=DeliveryDirectory.Digest(stream,cancel);work.CheckDeliveryPrivacy(photo);work.SaveDeliveryPhoto(job.id,photo);
                                },spec.maxBytes,spec.minEdge,spec.matte);
                            break;
                        }
                        catch(IOException error)
                        {
                            if(SharedDeliveryPause.LostConnection(job.root,error))throw;
                            // A simultaneous new export may have filled this name during encoding. Its file stays in place.
                            if(!File.Exists(target) && !Directory.Exists(target))throw;
                            if(file.hash!=null && file.identity!=null && DeliveryDirectory.Matches(target,file.hash,file.identity,cancel))break;
                            file.hash=null;file.temporary=null;file.identity=null;
                        }
                    }
                    file.done=true;file.temporary=null;file.identity=null;
                }
            }
            catch(OperationCanceledException){throw;}
            catch(SharedDeliveryPause){throw;}
            catch(Exception error)
            {
                if(SharedDeliveryPause.LostConnection(job.root,error))
                    throw new SharedDeliveryPause("共享连接中断。重新连接原片及交付目录后，可继续核对和补交。",error);
                // Some codecs return a generic stream failure. Recheck the destination before recording a per-file error.
                if(SharedDeliveryPause.IsShare(job.root))using(DeliveryDirectory.HoldRoot(job.root,job.rootIdentity)){}
                file.done=false;file.error=error.GetBaseException().Message;
            }
            work.SaveDeliveryPhoto(job.id,photo);
        }
    }
}
