using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AIPhotoStudio
{
    internal sealed partial class StudioBridge
    {
        internal sealed class ExportSettings
        {
            internal string Format,Matte;
            internal ExportCanvas Canvas;
            internal int LongEdge,Quality,MaxBytes,MinEdge,MeasuredBytes;
            internal string Extension { get { return Format=="png"?".png":Format=="tiff"?".tif":".jpg"; } }
            internal string Filter { get {
                if(Format=="png")return "16 位 PNG|*.png";
                if(Format=="tiff")return "16 位 TIFF|*.tif;*.tiff";
                if(Format=="jpeg")return "JPEG 照片|*.jpg;*.jpeg";
                return "JPEG 照片|*.jpg|PNG 图像|*.png|TIFF 高精度图像|*.tif";
            } }
            internal string EnglishFilter { get {
                if(Format=="png")return "16-bit PNG|*.png";
                if(Format=="tiff")return "16-bit TIFF|*.tif;*.tiff";
                if(Format=="jpeg")return "JPEG photo|*.jpg;*.jpeg";
                return "JPEG photo|*.jpg|PNG image|*.png|High-precision TIFF|*.tif";
            } }
            internal static int ReadInteger(Dictionary<string,object> args,string key,int fallback,int min,int max)
            {
                object raw;
                if(!args.TryGetValue(key,out raw))return fallback;
                double n=StudioWorkspace.Number(raw);
                if(n!=Math.Truncate(n)||n<min||n>max)throw new ArgumentException("导出设置超出允许范围，字段 "+key+"。");
                return (int)n;
            }
            internal static ExportSettings Read(Dictionary<string,object> args)
            {
                string format=args.ContainsKey("format")?TextOf(args,"format"):null;
                if(format!=null&&format!="jpeg"&&format!="png"&&format!="tiff")throw new ArgumentException("请选择 JPEG、PNG 或 TIFF。");
                string matte=TextOf(args,"matte");
                if(String.IsNullOrEmpty(matte))matte="#ffffff";
                if(!System.Text.RegularExpressions.Regex.IsMatch(matte,@"\A#[0-9A-Fa-f]{6}\z"))throw new ArgumentException("JPEG 底色须为六位 RGB 颜色。");
                return new ExportSettings{Format=format,LongEdge=ReadInteger(args,"longEdge",0,0,65535),Quality=ReadInteger(args,"quality",95,1,100),
                    MaxBytes=format=="jpeg"?ReadInteger(args,"maxBytes",0,0,500000000):0,MinEdge=format=="jpeg"?ReadInteger(args,"minEdge",0,0,65535):0,
                    MeasuredBytes=format=="jpeg"?ReadInteger(args,"measuredBytes",0,0,500000000):0,Matte=matte,Canvas=ExportCanvas.Read(args)};
            }
            internal bool Accepts(string target)
            {
                string ext=Path.GetExtension(target).ToLowerInvariant();
                return Format==null || ext==Extension || Format=="jpeg"&&(ext==".jpeg"||ext==".jpe") || Format=="tiff"&&ext==".tiff";
            }
        }
        private object ExportResult(List<string> paths, List<string> errors)
        {
            return new { count = paths.Count, paths = paths, errors = errors };
        }
        private CancellationTokenSource jpegPlanStop;
        private string jpegPlanTicket;
        private readonly object jpegPlanGate=new object();
        private bool CancelJpegPlan(string ticket)
        {
            lock(jpegPlanGate){
                if(jpegPlanStop==null || jpegPlanTicket!=ticket)return false;
                jpegPlanStop.Cancel();return true;
            }
        }
        private async Task<object> PlanJpeg(Dictionary<string,object> args)
        {
            string id=TextOf(args,"id");long revision=Convert.ToInt64(args["revision"]);
            var settings=ExportSettings.Read(args);
            var values=work.PreviewValues(id,revision,null,true);
            string path=work.PathFor(id);
            using(var job=CancellationTokenSource.CreateLinkedTokenSource(closing.Token)){
                lock(jpegPlanGate){
                    if(jpegPlanStop!=null)jpegPlanStop.Cancel();
                    jpegPlanStop=job;jpegPlanTicket=TextOf(args,"ticket");
                }
                try{
                    var choices=await Task.Run(()=>Imaging.JpegOptions(path,values,settings,job.Token),job.Token);
                    work.VerifyPreviewRevision(id,revision);return choices;
                }finally{lock(jpegPlanGate){if(jpegPlanStop==job){jpegPlanStop=null;jpegPlanTicket=null;}}}
            }
        }
        private async Task<object> ExportOne(string id,ExportSettings settings,long? revision)
        {
            string path = work.PathFor(id);
            var output = new List<string>();
            var details = new List<ExportMeasure>();
            using (var dialog = new SaveFileDialog
            {
                Title = L("另存修图成品","Save edited photo as"), Filter = L(settings.Filter,settings.EnglishFilter),DefaultExt=settings.Format==null?"":settings.Extension.TrimStart('.'),
                FileName = Path.GetFileNameWithoutExtension(path) + L("-编辑","-edited"), AddExtension = true, OverwritePrompt = false
            })
            {
                if (dialog.ShowDialog(owner) == DialogResult.OK)
                {
                    string target = dialog.FileName;
                    if(!settings.Accepts(target))throw new ArgumentException("文件后缀与所选导出格式不一致，请重新选择文件名。");
                    path=work.PathFor(id);
                    var values=revision.HasValue?work.PreviewValues(id,revision.Value,null,true):work.ValuesFor(id);
                    var detail=await Task.Run(()=>Imaging.WriteOutput(path,target,()=>Imaging.ShareCanvas(path,values,settings,closing.Token),closing.Token,
                        settings.Canvas==null?settings.LongEdge:0,settings.Quality,null,"srgb",null,f=>{
                            if(revision.HasValue)work.VerifyPreviewRevision(id,revision.Value);
                            work.EnsureNoPrivacyDraft(id);
                        },
                        settings.MaxBytes,settings.Canvas==null?settings.MinEdge:0,settings.Matte,settings.MeasuredBytes));
                    work.StoreSingleExport(id,values,target,detail);
                    details.Add(detail);
                    output.Add(target);
                }
            }
            return new {count=output.Count,paths=output,errors=new string[0],files=details};
        }
        internal sealed class BatchRun
        {
            internal string Id,File="";
            internal string[] Photos;
            internal int Total,Written,Failed;
            internal bool Started;
            internal CancellationTokenSource Stop;
        }
        private readonly object batchGate=new object();
        private BatchRun batchRun;
        internal BatchRun BeginBatch(string[] ids,string token)
        {
            var photos=ids.Distinct(StringComparer.Ordinal).ToArray();
            work.EnsureNoPrivacyDrafts(photos);
            return BeginBatch(photos,photos.Length,token);
        }
        private BatchRun BeginBatch(string[] photos,int total,string token)
        {
            if(token.Length>128)throw new ArgumentException("批次编号过长。");
            if(total<1)throw new ArgumentException("请先选择要导出的照片。");
            lock(batchGate){
                if(batchRun!=null)throw new InvalidOperationException("已有一批照片正在导出。");
                return batchRun=new BatchRun{Id=token.Length==0?Guid.NewGuid().ToString("N"):token,Photos=photos,Total=total,
                    Stop=CancellationTokenSource.CreateLinkedTokenSource(closing.Token)};
            }
        }
        internal object BatchStatus(string token,bool cancel)
        {
            lock(batchGate){
                var job=batchRun;
                if(job==null||job.Id!=token)return new {active=false};
                if(cancel)job.Stop.Cancel();
                return new {active=true,started=job.Started,total=job.Total,written=job.Written,failed=job.Failed,
                    file=job.File,cancelling=job.Stop.IsCancellationRequested};
            }
        }
        internal void EndBatch(BatchRun job)
        {
            lock(batchGate){if(batchRun==job)batchRun=null;job.Stop.Dispose();}
        }
        private async Task<object> BatchExport(string[] ids,ExportSettings settings,string token)
        {
            var job=BeginBatch(ids,token);
            try{
                using(var dialog=new FolderBrowserDialog{Description=L("选择批量导出文件夹，已有文件将被保留","Choose a batch export folder. Existing files will be kept."),ShowNewFolderButton=true}){
                    if(dialog.ShowDialog(owner)!=DialogResult.OK)return ExportResult(new List<string>(),new List<string>());
                    string dir=dialog.SelectedPath;
                    return await Task.Run(()=>WriteBatch(job,settings,dir));
                }
            }finally{EndBatch(job);}
        }
        internal object WriteBatch(BatchRun job,ExportSettings settings,string dir)
        {
            var outputs=new List<string>();var errors=new List<string>();var details=new List<ExportMeasure>();
            var token=job.Stop.Token;
            lock(batchGate)job.Started=true;
            foreach (string id in job.Photos)
            {
                if(token.IsCancellationRequested)break;
                string path=null;
                try
                {
                    path=work.PathFor(id);
                    lock(batchGate)job.File=Path.GetFileName(path);
                    string target=ChooseNewName(dir,Path.GetFileNameWithoutExtension(path)+L("-编辑","-edited"),settings.Extension);
                    var values=work.ValuesFor(id);
                    details.Add(Imaging.WriteOutput(path,target,()=>Imaging.Render(path,values,0,token),token,settings.LongEdge,settings.Quality,
                        null,"srgb",null,f=>work.EnsureNoPrivacyDraft(id),settings.MaxBytes,settings.MinEdge,settings.Matte));
                    outputs.Add(target);
                }
                catch(OperationCanceledException){if(!token.IsCancellationRequested)throw;break;}
                catch(Exception ex){errors.Add((path==null?id:Path.GetFileName(path))+" · "+LocalMessage(ex.GetBaseException().Message,"batchExport"));}
                lock(batchGate){job.Written=outputs.Count;job.Failed=errors.Count;}
            }
            return new {count=outputs.Count,paths=outputs,files=details,errors=errors,cancelled=token.IsCancellationRequested,total=job.Photos.Length};
        }

        internal static string ChooseNewName(string dir,string stem,string ext)
        {
            string candidate = Path.Combine(dir,stem+ext);
            for (int i=2; File.Exists(candidate) || Directory.Exists(candidate); i++)
                candidate = Path.Combine(dir,stem+"-"+i+ext);
            return candidate;
        }

        private object BatchApply(Dictionary<string,object> args)
        {
            Selected(args);
            return Snapshot(work.CopySettingsToMany(TextOf(args,"id"),Convert.ToInt64(args["revision"]),Strings(args["ids"])));
        }

        private object RecipeFile(Dictionary<string,object> args, bool import)
        {
            Selected(args);
            if (import)
            {
                using (var open = new OpenFileDialog { Title = L("导入编辑配方","Import editing recipe"), Filter = L("AIPhotoStudio 配方|*.aiprecipe|JSON|*.json","AIPhotoStudio recipe|*.aiprecipe|JSON|*.json"), CheckFileExists = true })
                {
                    if (open.ShowDialog(owner) != DialogResult.OK) return Snapshot();
                    if (new FileInfo(open.FileName).Length > 8 * 1024 * 1024) throw new InvalidDataException("配方超过 8 MB。");
                    args["recipe"] = UnpackRecipe(Json().DeserializeObject(File.ReadAllText(open.FileName,Encoding.UTF8)));
                    if(!args.ContainsKey("mode"))args["mode"]="apply";
                    return Snapshot(work.Dispatch("importRecipe",args));
                }
            }
            using (var save = new SaveFileDialog { Title = L("导出编辑配方","Export editing recipe"), Filter = L("AIPhotoStudio 配方|*.aiprecipe","AIPhotoStudio recipe|*.aiprecipe"),
                FileName = Path.GetFileNameWithoutExtension(work.PathFor(TextOf(args,"id"))) + ".aiprecipe", OverwritePrompt = false })
            {
                if (save.ShowDialog(owner) == DialogResult.OK)
                {
                    string json = Json().Serialize(PackageRecipe(TextOf(args,"id")));
                    if(Encoding.UTF8.GetByteCount(json)>8*1024*1024)throw new InvalidDataException("完整配方超过 8 MB，无法写出可再次导入的文件。");
                    using (var stream = new FileStream(save.FileName, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    using (var writer = new StreamWriter(stream,new UTF8Encoding(false))) writer.Write(json);
                }
            }
            return Snapshot();
        }
    }
}
