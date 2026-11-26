using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace AIPhotoStudio
{
    public static partial class Imaging
    {
        // A foreground camera preview already develops the whole sensor. Publish the overview
        // immediately, then retain exact rows from that same worker only when they fit the cap.
        private sealed class OverviewDetail
        {
            internal CacheEntry Input;
            internal readonly CancellationTokenSource Stop=new CancellationTokenSource();
            internal readonly TaskCompletionSource<VendorRaw.Result> Detail=new TaskCompletionSource<VendorRaw.Result>();
            internal readonly TaskCompletionSource<bool> OverviewReady=new TaskCompletionSource<bool>();
            internal bool Published,Retired;
            internal void Retire(){Retired=true;if(Published)Stop.Cancel();}
        }
        internal static void CameraOverviewVisible(string path){
            lock(DetailLock){
                var work=overviewDetail;
                if(work!=null&&String.Equals(path,work.Input.Path,StringComparison.OrdinalIgnoreCase))
                    work.OverviewReady.TrySetResult(true);
            }
        }
        private static readonly object DetailLock=new object();
        private static string detailSelection;
        private static OverviewDetail overviewDetail;

        internal static void SelectCameraDetail(string path)
        {
            lock(DetailLock){
                if(String.Equals(path,detailSelection,StringComparison.OrdinalIgnoreCase))return;
                detailSelection=path;
                if(overviewDetail!=null){overviewDetail.Retire();overviewDetail=null;}
            }
        }
        private static VendorRaw.Result ReadCameraOverview(CacheEntry key,CancellationToken cancel)
        {
            OverviewDetail work=null;
            lock(DetailLock){
                if(String.Equals(key.Path,detailSelection,StringComparison.OrdinalIgnoreCase)&&
                    (overviewDetail==null||!SameCamera(key,overviewDetail.Input))){
                    if(overviewDetail!=null)overviewDetail.Retire();
                    overviewDetail=work=new OverviewDetail{Input=key};
                }
            }
            if(work==null)return VendorRaw.Read(key.Path,"render",key.MaxEdge,cancel,key.RawFrame);
            var preview=new TaskCompletionSource<VendorRaw.Result>();
            Task.Run(delegate{
                using(var combined=CancellationTokenSource.CreateLinkedTokenSource(cancel,work.Stop.Token)){
                    try{
                        var result=VendorRaw.Read(key.Path,"previewDetail",key.MaxEdge,combined.Token,key.RawFrame,0,r=>{
                            preview.TrySetResult(r);
                            // A gallery thumbnail may still be subscribed after another photo is selected.
                            lock(DetailLock){work.Published=true;if(work.Retired)work.Stop.Cancel();}
                            if(Object.Equals(r.Metadata["detailFollows"],true)&&!work.OverviewReady.Task.Wait(3500,combined.Token))
                                work.Stop.Cancel();
                        });
                        work.Detail.TrySetResult(result.Packed==null?null:result);
                    }catch(Exception error){
                        preview.TrySetException(error);
                        work.Detail.TrySetResult(null);
                    }
                }
            });
            return preview.Task.GetAwaiter().GetResult();
        }
        internal static CameraViewport TakeOverviewDetail(string path,int frame,FileStream file,CancellationToken cancel)
        {
            OverviewDetail work;
            lock(DetailLock)work=overviewDetail;
            if(work==null||!String.Equals(path,work.Input.Path,StringComparison.OrdinalIgnoreCase)||frame!=work.Input.RawFrame||
                file.Length!=work.Input.Length||CameraIdentity(file,cancel)!=work.Input.SourceIdentity)return null;
            work.OverviewReady.TrySetResult(true);work.Detail.Task.Wait(cancel);
            lock(DetailLock){
                if(overviewDetail!=work)return null;
                var result=work.Detail.Task.Result;
                if(result==null)return null;
                overviewDetail=null;
                return new CameraViewport(path,frame,result,true){OverviewReused=true};
            }
        }
    }
    public sealed partial class StudioWorkspace
    {
        internal string CameraSelectionPath(string id)
        {
            lock(gate)return (string)db.Scalar("SELECT path FROM photos WHERE id=?",id);
        }
    }
}
