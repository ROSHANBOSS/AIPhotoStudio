using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace AIPhotoStudio
{
    internal sealed partial class StudioBridge
    {
        private async Task<object> PortraitSave(Dictionary<string,object> args)
        {
            Selected(args);
            string id=TextOf(args,"id");
            BodyInputTicket ticket;
            lock(bodyTicketLock){
                ticket=bodyTicket;
                if(ticket==null||!ticket.Portrait||ticket.PhotoId!=id||ticket.Token!=TextOf(args,"token")||ticket.Revision!=Convert.ToInt64(args["revision"]))
                    throw new InvalidOperationException("人脸识别已过期，请重新检测。");
                bodyTicket=null;
            }
            var result=Map(args["result"]);
            if(!Object.Equals(result["width"],ticket.Width)||!Object.Equals(result["height"],ticket.Height))
                throw new ArgumentException("人脸关键点不能用于其他尺寸的照片。");
            string hash=await Task.Run(()=>work.OriginalHash(id));
            var doc=Selected(args);
            if(hash!=ticket.SourceHash)throw new InvalidOperationException("原片在检测期间发生了变化，请重新识别。");
            var draft=new PersonDetectionDraft();
            var before=work.ValuesFor(id);
            Dictionary<string,double> patch;
            try{patch=PortraitAssets.Save(hash,result,draft);}
            catch(InvalidDataException error){
                if(!PortraitAssets.HasKeepMarks(before))throw;
                throw new InvalidOperationException("请核对原有人脸和保留位置，本次检测未写入。"+error.Message);
            }
            PortraitAssets.ConfirmKeptFaces(PortraitAssets.Resolve(before),PortraitAssets.Resolve(patch,draft));
            int[] slots;
            patch=PortraitAssets.Merge(before,patch,out slots,draft);
            if(PortraitAssets.Hash(before)==PortraitAssets.Hash(patch))return Snapshot();
            if(!PortraitAssets.CanReplace(before,patch,Strings(doc["locks"]),draft))
                throw new InvalidOperationException("本次识别与已有调整或锁定的人脸不一致，已保留原先的关键点。");
            draft.StoreSelected(patch);
            return CommitPatch(args,patch,"识别照片中的人脸关键点");
        }
        private static int PortraitIndex(Dictionary<string,object> args,IDictionary<string,double> values)
        {
            var people=PortraitAssets.Describe(values);
            object raw;
            if(!args.TryGetValue("faceIndex",out raw)||raw==null)return people.Length==1?0:-1;
            if(!(raw is int) || (int)raw<0 || (int)raw>=people.Length)throw new ArgumentException("所选人脸已改变，请重新点选。");
            return (int)raw;
        }
    }
}
