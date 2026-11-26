using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace AIPhotoStudio
{
    internal sealed partial class StudioBridge
    {
        private sealed class BodyInputTicket
        {
            internal string Token, PhotoId, SourceHash;
            internal long Revision;
            internal bool Portrait;
            internal int Width,Height;
        }

        private readonly object bodyTicketLock = new object();
        private long bodyInputSequence;
        private BodyInputTicket bodyTicket;

        private async Task<object> BodyInput(Dictionary<string, object> args, bool portrait=false)
        {
            Selected(args);
            string id = TextOf(args, "id");
            long revision = Convert.ToInt64(args["revision"]);
            long sequence = Interlocked.Increment(ref bodyInputSequence);
            lock (bodyTicketLock) bodyTicket = null;
            string hash = await Task.Run(() => work.OriginalHash(id));
            Selected(args);
            string path = work.PathFor(id);
            bool fine = !portrait && args.ContainsKey("fine") && Object.Equals(args["fine"], true);
            int edge = portrait ? 1200 : fine ? 2304 : 960;
            var neutral = work.NeutralFor(id,path,revision);
            BitmapSource input = await Render(path, neutral, edge);
            if (!portrait && Math.Max(input.PixelWidth,input.PixelHeight)==edge &&
                Math.Max(input.PixelWidth,input.PixelHeight)>2.4*Math.Min(input.PixelWidth,input.PixelHeight))
                input = await Render(path, neutral, 3072);
            var encoded = Map(await EncodePreview(input));
            string confirmedHash = await Task.Run(() => work.OriginalHash(id));
            Selected(args);
            if (sequence != Interlocked.Read(ref bodyInputSequence) || !String.Equals(hash, confirmedHash, StringComparison.Ordinal))
                throw new InvalidOperationException("原片或识别请求已更改，请重新识别。");
            var ticket = new BodyInputTicket { Token = Guid.NewGuid().ToString("N"), PhotoId = id, SourceHash = hash, Revision = revision,
                Portrait=portrait,Width=input.PixelWidth,Height=input.PixelHeight };
            lock (bodyTicketLock) bodyTicket = ticket;
            return new Dictionary<string, object> { { "token", ticket.Token }, { "image", encoded["image"] },
                { "width", input.PixelWidth }, { "height", input.PixelHeight } };
        }

        private async Task<object> BodySave(Dictionary<string, object> args)
        {
            Selected(args);
            string id = TextOf(args, "id"), token = TextOf(args, "token");
            long revision = Convert.ToInt64(args["revision"]);
            BodyInputTicket ticket;
            lock (bodyTicketLock)
            {
                ticket = bodyTicket;
                if (ticket == null || ticket.Portrait || ticket.Token != token || ticket.PhotoId != id || ticket.Revision != revision)
                    throw new InvalidOperationException("人体检测已过期，请对当前照片重新识别。");
                bodyTicket = null;
            }
            object result;
            if (!args.TryGetValue("result", out result)) throw new ArgumentException("缺少识别结果。");
            string hash = await Task.Run(() => work.OriginalHash(id));
            Selected(args);
            if (!String.Equals(hash, ticket.SourceHash, StringComparison.Ordinal))
                throw new InvalidOperationException("照片的原始内容已变化，请重新识别。");
            var draft=new PersonDetectionDraft();
            var patch = BodyAssets.Save(hash, result,draft);
            var current = work.ValuesFor(id);
            var doc=Selected(args);
            int[] slots;
            patch=BodyAssets.Merge(current,patch,Strings(doc["locks"]),out slots,draft);
            if (BodyAssets.ResourceHash(current) == BodyAssets.ResourceHash(patch)) return Snapshot();
            if (!BodyAssets.CanReplace(current,patch,Strings(doc["locks"]),draft))
                throw new InvalidOperationException("这次识别改变了已有调整的人物或部位。已保留原来的人物框和美型设置。");
            draft.StoreSelected(patch);
            return CommitPatch(args, patch, "识别照片中的人物");
        }

        private static int BodyPersonIndex(Dictionary<string,object> args,IDictionary<string,double> values,bool required)
        {
            var people=BodyAssets.Describe(values);
            object raw;
            if(!args.TryGetValue("personIndex",out raw) || raw==null)
            {
                if(people.Length==1)return people[0].index;
                if(!required)return -1;
                throw new InvalidOperationException(people.Length==0?"请先识别照片中的人物。":"请先点选要调整的人物。");
            }
            if(!(raw is int) && !(raw is long))throw new ArgumentException("无效的人物编号。");
            long index=Convert.ToInt64(raw);
            if(index<0 || index>=people.Length)throw new ArgumentException("此人物不在当前照片中，请重新选择。");
            return (int)index;
        }

        private object AutoBody(Dictionary<string,object> args)
        {
            var doc=Selected(args);
            var values=work.ValuesFor(TextOf(args,"id"));
            int index=BodyPersonIndex(args,values,true);
            BodyGeometry pose=BodyAssets.ResolvePerson(values,index);
            var locks=new HashSet<string>(Strings(doc["locks"]),StringComparer.Ordinal);
            var patch=new Dictionary<string,double>();
            Action<string,int,int,int> propose=delegate(string baseKey,int a,int b,int amount)
            {
                string key=BodyEdits.KeyFor(index,baseKey);
                double visibility=Math.Min(pose.Points[a].Visibility,pose.Points[b].Visibility);
                if(BodyAssets.CanAdjust(values,key) && !locks.Contains(key) && values[key]==0)
                    patch[key]=Math.Max(1,Math.Round(amount*visibility));
            };
            propose("body_shoulders",11,12,8);
            propose("body_waist",23,24,16);
            propose("body_thighs",25,26,12);
            propose("body_leglength",27,28,9);
            if(patch.Count==0)propose("body_arms",13,14,8);
            if(patch.Count==0)propose("body_abdomen",23,24,8);
            if(patch.Count==0)throw new InvalidOperationException("当前可见部位已经调整或锁定，可在滑块上继续微调。");
            return work.IssueProposal(args,patch,"人物 "+(index+1)+" · 自动美型","按所选人物的可见部位生成轻度调整。");
        }
    }
}
