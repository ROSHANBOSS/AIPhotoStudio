using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace AIPhotoStudio
{
    internal sealed partial class StudioBridge
    {
        private string pendingSegmentId;
        private long pendingSegmentRevision;
        private string pendingSegmentToken;
        private byte[] pendingSegment;

        private static double SeedCoordinate(Dictionary<string,object> point, string name)
        {
            object raw;
            if (!point.TryGetValue(name,out raw) || !(raw is int || raw is long || raw is double || raw is decimal))
                throw new ArgumentException("请在原片内点选前景和背景。");
            double value = Convert.ToDouble(raw,CultureInfo.InvariantCulture);
            if (!(value >= 0 && value <= 1)) throw new ArgumentException("分区取样点超出照片。");
            return value;
        }

        private async Task<object> PreviewSegment(Dictionary<string,object> args)
        {
            Selected(args);
            string id = TextOf(args,"id");
            long revision = Convert.ToInt64(args["revision"]);
            var fg = Map(args["foreground"]);
            var bg = Map(args["background"]);
            double fx = SeedCoordinate(fg,"x"), fy = SeedCoordinate(fg,"y");
            double bx = SeedCoordinate(bg,"x"), by = SeedCoordinate(bg,"y");
            int edge = args.ContainsKey("maxEdge") ? Math.Max(128,Math.Min(2048,Convert.ToInt32(args["maxEdge"]))) : 1000;
            string token = Guid.NewGuid().ToString("N");
            pendingSegmentToken = token;
            pendingSegment = null;
            string path=work.PathFor(id);
            var original = await Render(path,work.NeutralFor(id,path,revision),edge);
            byte[] alpha = await Task.Run(() => Convert.FromBase64String(SubjectSelection.Compute(original,fx,fy,bx,by)),closing.Token);
            var overlay = await Task.Run(() => SubjectSelection.Overlay(original,alpha),closing.Token);
            var result = Map(await EncodePreview(overlay));
            Selected(args);
            if (pendingSegmentToken != token) throw new InvalidOperationException("新的点选已替代本次预览。");
            pendingSegmentId = id; pendingSegmentRevision = revision; pendingSegment = alpha;
            result["segmentToken"] = token; result["revision"] = revision;
            return result;
        }

        private object SaveSegment(Dictionary<string,object> args)
        {
            Selected(args);
            if (pendingSegment == null || TextOf(args,"segmentToken") != pendingSegmentToken ||
                TextOf(args,"id") != pendingSegmentId || pendingSegmentRevision != Convert.ToInt64(args["revision"]))
                throw new InvalidOperationException("请重新预览当前照片的前背景分区。");
            var result = CommitPatch(args,SegmentAssets.Save(pendingSegment),"确认点选前景与背景");
            pendingSegment = null; pendingSegmentToken = null;
            return result;
        }

        private object UserCommit(Dictionary<string,object> args,string catalogBase)
        {
            var proposed = Map(args["values"]);
            if(proposed.Keys.Any(k=>k.StartsWith("mask_ref",StringComparison.Ordinal)))
                throw new InvalidOperationException("蒙版资源只能从已确认的笔迹、选区或配方载入。");
            if(proposed.Keys.Any(k=>k.StartsWith("portrait_ref",StringComparison.Ordinal)))
                throw new InvalidOperationException("人脸关键点需要在本机识别并保存。");
            if (proposed.Keys.Any(k => k.StartsWith("body_ref",StringComparison.Ordinal)))
                throw new InvalidOperationException("人体区域需要在本机识别并确认。");
            if (proposed.Keys.Any(k => k.StartsWith("segment_ref",StringComparison.Ordinal)))
                throw new InvalidOperationException("分区只能从已确认的分区预览或配方载入。");
            if (proposed.ContainsKey("segment_foreground") || proposed.ContainsKey("segment_background"))
            {
                Selected(args);
                if (!SegmentAssets.Supported(work.ValuesFor(TextOf(args,"id")))) throw new InvalidOperationException("请先点选并确认前背景分区。");
            }
            return Snapshot(work.Dispatch("commit",args,catalogBase));
        }
    }
}
