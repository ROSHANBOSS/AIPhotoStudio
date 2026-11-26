using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;

namespace AIPhotoStudio
{
    internal sealed partial class StudioBridge
    {
        private readonly SemaphoreSlim viewportSlot=new SemaphoreSlim(1);
        private readonly CancellationTokenSource[] viewportJobs=new CancellationTokenSource[4];
        private string fullViewKey;
        private BitmapSource fullView;
        private Imaging.CameraViewport cameraView;
        private readonly Imaging.DragCache detailedStages=new Imaging.DragCache(true);

        private async Task<object> CameraPoster(Dictionary<string,object> args)
        {
            string id=TextOf(args,"id");
            long revision=Convert.ToInt64(args["revision"]);
            var values=work.PreviewValues(id,revision,null,false);
            string path=work.PathFor(id),kind=Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
            if(!VendorRaw.Supports(kind)||CameraRawModes.Selected(values)!=0)return null;
            var job=CancellationTokenSource.CreateLinkedTokenSource(closing.Token);
            lock(selectedPreviews)selectedPreviews.Add(job,id);
            try
            {
                var frame=await Task.Run<object>(delegate
                {
                    var source=VendorRaw.Read(path,"poster",768,job.Token).Surface;
                    return source==null?null:EncodeFrame(source,true,job.Token);
                },job.Token).ConfigureAwait(false);
                work.VerifyPreviewRevision(id,revision);
                return frame;
            }
            finally {lock(selectedPreviews)selectedPreviews.Remove(job);job.Dispose();}
        }

        private static int ViewNumber(Dictionary<string,object> args,string key)
        {
            object value;
            if(!args.TryGetValue(key,out value) || !(value is int || value is long))throw new ArgumentException("查看区域需要整数像素坐标。");
            return Convert.ToInt32(value,CultureInfo.InvariantCulture);
        }

        private async Task<object> PreviewViewport(Dictionary<string,object> args)
        {
            string id=TextOf(args,"id"),path=work.PathFor(id);
            long revision=Convert.ToInt64(args["revision"]);
            var rect=new Int32Rect(ViewNumber(args,"x"),ViewNumber(args,"y"),ViewNumber(args,"width"),ViewNumber(args,"height"));
            if(rect.Width<1||rect.Height<1||rect.Width>6144||rect.Height>6144||(long)rect.Width*rect.Height>5000000)
                throw new ArgumentException("原像素查看区域过大。");
            bool original=args.ContainsKey("original")&&args["original"] is bool&&(bool)args["original"];
            bool source=args.ContainsKey("sourceView")&&args["sourceView"] is bool&&(bool)args["sourceView"];
            bool reviewing=original&&source&&args.ContainsKey("reviewSource");
            int bypass=original?-1:BypassedMask(args);
            var values=reviewing?work.SetPhotoAt(args).Values:work.PreviewValues(id,revision,null,false);
            values=LocalEdits.PreviewWithoutMask(values,bypass);
            if(original)values=FrameGeometry.NeutralValues(values,!source);
            else if(source)values=FrameGeometry.SourceValues(values);
            int lane=reviewing?(TextOf(args,"reviewPanel")=="adjacent"?3:2):(original?1:0);
            var job=CancellationTokenSource.CreateLinkedTokenSource(closing.Token);
            lock(selectedPreviews)
            {
                if(viewportJobs[lane]!=null)viewportJobs[lane].Cancel();
                viewportJobs[lane]=job;
                selectedPreviews.Add(job,id);
            }
            try
            {
                var token=job.Token;
                await viewportSlot.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    var response=await Task.Run<object>(delegate
                    {
                        // Hold an actual read handle for identity checks and all lazy WIC copies.
                        using(ImagingInput.OpenRead(path))
                        {
                            if(args.ContainsKey("reviewSource")&&BodyAssets.SourceDigest(path)!=TextOf(args,"reviewSource"))
                                throw new InvalidOperationException("原片已经改变，请重新分析眼睛位置。");
                            BitmapSource image;
                            var watch=System.Diagnostics.Stopwatch.StartNew();
                            long cameraWait=0,regionMs=0;
                            Imaging.CameraViewport usedCamera=null;
                            if(Imaging.CanReadViewport(values))
                            {
                                if(!original)detailedStages.Clear();
                                string kind=ImagingInput.TypeFromExtension(path);
                                if(kind=="DNG"||VendorRaw.Supports(kind))
                                {
                                    int track=CameraRawModes.Selected(values);
                                    string key="camera/"+id+"/"+track+"/"+BodyAssets.SourceDigest(path);
                                    var camera=fullViewKey==key?cameraView:null;
                                    if(camera==null)
                                    {
                                        fullView=null;cameraView=null;fullViewKey=null;
                                        camera=Imaging.LoadCameraViewport(path,track,token);
                                        token.ThrowIfCancellationRequested();
                                    }
                                    if(original)detailedStages.TrimTo(Imaging.FullViewportCacheBytes-Math.Min(Imaging.CameraViewport.Budget,(long)camera.Width*camera.Height*8));
                                    usedCamera=camera;cameraWait=watch.ElapsedMilliseconds;
                                    image=Imaging.ReadEditedViewport(path,values,rect,token,camera);
                                    regionMs=watch.ElapsedMilliseconds-cameraWait;
                                    token.ThrowIfCancellationRequested();
                                    if(camera.RetainedBytes<=Imaging.CameraViewport.Budget){cameraView=camera;fullViewKey=key;}
                                }
                                else image=Imaging.ReadViewport(path,values,rect,token);
                            }
                            else if(!FrameGeometry.Active(values)&&!ValuedLut(values)&&LocalEdits.SupportsToneRegion(values))
                            {
                                fullView=null;cameraView=null;fullViewKey=null;
                                var stages=new Imaging.DragStages(detailedStages,path+"|"+BodyAssets.SourceDigest(path)+"|"+GpuColorPass.CpuOnly,values);
                                image=Imaging.Render(path,values,0,token,null,null,stages,rect);
                            }
                            else
                            {
                                detailedStages.Clear();
                                string key=id+"/"+revision+"/"+original+"/"+source+"/"+bypass+"/"+BodyAssets.SourceDigest(path);
                                var surface=fullViewKey==key?fullView:null;
                                if(surface==null)
                                {
                                    fullView=null;cameraView=null;fullViewKey=null;
                                    surface=Imaging.Render(path,values,0,token);
                                    token.ThrowIfCancellationRequested();
                                    if((long)surface.PixelWidth*surface.PixelHeight*8<=256L*1024*1024){fullView=surface;fullViewKey=key;}
                                }
                                else
                                {
                                    BodyAssets.ValidateFile(path,values);
                                    PortraitAssets.ValidateFile(path,values);
                                    if(LocalEdits.Hash(values)!=null)LocalEdits.ValidateSource(LocalEdits.ReadSet(values),path);
                                    if(SegmentAssets.Hash(values)!=null)SegmentAssets.Read(values);
                                }
                                Imaging.CheckViewport(rect,surface.PixelWidth,surface.PixelHeight);
                                image=new CroppedBitmap(surface,rect);image.Freeze();
                            }
                            var encoded=EncodeFrame(image,false,token,original?0:ClippingDisplay.Flags(args));
                            if(usedCamera!=null)encoded["cameraTiming"]=new {reusedOverview=usedCamera.OverviewReused,sensorDevelopMs=usedCamera.SensorDevelopMs,
                                sourceWaitMs=cameraWait,regionMs=regionMs,encodeMs=watch.ElapsedMilliseconds-cameraWait-regionMs,retainedBytes=usedCamera.RetainedBytes};
                            return encoded;
                        }
                    },token).ConfigureAwait(false);
                    if(reviewing)work.CheckSetPhoto(id,revision);else work.VerifyPreviewRevision(id,revision);
                    return response;
                }
                finally {viewportSlot.Release();}
            }
            finally
            {
                lock(selectedPreviews){selectedPreviews.Remove(job);if(viewportJobs[lane]==job)viewportJobs[lane]=null;}
                job.Dispose();
            }
        }
        private static bool ValuedLut(IDictionary<string,double> values)
        {
            double strength;
            return values.TryGetValue("lut_strength",out strength)&&strength!=0&&values.Any(p=>p.Key.StartsWith("lut_ref",StringComparison.Ordinal)&&p.Value!=0);
        }
    }
}
