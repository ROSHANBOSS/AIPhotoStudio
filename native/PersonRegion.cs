using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;

namespace AIPhotoStudio
{
    internal sealed class PersonRegion
    {
        internal BodyBox Box;
        internal int Width,Height;

        internal Dictionary<string,object> MapFace(string encoded)
        {
            var bytes=Convert.FromBase64String(encoded);
            if(bytes.Length!=478*4)throw new InvalidDataException("框内人脸缺少完整关键点。");
            for(int i=0;i<478;i++){
                double x=BitConverter.ToUInt16(bytes,i*4)/65535d,y=BitConverter.ToUInt16(bytes,i*4+2)/65535d;
                ushort u=(ushort)Math.Round((Box.x+x*Box.w)*65535),v=(ushort)Math.Round((Box.y+y*Box.h)*65535);
                bytes[i*4]=(byte)u;bytes[i*4+1]=(byte)(u>>8);bytes[i*4+2]=(byte)v;bytes[i*4+3]=(byte)(v>>8);
            }
            // PSL files store the reference dimensions used to validate inter-eye distance.
            double scale=Math.Min(1,1600d/Math.Max(Width,Height));
            return new Dictionary<string,object>{{"width",Math.Max(1,(int)Math.Round(Width*scale))},{"height",Math.Max(1,(int)Math.Round(Height*scale))},
                {"meshes",new[]{Convert.ToBase64String(bytes)}}};
        }

        internal Dictionary<string,object> MapBody(Dictionary<string,object> result)
        {
            var raw=result["points"] as IEnumerable;
            if(raw==null||raw is string)throw new InvalidDataException("框内人体关键点无效。");
            var points=new List<Dictionary<string,object>>();
            foreach(var item in raw){
                var point=item as IDictionary<string,object>;
                if(point==null||points.Count>=33)throw new InvalidDataException("框内人体关键点数量无效。");
                double x=Convert.ToDouble(point["x"]),y=Convert.ToDouble(point["y"]);
                points.Add(new Dictionary<string,object>{{"x",Box.x+x*Box.w},{"y",Box.y+y*Box.h},
                    {"visibility",x<0||y<0||x>1||y>1?0:point["visibility"]}});
            }
            byte[] local=Convert.FromBase64String((string)result["mask"]);
            int side=BodyAssets.MaskSide;
            if(local.Length!=side*side)throw new InvalidDataException("框内人体分割尺寸无效。");
            var global=new byte[local.Length];
            for(int y=0;y<side;y++)for(int x=0;x<side;x++){
                // Integrate the part of this original-photo cell covered by the detector crop.
                double l=Math.Max((double)x/side,Box.x),r=Math.Min((x+1d)/side,Box.x+Box.w);
                double t=Math.Max((double)y/side,Box.y),b=Math.Min((y+1d)/side,Box.y+Box.h);
                if(l>=r||t>=b)continue;
                double u=((l+r)*.5-Box.x)/Box.w,v=((t+b)*.5-Box.y)/Box.h;
                global[y*side+x]=(byte)Math.Round(255*BodyAssets.Probability(local,u,v)*Math.Min(1,(r-l)*(b-t)*side*side));
            }
            return new Dictionary<string,object>{{"people",1},{"points",points},{"mask",Convert.ToBase64String(global)}};
        }
    }

    internal sealed partial class StudioBridge
    {
        private sealed class RegionTicket
        {
            internal string Id,Hash,Token;
            internal long Revision;
            internal PersonRegion Region;
            internal PersonDetectionDraft Draft;
            internal Dictionary<string,double> Patch;
            internal int Person;
        }
        private RegionTicket regionTicket;

        private object CancelPersonRegion(Dictionary<string,object> args)
        {
            var ticket=regionTicket;
            if(ticket!=null&&ticket.Id==TextOf(args,"id")&&ticket.Token==TextOf(args,"token"))regionTicket=null;
            return true;
        }

        private async Task<object> PersonRegionInput(Dictionary<string,object> args)
        {
            Selected(args);
            var rect=Map(args["box"]);
            double x=Convert.ToDouble(rect["x"]),y=Convert.ToDouble(rect["y"]),w=Convert.ToDouble(rect["w"]),h=Convert.ToDouble(rect["h"]);
            if(!(x>=0&&y>=0&&w>=.01&&h>=.01&&x+w<=1.000001&&y+h<=1.000001))
                throw new ArgumentException("请在照片内框出目标人物。");
            var ticket=new RegionTicket{Id=TextOf(args,"id"),Revision=Convert.ToInt64(args["revision"]),Token=Guid.NewGuid().ToString("N")};
            regionTicket=ticket;
            ticket.Hash=await Task.Run(()=>work.OriginalHash(ticket.Id));
            string path=work.PathFor(ticket.Id);
            int edge=(int)Math.Min(3072,Math.Max(1200,1200/Math.Max(w,h)));
            var full=await Render(path,work.NeutralFor(ticket.Id,path,ticket.Revision),edge);
            int width=full.PixelWidth,height=full.PixelHeight;
            int left=(int)Math.Floor(x*width),top=(int)Math.Floor(y*height);
            int right=Math.Min(width,(int)Math.Ceiling((x+w)*width)),bottom=Math.Min(height,(int)Math.Ceiling((y+h)*height));
            if(right-left<32||bottom-top<32)throw new ArgumentException("框选范围太小，请至少框住完整的脸或双肩。");
            ticket.Region=new PersonRegion{Width=width,Height=height,Box=new BodyBox{x=(double)left/width,y=(double)top/height,w=(double)(right-left)/width,h=(double)(bottom-top)/height}};
            var cropped=new CroppedBitmap(full,new Int32Rect(left,top,right-left,bottom-top));cropped.Freeze();
            var image=Map(await EncodePreview(cropped));
            Selected(args);
            if(ticket!=regionTicket||ticket.Hash!=await Task.Run(()=>work.OriginalHash(ticket.Id)))throw new InvalidOperationException("补检请求或原片已改变，请重新框选。");
            return new {token=ticket.Token,image=image["image"]};
        }

        private async Task<RegionTicket> CurrentRegion(Dictionary<string,object> args)
        {
            Selected(args);
            var ticket=regionTicket;
            if(ticket==null||ticket.Token!=TextOf(args,"token")||ticket.Id!=TextOf(args,"id")||ticket.Revision!=Convert.ToInt64(args["revision"]))
                throw new InvalidOperationException("人物补检结果已过期，请重试。");
            string hash=await Task.Run(()=>work.OriginalHash(ticket.Id));
            Selected(args);
            if(ticket!=regionTicket||ticket.Hash!=hash)throw new InvalidOperationException("原片或框选范围已改变，请重试。");
            return ticket;
        }

        private async Task<object> PersonRegionPreview(Dictionary<string,object> args)
        {
            var ticket=await CurrentRegion(args);
            if(ticket.Patch!=null)throw new InvalidOperationException("已生成补检结果，请重新框选后再试。");
            var before=work.ValuesFor(ticket.Id);
            var locks=Strings(Selected(args)["locks"]);
            var combined=new Dictionary<string,double>(before);
            var patch=new Dictionary<string,double>();
            var draft=new PersonDetectionDraft();
            var failures=new List<string>();
            int? body=null,face=null;int[] slots;
            object encoded,pose;
            if(args.TryGetValue("mesh",out encoded)&&encoded is string)
            {
                try{
                    var found=PortraitAssets.Save(ticket.Hash,ticket.Region.MapFace((string)encoded),draft);
                    var merged=PortraitAssets.Merge(before,found,out slots,draft);
                    if(!PortraitAssets.CanReplace(before,merged,locks,draft))throw new InvalidOperationException("人脸定位与原有调整不一致。");
                    face=slots[0];
                    if(PortraitAssets.Hash(before)!=PortraitAssets.Hash(merged))foreach(var p in merged)patch[p.Key]=combined[p.Key]=p.Value;
                }catch(InvalidDataException e){failures.Add(e.Message);}
                catch(InvalidOperationException e){failures.Add(e.Message);}
            }
            if(args.TryGetValue("pose",out pose)&&pose is Dictionary<string,object>)
            {
                try{
                    var found=BodyAssets.Save(ticket.Hash,ticket.Region.MapBody(Map(pose)),draft);
                    var merged=BodyAssets.Merge(before,found,locks,out slots,draft);
                    if(!BodyAssets.CanReplace(before,merged,locks,draft))throw new InvalidOperationException("人体定位与原有调整不一致。");
                    body=slots[0];
                    if(BodyAssets.ResourceHash(before)!=BodyAssets.ResourceHash(merged))foreach(var p in merged)patch[p.Key]=combined[p.Key]=p.Value;
                }catch(InvalidDataException e){failures.Add(e.Message);}
                catch(InvalidOperationException e){failures.Add(e.Message);}
            }
            if(!face.HasValue&&!body.HasValue)throw new InvalidOperationException("框内没有足够可靠的人脸或人体。"+String.Join(" ",failures));
            var bodyScene=BodyAssets.ResolveScene(combined,draft);
            var portraitScene=PortraitAssets.Resolve(combined,draft);
            var target=PhotoPeople.Describe(bodyScene,portraitScene).SingleOrDefault(p=>(!face.HasValue||p.faceIndex==face)&&(!body.HasValue||p.bodyIndex==body));
            if(target==null)throw new InvalidOperationException("框内的人脸与身体无法确定属于同一个人，请缩小范围重试。");
            ticket.Patch=patch;ticket.Person=target.index;ticket.Draft=draft;
            return new {person=target.index,face=face.HasValue,body=body.HasValue,changed=patch.Count>0,
                box=body.HasValue?bodyScene.Subjects[body.Value].Box:portraitScene.Faces[face.Value].Box};
        }

        private async Task<object> PersonRegionSave(Dictionary<string,object> args)
        {
            var ticket=await CurrentRegion(args);
            if(ticket.Patch==null)throw new InvalidOperationException("请先预览框选人物的检测结果。");
            regionTicket=null;
            ticket.Draft.StoreSelected(ticket.Patch);
            var state=ticket.Patch.Count==0?Snapshot():Map(CommitPatch(args,ticket.Patch,"框选补检人物"));
            state["targetPerson"]=ticket.Person;
            return state;
        }
    }
}
