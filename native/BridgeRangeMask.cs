using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace AIPhotoStudio
{
    internal sealed partial class StudioBridge
    {
        private sealed class RangeTicket
        {
            internal string Id,Hash,Kind,Token;
            internal long Revision;
            internal LocalEdits.RangeSampler Sampler;
        }
        private RangeTicket rangeTicket;

        private async Task<object> RangeInput(Dictionary<string,object> args)
        {
            string id=TextOf(args,"id"),kind=TextOf(args,"kind");
            long revision=Convert.ToInt64(args["revision"]);
            work.VerifyPreviewRevision(id,revision);
            if(!LocalEdits.RangeKind(kind))throw new ArgumentException("请选择颜色或亮度范围。");
            var ticket=new RangeTicket{Id=id,Revision=revision,Kind=kind,Token=Guid.NewGuid().ToString("N")};
            rangeTicket=ticket;
            string path=work.PathFor(id);
            var neutral=work.NeutralFor(id,path,revision);
            ticket.Hash=await Task.Run(()=>work.OriginalHash(id));
            var values=work.PreviewValues(id,revision,null,false);
            var areas=await Task.Run(()=>Imaging.SamplingRedactions(path,values));
            var image=areas==null?await Render(path,neutral,768):
                await Task.Run(()=>Imaging.ConcealedRangeInput(path,neutral,areas,closing.Token),closing.Token);
            ticket.Sampler=await Task.Run(()=>new LocalEdits.RangeSampler(image));
            var reference=areas==null?null:await EncodePreview(image);
            work.VerifyPreviewRevision(id,revision);
            if(ticket!=rangeTicket||ticket.Hash!=await Task.Run(()=>work.OriginalHash(id)))throw new InvalidOperationException("原片或取样请求已变化，请重新取样。");
            return new {token=ticket.Token,reference=reference};
        }

        private RangeTicket CheckedRangeTicket(Dictionary<string,object> args)
        {
            string id=TextOf(args,"id");long revision=Convert.ToInt64(args["revision"]);
            work.VerifyPreviewRevision(id,revision);
            var ticket=rangeTicket;
            if(ticket==null||ticket.Sampler==null||ticket.Id!=id||ticket.Revision!=revision||
                ticket.Kind!=TextOf(args,"kind")||ticket.Token!=TextOf(args,"token"))
                throw new InvalidOperationException("范围取样已过期，请重新打开当前原片的范围蒙版。");
            return ticket;
        }

        private object RangePreview(Dictionary<string,object> args)
        {
            var ticket=CheckedRangeTicket(args);
            var range=LocalEdits.ReadRange(ticket.Kind,args["range"]);
            object raw;
            if(args.TryGetValue("point",out raw))
            {
                var point=Map(raw);
                ticket.Sampler.Pick(ticket.Kind,range,StudioWorkspace.Number(point["x"]),StudioWorkspace.Number(point["y"]));
            }
            int covered;
            string alpha=ticket.Sampler.Preview(ticket.Kind,range,out covered);
            return new {range=range,alpha=alpha,covered=covered};
        }
    }
}
