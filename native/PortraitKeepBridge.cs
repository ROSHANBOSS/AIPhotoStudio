using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace AIPhotoStudio
{
    internal sealed partial class StudioBridge
    {
        private async Task<object> PortraitKeepInput(Dictionary<string,object> args)
        {
            Selected(args);
            string id=TextOf(args,"id"),path=work.PathFor(id);
            var values=work.ValuesFor(id);int index=PortraitIndex(args,values);
            var scene=PortraitAssets.Resolve(values);
            if(scene==null||index<0)throw new InvalidOperationException("请先选择要保留痣或雀斑的人脸。");
            long revision=Convert.ToInt64(args["revision"]);
            await Task.Run(()=>PortraitAssets.ValidateFile(path,values,true));
            var image=await Render(path,work.NeutralFor(id,path,revision),1200);
            var frame=Map(await EncodePreview(image));
            await Task.Run(()=>PortraitAssets.ValidateFile(path,values,true));
            Selected(args);
            return new {image=frame["image"],width=image.PixelWidth,height=image.PixelHeight,mesh=PortraitAssets.Hash(values),
                box=scene.Faces[index].Box,points=scene.KeepMarks.Where(m=>m.face==index).ToArray()};
        }
        private object SavePortraitKeep(Dictionary<string,object> args)
        {
            Selected(args);string id=TextOf(args,"id");
            var values=work.ValuesFor(id);int index=PortraitIndex(args,values);
            var scene=PortraitAssets.Resolve(values);
            if(scene==null||index<0||TextOf(args,"mesh")!=PortraitAssets.Hash(values))
                throw new InvalidOperationException("人脸已改变，请重新核对保留位置。");
            object input;
            if(!args.TryGetValue("points",out input)||!(input is IEnumerable)||input is string)
                throw new ArgumentException("请选择人脸上的保留位置。");
            var points=new List<PortraitSpot>();
            foreach(var entry in (IEnumerable)input){
                var data=entry as IDictionary<string,object>;
                if(data==null||data.Count!=3||!data.ContainsKey("x")||!data.ContainsKey("y")||!data.ContainsKey("radius")||points.Count>=PortraitKeep.Limit)
                    throw new ArgumentException("保留位置需要横纵坐标和半径，每张照片最多 96 处。");
                points.Add(new PortraitSpot{face=index,x=StudioWorkspace.Number(data["x"]),y=StudioWorkspace.Number(data["y"]),radius=StudioWorkspace.Number(data["radius"])});
            }
            PortraitAssets.ValidateFile(work.PathFor(id),values,true);
            var combined=scene.KeepMarks.Where(p=>p.face!=index).Concat(points).OrderBy(p=>p.face).ToArray();
            var patch=PortraitAssets.WriteEdits(values,scene.Spots,combined);
            if(PortraitAssets.Hash(values)==PortraitAssets.Hash(patch))return Snapshot();
            return CommitPatch(args,patch,"人脸 "+(index+1)+" · 保留痣和雀斑");
        }
    }
}
