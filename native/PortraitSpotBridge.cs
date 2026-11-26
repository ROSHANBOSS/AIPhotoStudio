using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace AIPhotoStudio
{
    internal sealed partial class StudioBridge
    {
        private sealed class SpotTicket
        {
            internal string Id,Token,Geometry,Previewed;
            internal long Revision,PreviewOrder;
            internal int Face;
            internal PortraitSpot[] Candidates;
        }
        private readonly object spotGate=new object();
        private long spotOrder;
        private SpotTicket currentSpot;
        private async Task<object> FindPortraitSpots(Dictionary<string,object> args)
        {
            var doc=Selected(args);string id=TextOf(args,"id");
            var values=work.ValuesFor(id);int index=PortraitIndex(args,values);
            var scene=PortraitAssets.Resolve(values);
            if(scene==null||index<0)throw new InvalidOperationException("请先识别并选择要检查的人脸。");
            long order=Interlocked.Increment(ref spotOrder);
            lock(spotGate)currentSpot=null;
            string path=work.PathFor(id);
            await Task.Run(()=>PortraitAssets.ValidateFile(path,values,true));
            BitmapSource image;
            await renderSlots.WaitAsync(closing.Token);
            try{image=await Task.Run(()=>Imaging.PortraitSearchImage(path,values,closing.Token));}
            finally{renderSlots.Release();}
            var candidates=await Task.Run(()=>PortraitSpots.Find(image,scene,index,closing.Token));
            var frame=Map(await EncodePreview(image));
            await Task.Run(()=>PortraitAssets.ValidateFile(path,values,true));
            Selected(args);
            var ticket=new SpotTicket{Id=id,Geometry=PortraitAssets.Hash(values),Revision=Convert.ToInt64(doc["revision"]),
                Token=Guid.NewGuid().ToString("N"),Face=index,Candidates=candidates};
            lock(spotGate){
                if(order!=Interlocked.Read(ref spotOrder))throw new InvalidOperationException("已经开始检查另一张脸。");
                currentSpot=ticket;
            }
            return new {token=ticket.Token,points=candidates,image=frame["image"],width=image.PixelWidth,height=image.PixelHeight,box=scene.Faces[index].Box};
        }
        private SpotTicket SpotSelection(Dictionary<string,object> args,out int[] chosen)
        {
            Selected(args);
            SpotTicket ticket;
            lock(spotGate)ticket=currentSpot;
            string id=TextOf(args,"id");var values=work.ValuesFor(id);
            if(ticket==null||ticket.Token!=TextOf(args,"token")||ticket.Id!=id||ticket.Revision!=Convert.ToInt64(args["revision"])||
                !Object.Equals(args["faceIndex"],ticket.Face)||ticket.Geometry!=PortraitAssets.Hash(values))
                throw new InvalidOperationException("小瑕疵候选已过期，请重新查找。");
            var raw=args["selected"] as IEnumerable;
            if(raw==null||raw is string)throw new ArgumentException("请选择要修复的小点。");
            var ids=new HashSet<int>();
            foreach(object value in raw){
                if(!(value is int)||(int)value<0||(int)value>=ticket.Candidates.Length||!ids.Add((int)value))
                    throw new ArgumentException("选择中包含无效的候选点。");
            }
            if(ids.Count==0)throw new InvalidOperationException("请至少选择一处候选点。");
            chosen=ids.OrderBy(i=>i).ToArray();
            PortraitAssets.ValidateFile(work.PathFor(id),values,true);
            return ticket;
        }
        private async Task<object> PreviewPortraitSpots(Dictionary<string,object> args)
        {
            int[] ids;var ticket=SpotSelection(args,out ids);long sequence;
            lock(spotGate){sequence=++ticket.PreviewOrder;ticket.Previewed=null;}
            var values=work.ValuesFor(ticket.Id);var spots=ids.Select(i=>ticket.Candidates[i]).ToArray();
            var image=await RenderPreview(work.PathFor(ticket.Id),FrameGeometry.SourceValues(values),1200,false,null,ticket.Id,false,spots);
            Selected(args);
            lock(spotGate){
                if(currentSpot!=ticket||ticket.PreviewOrder!=sequence)throw new InvalidOperationException("点修预览已更新。");
                ticket.Previewed=String.Join(",",ids);
            }
            return image;
        }
        private object CancelPortraitSpots(Dictionary<string,object> args)
        {
            lock(spotGate){
                if(currentSpot!=null&&currentSpot.Token==TextOf(args,"token")&&currentSpot.Id==TextOf(args,"id"))currentSpot=null;
            }
            return null;
        }
        private object SavePortraitSpots(Dictionary<string,object> args)
        {
            int[] ids;var ticket=SpotSelection(args,out ids);
            lock(spotGate){
                if(currentSpot!=ticket||ticket.Previewed!=String.Join(",",ids))throw new InvalidOperationException("请先预览本次勾选的小瑕疵。");
                var patch=PortraitAssets.WithSpots(work.ValuesFor(ticket.Id),ids.Select(i=>ticket.Candidates[i]).ToArray());
                var saved=CommitPatch(args,patch,"人脸 "+(ticket.Face+1)+" · 修复 "+ids.Length+" 处小瑕疵");
                currentSpot=null;return saved;
            }
        }
    }
}
