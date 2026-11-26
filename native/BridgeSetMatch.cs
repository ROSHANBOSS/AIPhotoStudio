using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace AIPhotoStudio
{
    internal sealed partial class StudioBridge
    {
        private sealed class MatchSession
        {
            internal string Token,Reference,Digest;
            internal long Revision;
            internal double[] Goal;
            internal bool Suggested;
            internal string SkinSide;
            internal string ReferenceDetail;
            internal SetMatchScene Scene;
            internal Dictionary<string,SetMatchRegions.Samples> Regions;
            internal readonly HashSet<string> Fitting=new HashSet<string>();
            internal readonly Dictionary<string,StudioWorkspace.SetMatchRow> Rows=new Dictionary<string,StudioWorkspace.SetMatchRow>();
        }
        private readonly object setMatchGate=new object();
        private MatchSession matchSession;
        private readonly Dictionary<string,int> guideFaceCounts=new Dictionary<string,int>();
        private readonly Queue<string> guideFaceOrder=new Queue<string>();

        private static string GuideFaceKey(StudioWorkspace.SetPhoto photo,string digest)
        {
            return photo.Id+"/"+photo.Revision+"/"+CameraRawModes.Selected(photo.Values)+"/"+digest;
        }
        private static string MatchSkinSide(Dictionary<string,object> args)
        {
            string side=TextOf(args,"skinSide");
            return side=="left"||side=="right"?side:null;
        }

        private MatchSession MatchTicket(Dictionary<string,object> args)
        {
            lock(setMatchGate) {
                if(matchSession==null||matchSession.Token!=TextOf(args,"token")||matchSession.Scene==null)
                    throw new InvalidOperationException("整组校色预览已过期，请重新选择基准片。");
                return matchSession;
            }
        }
        private void CurrentMatch(MatchSession session)
        {
            if(matchSession!=session)throw new InvalidOperationException("本次整组校色预览已过期。");
        }
        private int? MatchFaceCount(StudioWorkspace.SetPhoto photo,string digest)
        {
            var mesh=PortraitAssets.Resolve(photo.Values);
            if(mesh!=null&&mesh.SourceHash==Hash64(digest))return mesh.Faces.Length;
            var known=work.ReviewEvidenceFor(photo.Id);
            if(known!=null&&known.version==SetReviewPixels.Version&&known.source==digest&&known.frame==CameraRawModes.Selected(photo.Values)
                &&known.mesh==PortraitAssets.Hash(photo.Values)&&(known.detection=="local"||known.detection=="noFace"||known.detection=="saved"))return known.faceCount;
            int count;
            lock(setMatchGate)if(guideFaceCounts.TryGetValue(GuideFaceKey(photo,digest),out count))return count;
            return null;
        }
        private SetMatchScene SceneForMatch(StudioWorkspace.SetPhoto photo,string digest)
        {
            var neutral=Imaging.Render(photo.Path,FrameGeometry.NeutralValues(photo.Values,true),144,closing.Token);
            return new SetMatchScene(neutral,MatchFaceCount(photo,digest));
        }
        private void VerifyMatchSource(StudioWorkspace.SetPhoto photo,string digest)
        {
            work.CheckSetPhoto(photo.Id,photo.Revision);
            if(BodyAssets.SourceDigest(photo.Path)!=digest)throw new InvalidOperationException("拟合原片已改变，请重新拟合。");
        }

        private async Task<object> SetMatchAreaInput(Dictionary<string,object> args)
        {
            var photo=work.SetPhotoAt(args);
            await renderSlots.WaitAsync(closing.Token);
            try{return await Task.Run<object>(delegate {
                string digest=BodyAssets.SourceDigest(photo.Path);
                bool suggest=Object.Equals(args.ContainsKey("suggest")?args["suggest"]:null,true);
                var image=Imaging.Render(photo.Path,FrameGeometry.SourceValues(photo.Values),suggest?1200:680,closing.Token);
                var display=EncodeFrame(image,false,closing.Token);
                bool reusable=suggest&&SetMatchSuggestions.SavedGeometryFits(photo.Values);
                var faces=reusable?PortraitAssets.Resolve(photo.Values):null;
                var bodies=reusable&&TextOf(args,"purpose")!="hair"?BodyAssets.ResolveScene(photo.Values):null;
                VerifyMatchSource(photo,digest);
                return new {image=display["image"],digest=digest,stored=SetMatchRegions.ExistingAreas(photo.Values),
                    savedFaces=faces!=null&&faces.SourceHash==Hash64(digest),savedBodies=bodies!=null&&bodies.SourceHash==digest};
            });}finally{renderSlots.Release();}
        }

        private async Task<object> SuggestSetMatchAreas(Dictionary<string,object> args)
        {
            var photo=work.SetPhotoAt(args);
            await renderSlots.WaitAsync(closing.Token);
            try{return await Task.Run<object>(delegate {
                string digest=BodyAssets.SourceDigest(photo.Path);
                if(TextOf(args,"pickedDigest")!=digest)throw new InvalidOperationException("原片已改变，请重新打开取样图。");
                bool reusable=SetMatchSuggestions.SavedGeometryFits(photo.Values);
                var scene=reusable?PortraitAssets.Resolve(photo.Values):null;
                var pose=reusable&&TextOf(args,"purpose")!="hair"?BodyAssets.ResolveScene(photo.Values):null;
                if(pose!=null&&pose.SourceHash!=digest)pose=null;
                bool saved=scene!=null&&scene.SourceHash==Hash64(digest);
                var samples=Imaging.Render(photo.Path,FrameGeometry.SourceValues(photo.Values),960,closing.Token);
                if(!saved){
                    scene=null;
                    var meshes=Strings(args.ContainsKey("meshes")?args["meshes"]:null);
                    if(meshes.Length!=0){
                        int w=Convert.ToInt32(args["width"]),h=Convert.ToInt32(args["height"]);
                        if(w<1||h<1||Math.Max(w,h)>1200||Math.Abs(w*(double)samples.PixelHeight-h*(double)samples.PixelWidth)>Math.Max(w,h)*1.5)
                            throw new ArgumentException("人脸识别图的尺寸已经改变。");
                        var draft=new PersonDetectionDraft();
                        var patch=PortraitAssets.Save(Hash64(digest),new Dictionary<string,object>{{"width",w},{"height",h},{"meshes",meshes}},draft);
                        scene=PortraitAssets.Resolve(patch,draft);
                    }
                }
                int focus=args.ContainsKey("focusNumber")?Convert.ToInt32(args["focusNumber"]):0;
                if(focus<0||focus>PortraitControls.MaxFaces)throw new ArgumentException("局部复查的人物编号无效。");
                var suggestions=new SetMatchSuggestions(samples,scene==null?new FaceMesh[0]:scene.Faces,pose,
                    SetMatchSuggestions.Category(args,"skin"),SetMatchSuggestions.Category(args,"background"));
                var hair=focus==0?null:SetMatchSuggestions.Category(args,"hair");
                var result=suggestions.Recommend(saved,pose==null?0:pose.Subjects.Length,focus,hair,GuideFaceKey(photo,digest),
                    hair==null?null:LocalEdits.ReadSet(photo.Values),digest);
                VerifyMatchSource(photo,digest);
                if(focus==0&&!saved&&Object.Equals(args.ContainsKey("facesChecked")?args["facesChecked"]:null,true)&&!result.people.Any(p=>p.uncertain)){
                    string key=GuideFaceKey(photo,digest);
                    lock(setMatchGate){
                        if(!guideFaceCounts.ContainsKey(key))guideFaceOrder.Enqueue(key);
                        guideFaceCounts[key]=result.people.Length;
                        if(guideFaceOrder.Count>24)guideFaceCounts.Remove(guideFaceOrder.Dequeue());
                    }
                }
                return result;
            });}finally{renderSlots.Release();}
        }

        private async Task<object> BeginSetMatch(Dictionary<string,object> args)
        {
            var photo=work.SetPhotoAt(args);var areas=SetMatchRegions.Read(args);
            var session=new MatchSession {Token=Guid.NewGuid().ToString("N"),Reference=photo.Id,Revision=photo.Revision,
                Suggested=areas.Length>0&&Object.Equals(args.ContainsKey("suggested")?args["suggested"]:null,true),SkinSide=MatchSkinSide(args)};
            lock(setMatchGate)matchSession=session;
            await renderSlots.WaitAsync(closing.Token);
            try{return await Task.Run<object>(delegate {
                session.Digest=BodyAssets.SourceDigest(photo.Path);
                CheckPickedDigest(args,session.Digest);
                foreach(var area in areas.Where(a=>a.Hair!=null))area.Hair.Verify(GuideFaceKey(photo,session.Digest));
                if(areas.Length==0)session.Goal=ColorMatch.Describe(Imaging.Render(photo.Path,photo.Values,160,closing.Token));
                else {
                    var samples=Imaging.Render(photo.Path,FrameGeometry.SourceValues(photo.Values),SetMatchRegions.SampleSize(areas),closing.Token);
                    session.Regions=areas.ToDictionary(a=>a.Role,a=>new SetMatchRegions.Samples(samples,a));
                    var skin=areas.FirstOrDefault(a=>a.Role=="skin"||a.Role=="hair");
                    if(skin!=null&&(session.Suggested||skin.Hair!=null)){
                        var detail=samples;
                        if(SetMatchRegions.SampleSize(areas)<960)detail=Imaging.Render(photo.Path,FrameGeometry.SourceValues(photo.Values),960,closing.Token);
                        session.ReferenceDetail=(string)EncodeFrame(SetMatchRegions.Detail(detail,skin),false,closing.Token)["image"];
                    }
                }
                var scene=SceneForMatch(photo,session.Digest);
                var preview=EncodeFrame(Imaging.Render(photo.Path,photo.Values,480,closing.Token),false,closing.Token);
                VerifyMatchSource(photo,session.Digest);
                lock(setMatchGate){CurrentMatch(session);session.Scene=scene;}
                return new {token=session.Token,image=preview["image"]};
            });}finally{renderSlots.Release();}
        }
        private static void CheckPickedDigest(Dictionary<string,object> args,string digest)
        {
            if(args.ContainsKey("regions")&&TextOf(args,"pickedDigest")!=digest)
                throw new InvalidOperationException("取样图对应的原片已改变，请重新打开取样图。");
        }

        private async Task<object> FitSetPhoto(Dictionary<string,object> args)
        {
            var session=MatchTicket(args);var photo=work.SetPhotoAt(args);var areas=SetMatchRegions.Read(args);
            if(photo.Id==session.Reference)throw new ArgumentException("请选择基准以外的照片。");
            if((session.Regions==null)!=(areas.Length==0))throw new ArgumentException("请为基准与目标选择对应的区域。");
            work.CheckSetPhoto(session.Reference,session.Revision);
            lock(setMatchGate){
                CurrentMatch(session);
                if(session.Rows.ContainsKey(photo.Id)||!session.Fitting.Add(photo.Id))
                    throw new InvalidOperationException("该目标已在本组拟合，更改选区请重新开始拟合。");
            }
            try{
                await renderSlots.WaitAsync(closing.Token);
                try{return await Task.Run<object>(delegate {
                    string digest=BodyAssets.SourceDigest(photo.Path);CheckPickedDigest(args,digest);
                    foreach(var area in areas.Where(a=>a.Hair!=null))area.Hair.Verify(GuideFaceKey(photo,digest));
                    var scene=session.Scene.Compare(SceneForMatch(photo,digest));
                    var row=new StudioWorkspace.SetMatchRow{Id=photo.Id,Revision=photo.Revision,Digest=digest,Request=Guid.NewGuid().ToString("N")};
                    double before,after;string[] locks;
                    if(areas.Length==0){
                        row.Patch=ColorMatch.FitSet(photo.Path,session.Goal,photo.Values,photo.Locks,closing.Token,out before,out after);
                        locks=photo.Locks.Intersect(new[]{"exposure","temperature","tint"}).ToArray();
                    }else{
                        row.MaskSource=Hash64(digest);
                        row.Patch=new Dictionary<string,double>();
                        row.NeedsPreview=areas.Any(a=>a.Hair!=null)||session.Suggested||Object.Equals(args.ContainsKey("suggested")?args["suggested"]:null,true);
                        var goals=session.Regions;
                        if(Object.Equals(args.ContainsKey("onlyPaired")?args["onlyPaired"]:null,true))
                            goals=goals.Where(g=>areas.Any(a=>a.Role==g.Key)).ToDictionary(g=>g.Key,g=>g.Value);
                        if(session.SkinSide==null||session.SkinSide!=MatchSkinSide(args))
                            foreach(var a in areas)a.Opposite=null;
                        row.Layers=SetMatchRegions.Fit(photo,digest,areas,goals,row.Patch,scene.Reasons,closing.Token);
                        if(areas.Any(a=>a.Hair!=null)&&row.Patch.Count>0)scene.Reasons.Add("请核对发际线与发色，再勾选本张");
                        var cheek=row.Layers.FirstOrDefault(l=>l.Area.Role=="skin");
                        if(row.NeedsPreview&&cheek!=null){
                            string targetSide=MatchSkinSide(args);
                            if(session.SkinSide!=null&&targetSide!=null&&targetSide!=session.SkinSide)
                                scene.Reasons.Add("两张照片取自不同侧面颊，请核对受光方向");
                            if(SetMatchRegions.Tones.Any(t=>{
                                string k=LocalEdits.ToneKey(cheek.Slot,t);double value;
                                return row.Patch.TryGetValue(k,out value)&&Math.Abs(value-photo.Values[k])>(t=="exposure"?.8:45);
                            }))scene.Reasons.Add("面颊局部调整幅度较大，请核对肤色和周围的过渡");
                        }
                        before=row.Layers.Max(l=>l.Before);after=row.Layers.Max(l=>l.After);
                        locks=row.Layers.SelectMany(l=>SetMatchRegions.Tones.Select(t=>LocalEdits.ToneKey(l.Slot,t))).Intersect(photo.Locks).ToArray();
                    }
                    VerifyMatchSource(photo,digest);work.CheckSetPhoto(session.Reference,session.Revision);
                    if(BodyAssets.SourceDigest(work.PathFor(session.Reference))!=session.Digest)throw new InvalidOperationException("基准原片已改变，请重新拟合。");
                    lock(setMatchGate){CurrentMatch(session);row.Slot=session.Rows.Count;session.Rows.Add(row.Id,row);}
                    return new {id=photo.Id,slot=row.Slot,before=before,after=after,values=row.Patch,locks=locks,
                        review=scene.Reasons.ToArray(),layout=scene.Layout,colorSpread=scene.ColorSpread,needsPreview=row.NeedsPreview,
                        regions=row.Layers==null?null:row.Layers.Select(l=>new{role=l.Area.Role,maskSlot=l.Slot,before=l.Before,after=l.After,
                            oppositeBefore=l.OppositeBefore,oppositeAfter=l.OppositeAfter}).ToArray()};
                });}finally{renderSlots.Release();}
            }finally{lock(setMatchGate)session.Fitting.Remove(photo.Id);}
        }

        private async Task<object> PreviewSetMatch(Dictionary<string,object> args)
        {
            var session=MatchTicket(args);var photo=work.SetPhotoAt(args);StudioWorkspace.SetMatchRow row;
            lock(setMatchGate)if(!session.Rows.TryGetValue(photo.Id,out row))throw new InvalidOperationException("缺少该照片的拟合结果。");
            work.CheckSetPhoto(session.Reference,session.Revision);work.CheckSetPhoto(photo.Id,row.Revision);
            var values=new Dictionary<string,double>(photo.Values);foreach(var p in row.Patch)values[p.Key]=p.Value;
            await renderSlots.WaitAsync(closing.Token);
            try{return await Task.Run<object>(delegate {
                var changedLayers=row.ChangedLayers();
                var draft=changedLayers==null?null:SetMatchRegions.Draft(photo.Values,changedLayers);
                var before=EncodeFrame(Imaging.Render(photo.Path,photo.Values,480,closing.Token),false,closing.Token)["image"];
                var after=EncodeFrame(Imaging.Render(photo.Path,values,480,closing.Token,draft),false,closing.Token)["image"];
                object detail=null;
                var skin=row.NeedsPreview?row.Layers.FirstOrDefault(l=>l.Area.Role=="skin"||l.Area.Role=="hair"):null;
                if(skin!=null){
                    var current=Imaging.Render(photo.Path,FrameGeometry.SourceValues(photo.Values),960,closing.Token);
                    var proposed=Imaging.Render(photo.Path,FrameGeometry.SourceValues(values),960,closing.Token,draft);
                    var actual=changedLayers.Contains(skin)?draft.layers.FirstOrDefault(l=>l.slot==skin.Slot):null;
                    var hair=skin.Area.Hair;
                    detail=new {role=skin.Area.Role,reference=session.ReferenceDetail,before=EncodeFrame(SetMatchRegions.Detail(current,skin.Area),false,closing.Token)["image"],
                        after=EncodeFrame(SetMatchRegions.Detail(proposed,skin.Area),false,closing.Token)["image"],
                        coverage=hair!=null?EncodeFrame(hair.MarkedDetail(current,actual!=null),false,closing.Token)["image"]:
                            skin.Area.Effect==null?null:EncodeFrame(skin.Area.Effect.MarkedDetail(current,actual),false,closing.Token)["image"]};
                }
                VerifyMatchSource(photo,row.Digest);work.CheckSetPhoto(session.Reference,session.Revision);
                if(BodyAssets.SourceDigest(work.PathFor(session.Reference))!=session.Digest)throw new InvalidOperationException("基准原片已改变，请重新拟合。");
                lock(setMatchGate){CurrentMatch(session);row.Previewed=true;}
                return new {before=before,after=after,detail=detail};
            });}finally{renderSlots.Release();}
        }

        private async Task<object> ConfirmSetMatch(Dictionary<string,object> args)
        {
            var session=MatchTicket(args);var bits=Convert.FromBase64String(TextOf(args,"selection"));
            return Snapshot(await Task.Run(delegate {
                lock(setMatchGate){
                    CurrentMatch(session);
                    int count=session.Rows.Count,lastBits=count%8;
                    if(bits.Length>(count+7)/8||(lastBits!=0&&bits.Length==(count+7)/8&&(bits[bits.Length-1]>>lastBits)!=0))
                        throw new ArgumentException("选中的照片尚未拟合。");
                    var rows=session.Rows.Values.Where(row=>row.Slot/8<bits.Length&&(bits[row.Slot/8]&(1<<(row.Slot%8)))!=0).OrderBy(row=>row.Slot).ToList();
                    if(rows.Any(row=>row.NeedsPreview&&!row.Previewed))throw new InvalidOperationException("请先查看所选照片的拟合预览，再确认应用。");
                    return work.CommitSetMatch(session.Reference,session.Revision,session.Digest,rows);
                }
            }));
        }
    }
}
