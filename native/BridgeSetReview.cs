using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;

namespace AIPhotoStudio
{
    internal sealed partial class StudioBridge
    {
        private sealed class ReviewTicket
        {
            internal string Token;
            internal StudioWorkspace.SetPhoto Photo;
            internal SetEvidence Evidence;
            internal SetReviewPixels Pixels;
            internal int Width,Height;
        }
        private readonly object reviewGate=new object();
        private ReviewTicket reviewTicket;

        private static string Hash64(string hex)
        {
            var bytes=new byte[32];
            for(int i=0;i<bytes.Length;i++) bytes[i]=Convert.ToByte(hex.Substring(2*i,2),16);
            return Convert.ToBase64String(bytes);
        }

        private async Task<object> InspectSetPhoto(Dictionary<string,object> args)
        {
            var photo=work.SetPhotoAt(args);
            await renderSlots.WaitAsync(closing.Token);
            try { return await Task.Run<object>(delegate {
                string source=BodyAssets.SourceDigest(photo.Path),mesh=PortraitAssets.Hash(photo.Values);
                int frame=CameraRawModes.Selected(photo.Values);
                var saved=work.ReviewEvidenceFor(photo.Id);
                if(saved!=null && saved.version==SetReviewPixels.Version && saved.source==source && saved.frame==frame &&
                    saved.mesh==mesh && saved.faces!=null && saved.composition!=null && saved.quality!=null && saved.quality.Length==3 &&
                    saved.detection!="pending" && saved.detection!="unavailable") {
                    work.CheckSetPhoto(photo.Id,photo.Revision);
                    return new {evidence=saved};
                }
                var evidence=new SetEvidence {version=SetReviewPixels.Version,source=source,mesh=mesh,frame=frame};
                var input=Imaging.Render(photo.Path,CameraRawModes.Neutral(photo.Values),1200,closing.Token);
                var pixels=new SetReviewPixels(input);
                var scene=PortraitAssets.Resolve(photo.Values);
                bool stored=scene!=null && scene.SourceHash==Hash64(source);
                pixels.Measure(evidence,stored?scene.Faces:new FaceMesh[0],stored?"saved":"pending");
                if(BodyAssets.SourceDigest(photo.Path)!=source) throw new InvalidOperationException("分析期间原片发生变化，请重新分析。");
                work.SaveSetEvidence(photo,evidence);
                if(stored) return new {evidence=evidence};
                var ticket=new ReviewTicket {Token=Guid.NewGuid().ToString("N"),Photo=photo,Evidence=evidence,Pixels=pixels,
                    Width=input.PixelWidth,Height=input.PixelHeight};
                lock(reviewGate) reviewTicket=ticket;
                return new {evidence=evidence,token=ticket.Token,image=EncodeFrame(input,false,closing.Token)["image"]};
            }); } finally {renderSlots.Release();}
        }

        private async Task<object> ReadReviewExpression(Dictionary<string,object> args)
        {
            var photo=work.SetPhotoAt(args);
            var evidence=work.ReviewEvidenceFor(photo.Id);
            string source=TextOf(args,"source"),key=TextOf(args,"key");
            var face=evidence==null||evidence.faces==null?null:evidence.faces.FirstOrDefault(f=>f.key==key);
            if(face==null || evidence.source!=source || evidence.frame!=CameraRawModes.Selected(photo.Values))
                throw new InvalidOperationException("人脸位置已经更新，请重新选择。");
            await renderSlots.WaitAsync(closing.Token);
            try {return await Task.Run<object>(delegate {
                using(ImagingInput.OpenRead(photo.Path)) {
                    if(BodyAssets.SourceDigest(photo.Path)!=source)throw new InvalidOperationException("原片已经改变，请重新分析人脸。");
                    var view=Imaging.Render(photo.Path,CameraRawModes.Neutral(photo.Values),1200,closing.Token);
                    var c=face.centers;int w=view.PixelWidth,h=view.PixelHeight;
                    double dx=(c[0]-c[2])*w,dy=(c[1]-c[3])*h,span=Math.Max(2,Math.Sqrt(dx*dx+dy*dy));
                    int width=Math.Min(w,(int)Math.Ceiling(span*3.4)),height=Math.Min(h,(int)Math.Ceiling(span*3.5));
                    int x=Math.Max(0,Math.Min(w-width,(int)Math.Round((c[0]+c[2])*w/2-width*.5)));
                    int y=Math.Max(0,Math.Min(h-height,(int)Math.Round((c[1]+c[3])*h/2-height*.38)));
                    var region=new CroppedBitmap(view,new Int32Rect(x,y,width,height));
                    var result=EncodeFrame(region,false,closing.Token);
                    result["region"]=new[]{x,y,width,height,w,h};
                    work.CheckSetPhoto(photo.Id,photo.Revision);
                    return result;
                }
            });} finally {renderSlots.Release();}
        }

        private object SetReviewFaces(Dictionary<string,object> args)
        {
            ReviewTicket ticket;
            lock(reviewGate) {
                ticket=reviewTicket;
                if(ticket==null || ticket.Token!=TextOf(args,"token")) throw new InvalidOperationException("本次人脸分析已过期。");
                reviewTicket=null;
            }
            var photo=ticket.Photo;
            if(BodyAssets.SourceDigest(work.PathFor(photo.Id))!=ticket.Evidence.source)
                throw new InvalidOperationException("原片在识别期间发生变化，请重新分析。");
            var faces=new FaceMesh[0]; bool unavailable=Object.Equals(args.ContainsKey("unavailable")?args["unavailable"]:null,true);
            if(!unavailable) {
                var meshes=Strings(args["meshes"]);
                if(meshes.Length>0) {
                    var draft=new PersonDetectionDraft();
                    var input=new Dictionary<string,object>{{"meshes",meshes},{"width",ticket.Width},{"height",ticket.Height}};
                    var patch=PortraitAssets.Save(Hash64(ticket.Evidence.source),input,draft);
                    faces=PortraitAssets.Resolve(patch,draft).Faces;
                }
            }
            ticket.Pixels.Measure(ticket.Evidence,faces,unavailable?"unavailable":faces.Length==0?"noFace":"local");
            work.SaveSetEvidence(photo,ticket.Evidence);
            return ticket.Evidence;
        }
    }
}
