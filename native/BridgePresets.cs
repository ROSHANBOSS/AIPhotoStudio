using System;
using System.Collections.Generic;

namespace AIPhotoStudio
{
    internal sealed partial class StudioBridge
    {
        private string presetRequest, presetPhoto, savedPresetName;
        private long presetRevision;

        private object ProposePreset(Dictionary<string,object> args,string suggestedName)
        {
            presetPhoto=TextOf(args,"id");
            presetRevision=Convert.ToInt64(args["revision"]);
            presetRequest=Guid.NewGuid().ToString("N");
            savedPresetName=null;
            return new {
                action="savePreset", id=presetPhoto, revision=presetRevision, requestId=presetRequest,
                label="将当前调色保存成预设", suggestedName=suggestedName??"我的调色",
                message="确认后保存当前全图调色。手绘、人脸和前背景分区属于这张照片。",
                values=new Dictionary<string,double>()
            };
        }

        private object ConfirmPreset(Dictionary<string,object> args)
        {
            Selected(args);
            string name=TextOf(args,"name").Trim();
            if(presetRequest==null || TextOf(args,"requestId")!=presetRequest ||
               TextOf(args,"id")!=presetPhoto || Convert.ToInt64(args["revision"])!=presetRevision)
                throw new InvalidOperationException("这条保存建议已过期，请基于当前照片重新提出。");
            if(savedPresetName!=null)
            {
                if(savedPresetName!=name)throw new InvalidOperationException("这条建议已确认。");
                return Snapshot();
            }
            object result=work.SavePlannedPreset(presetPhoto,presetRevision,name);
            savedPresetName=name;
            return Snapshot(result);
        }
    }
}
