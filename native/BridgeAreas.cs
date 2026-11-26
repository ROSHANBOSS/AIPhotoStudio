using System;
using System.Collections.Generic;
using System.Linq;

namespace AIPhotoStudio
{
    internal sealed partial class StudioBridge
    {
        private static string AreaTool(Dictionary<string,object> args)
        {
            string tool=TextOf(args,"areaTool");
            if(tool!="privacy"&&tool!="guard")throw new ArgumentException("请选择可编辑的区域工具。");
            return tool;
        }
        private static bool AreaBelongs(string tool,LocalEdits.AreaMark mark){return (tool=="privacy")== (mark.kind=="redactRect");}
        private object AreaData(Dictionary<string,object> args)
        {
            Selected(args);string id=TextOf(args,"id"),tool=AreaTool(args);
            var set=LocalEdits.ReadSet(work.ValuesFor(id));LocalEdits.ValidateSource(set,work.PathFor(id));
            return (set.areas??new LocalEdits.AreaMark[0]).Where(m=>AreaBelongs(tool,m)).ToArray();
        }
        private static void UpdateAreas(LocalEdits.MaskSet set,Dictionary<string,object> args)
        {
            string tool=AreaTool(args);var marks=LocalEdits.ReadAreas(args["areas"]);
            if(marks.Any(m=>!AreaBelongs(tool,m)))throw new ArgumentException("区域与当前工具不符。");
            var combined=(set.areas??new LocalEdits.AreaMark[0]).Where(m=>!AreaBelongs(tool,m)).Concat(marks).ToArray();
            if(combined.Length>64||combined.Sum(m=>m.points.Length)>4096)throw new ArgumentException("每张照片最多可保存 64 处标记、4096 个笔迹点。");
            set.areas=combined.Length==0?null:combined;
        }
        private object SaveAreas(Dictionary<string,object> args)
        {
            Selected(args);string id=TextOf(args,"id");var set=LocalEdits.ReadSet(work.ValuesFor(id));
            UpdateAreas(set,args);
            return CommitPatch(args,LocalEdits.SaveSet(set,work.OriginalHash(id)),AreaTool(args)=="privacy"?"调整隐私遮挡":"调整文字与直线保护");
        }
    }
}
