using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AIPhotoStudio
{
    internal sealed partial class StudioBridge
    {
        private CancellationTokenSource proofStop;
        private string proofTicket;
        private Imaging.JpegProof proofFrame;
        private bool ReleaseProof(string ticket)
        {
            // Host actions and continuations run on the owning WinForms dispatcher.
            if(ticket!=proofTicket)return false;
            if(proofStop!=null)proofStop.Cancel();
            proofTicket=null;proofFrame=null;return true;
        }
        private async Task<object> PrepareShareProof(Dictionary<string,object> args)
        {
            string ticket=TextOf(args,"ticket"),id=TextOf(args,"id");
            long revision=Convert.ToInt64(args["revision"]);
            if(String.IsNullOrEmpty(ticket)||ticket.Length>128)throw new ArgumentException("对照请求无效。");
            var settings=ExportSettings.Read(args);
            if(settings.Format!="jpeg")throw new ArgumentException("压缩对照只适用于 JPEG。");
            var values=work.PreviewValues(id,revision,null,true);string path=work.PathFor(id);
            ReleaseProof(proofTicket);proofTicket=ticket;
            using(var stop=CancellationTokenSource.CreateLinkedTokenSource(closing.Token)){
                proofStop=stop;
                try{
                    var result=await Task.Run(()=>Imaging.PrepareProof(path,values,settings,stop.Token),stop.Token);
                    stop.Token.ThrowIfCancellationRequested();work.VerifyPreviewRevision(id,revision);
                    proofFrame=result;
                    return new {image=result.Overview,width=result.Before.PixelWidth,height=result.Before.PixelHeight};
                }finally{if(proofStop==stop)proofStop=null;}
            }
        }
        private async Task<object> ShareProofSample(Dictionary<string,object> args)
        {
            string id=TextOf(args,"id"),ticket=TextOf(args,"ticket");long revision=Convert.ToInt64(args["revision"]);
            work.VerifyPreviewRevision(id,revision);var frame=proofFrame;
            if(frame==null||proofTicket!=ticket)throw new InvalidOperationException("请重新打开压缩对照。");
            double x=ExportCanvas.Fraction(args,"x"),y=ExportCanvas.Fraction(args,"y");
            var sample=await Task.Run(()=>frame.Sample(x,y,closing.Token),closing.Token);
            work.VerifyPreviewRevision(id,revision);
            if(proofTicket!=ticket)throw new InvalidOperationException("压缩对照已改变。");
            return sample;
        }

        private async Task<object> CopyFinalImage(Dictionary<string,object> args)
        {
            string id=TextOf(args,"id");long revision=Convert.ToInt64(args["revision"]);
            work.EnsureNoPrivacyDrafts(new[]{id});
            var values=work.PreviewValues(id,revision,null,true);string source=work.PathFor(id);
            var image=await Task.Run(()=>Imaging.Render(source,values,0,closing.Token),closing.Token);
            using(var payload=await Task.Run(()=>new ClipboardPicture(image),closing.Token)){
                work.VerifyPreviewRevision(id,revision);
                work.EnsureNoPrivacyDrafts(new[]{id});
                payload.Copy();
            }
            return new {width=image.PixelWidth,height=image.PixelHeight};
        }

        private async Task<object> ReadLastExport(Dictionary<string,object> args,bool open)
        {
            string id=TextOf(args,"id");long revision=Convert.ToInt64(args["revision"]);
            var values=work.PreviewValues(id,revision,null,true);
            var last=work.LastSingleExport(id);if(last==null)return null;
            var info=new FileInfo(last.path);
            bool available=info.Exists && info.Length==last.bytes && info.LastWriteTimeUtc.Ticks==last.stamp;
            if(open){
                work.EnsureNoPrivacyDrafts(new[]{id});
                if(!available)throw new FileNotFoundException("原位置的成品已移动、删除或被替换。");
                string kind=ImagingInput.TypeFromExtension(last.path);
                if(kind!="JPEG"&&kind!="PNG"&&kind!="TIFF")throw new InvalidDataException("成品格式无效。");
                // Open the actual exported bytes inside the application without importing them into the library.
                var preview=await Task.Run(()=>EncodeFrame(Imaging.Render(last.path,null,1440,closing.Token),false,closing.Token),closing.Token);
                work.VerifyPreviewRevision(id,revision);work.EnsureNoPrivacyDrafts(new[]{id});return preview;
            }
            return new {path=last.path,bytes=last.bytes,width=last.width,height=last.height,available,
                changed=last.recipe!=StudioWorkspace.ShareRecipeKey(values)};
        }
    }

    internal sealed class ClipboardPicture : IDisposable
    {
        private readonly MemoryStream png=new MemoryStream(),dib=new MemoryStream();
        internal ClipboardPicture(BitmapSource image)
        {
            // Registered PNG preserves straight alpha; CF_DIBV5 also supplies bitmap-only Windows applications.
            var bgra=new FormatConvertedBitmap(image,PixelFormats.Bgra32,null,0);
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bgra));encoder.Save(png);png.Position=0;
            int w=bgra.PixelWidth,h=bgra.PixelHeight,stride=checked(w*4);
            var writer=new BinaryWriter(dib);
            writer.Write(124);writer.Write(w);writer.Write(-h);writer.Write((ushort)1);writer.Write((ushort)32);
            writer.Write(3);writer.Write(checked(stride*h));writer.Write(3780);writer.Write(3780);
            writer.Write(0);writer.Write(0);writer.Write(0x00ff0000);writer.Write(0x0000ff00);writer.Write(0x000000ff);writer.Write(0xff000000u);
            writer.Write(0x73524742);writer.Write(new byte[48]);writer.Write(4);writer.Write(new byte[12]);
            int rows=Math.Min(h,Math.Max(1,1048576/stride));var block=new byte[rows*stride];
            for(int y=0;y<h;y+=rows){
                int n=Math.Min(rows,h-y);
                bgra.CopyPixels(new System.Windows.Int32Rect(0,y,w,n),block,stride,0);dib.Write(block,0,n*stride);
            }
            dib.Position=0;
        }
        internal void Copy()
        {
            var data=new DataObject();
            data.SetData("PNG",false,png);
            data.SetData(DataFormats.GetFormat(17).Name,false,dib);
            Clipboard.SetDataObject(data,true,5,80);
        }
        public void Dispose(){png.Dispose();dib.Dispose();}
    }

    internal sealed class SingleExport
    {
        public string path,recipe;
        public long bytes,stamp;
        public int width,height;
    }
    public sealed partial class StudioWorkspace
    {
        internal static string ShareRecipeKey(IDictionary<string,double> values)
        {
            var text=new StringBuilder(Imaging.EngineVersion);
            foreach(var pair in values.OrderBy(p=>p.Key,StringComparer.Ordinal)){
                text.Append('|').Append(pair.Key).Append('=').Append(pair.Value.ToString("R",CultureInfo.InvariantCulture));
            }
            using(var hash=SHA256.Create())return Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(text.ToString())));
        }
        internal void StoreSingleExport(string id,IDictionary<string,double> values,string path,ExportMeasure detail)
        {
            var item=new SingleExport{path=path,width=detail.width,height=detail.height,bytes=detail.bytes,
                stamp=File.GetLastWriteTimeUtc(path).Ticks,recipe=ShareRecipeKey(values)};
            lock(gate)db.Run("INSERT OR REPLACE INTO settings(name,value) VALUES(?,?)","single-export/v1/"+id,json.Serialize(item));
        }
        internal SingleExport LastSingleExport(string id)
        {
            lock(gate){
                string data=(string)db.Scalar("SELECT value FROM settings WHERE name=?","single-export/v1/"+id);
                return data==null?null:json.Deserialize<SingleExport>(data);
            }
        }
    }
}
