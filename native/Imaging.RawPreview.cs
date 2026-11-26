using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32.SafeHandles;

namespace AIPhotoStudio
{
    public static partial class Imaging
    {
        private sealed class CameraPreview
        {
            internal CacheEntry Input;
            internal BitmapSource Surface;
            internal int W,H,Orientation;
            internal long Bytes {get{return (long)Surface.PixelWidth*Surface.PixelHeight*8;}}
        }
        private sealed class CameraFlight
        {
            internal CacheEntry Input;
            internal CameraPreview Value;
            internal Exception Error;
            internal int Readers;
            internal bool Complete;
            internal Timer Idle;
            internal readonly ManualResetEventSlim Ready=new ManualResetEventSlim(false);
            internal readonly CancellationTokenSource Cancel=new CancellationTokenSource();
        }
        private static readonly object CameraLock=new object();
        private static readonly List<CameraPreview> CameraPreviews=new List<CameraPreview>();
        private static readonly List<CameraFlight> CameraFlights=new List<CameraFlight>();
        private static readonly SemaphoreSlim CameraSlots=new SemaphoreSlim(2);
        private static readonly Lazy<double[]> CameraLinear=new Lazy<double[]>(()=>{
            var result=new double[65536];
            for(int i=0;i<result.Length;i++){double v=i/65535d;result[i]=v<=.04045?v/12.92:Math.Pow((v+.055)/1.055,2.4);}
            return result;
        });
        private static long cameraPreviewDecodes;
        public static long RawPreviewDecodeCount{get{return Interlocked.Read(ref cameraPreviewDecodes);}}
        internal static int RawPreviewActiveReaders{get{lock(CameraLock)return CameraFlights.Sum(job=>job.Readers);}}
        private const long CameraPreviewBudget=96L*1024*1024;
        [StructLayout(LayoutKind.Sequential)]
        private struct CameraFileInfo
        {
            internal uint Attributes,CreateLow,CreateHigh,AccessLow,AccessHigh,WriteLow,WriteHigh,Volume,SizeHigh,SizeLow,Links,IndexHigh,IndexLow;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct CameraFileTimes
        {
            internal long Created,Accessed,Written,Changed;
            internal uint Attributes;
        }
        [DllImport("kernel32.dll",SetLastError=true)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle handle,out CameraFileInfo info);
        [DllImport("kernel32.dll",SetLastError=true)]
        private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle,int kind,out CameraFileTimes info,uint length);
        private static string CameraIdentity(FileStream stream,CancellationToken token)
        {
            CameraFileInfo info;CameraFileTimes times;
            if(GetFileInformationByHandle(stream.SafeFileHandle,out info)&&(info.IndexHigh!=0||info.IndexLow!=0)&&
                GetFileInformationByHandleEx(stream.SafeFileHandle,0,out times,(uint)Marshal.SizeOf(typeof(CameraFileTimes))))
                return info.Volume.ToString("x8")+info.IndexHigh.ToString("x8")+info.IndexLow.ToString("x8")+times.Changed.ToString("x16");
            // Some virtual filesystems have no stable Windows file ID. Preserve identity using this exact read-only stream.
            long previous=stream.Position;
            try
            {
                stream.Position=0;var block=new byte[131072];int n;
                using(var sha=SHA256.Create())
                {
                    while((n=stream.Read(block,0,block.Length))>0){token.ThrowIfCancellationRequested();sha.TransformBlock(block,0,n,block,0);}
                    sha.TransformFinalBlock(block,0,0);return Convert.ToBase64String(sha.Hash);
                }
            }
            finally{stream.Position=previous;}
        }
        internal static void TargetSize(int w,int h,int edge,out int width,out int height)
        {
            double scale=edge==0?1:Math.Min(1,(double)edge/Math.Max(w,h));
            width=Math.Max(1,(int)Math.Round(w*scale,MidpointRounding.AwayFromZero));
            height=Math.Max(1,(int)Math.Round(h*scale,MidpointRounding.AwayFromZero));
        }
        private static CameraPreview DecodeCamera(CacheEntry key,CancellationToken token)
        {
            using(var input=ImagingInput.OpenRead(key.Path))
            {
                var file=new FileInfo(key.Path);
                if(input.Length!=key.Length||file.CreationTimeUtc.Ticks!=key.Created||file.LastWriteTimeUtc.Ticks!=key.Modified||CameraIdentity(input,token)!=key.SourceIdentity)
                    throw new IOException("原照片在解码前已更改，请重新打开。");
                var preview=new CameraPreview{Input=key};
                if(!VendorRaw.UsesWorker(key.Path,Path.GetExtension(key.Path).TrimStart('.'),input))
                {
                    var raw=RawInspection.Read(input,key.Path);var layout=raw.DescribeDng(input);
                    preview.W=layout.Width;preview.H=layout.Height;preview.Orientation=layout.Orientation;
                    int w,h;TargetSize(preview.W,preview.H,key.MaxEdge,out w,out h);
                    preview.Surface=raw.DevelopDng(input,layout,w,h,token);
                }
                else
                {
                    var developed=ReadCameraOverview(key,token);
                    preview.W=Convert.ToInt32(developed.Metadata["storedWidth"]);preview.H=Convert.ToInt32(developed.Metadata["storedHeight"]);
                    preview.Orientation=Convert.ToInt32(developed.Metadata["orientation"]);preview.Surface=developed.Surface;
                }
                return preview;
            }
        }
        private static void ProduceCameraPreview(CameraFlight job)
        {
            bool acquired=false;
            try
            {
                var token=job.Cancel.Token;CameraSlots.Wait(token);acquired=true;
                token.ThrowIfCancellationRequested();Interlocked.Increment(ref cameraPreviewDecodes);
                job.Value=DecodeCamera(job.Input,token);
            }
            catch(Exception ex){job.Error=ex;}
            finally
            {
                if(acquired)CameraSlots.Release();
                lock(CameraLock)
                {
                    bool current=CameraFlights.Remove(job);
                    if(current&&job.Error==null&&job.Value!=null&&!job.Cancel.IsCancellationRequested&&job.Value.Bytes<=CameraPreviewBudget)
                    {
                        CameraPreviews.Insert(0,job.Value);
                        long bytes=0;
                        for(int i=0;i<CameraPreviews.Count;i++)
                        {
                            bytes+=CameraPreviews[i].Bytes;
                            if(bytes>CameraPreviewBudget){CameraPreviews.RemoveRange(i,CameraPreviews.Count-i);break;}
                        }
                    }
                    job.Complete=true;if(job.Idle!=null){job.Idle.Dispose();job.Idle=null;}
                    job.Ready.Set();
                }
            }
        }
        private static void AbandonCameraPreview(object state)
        {
            var job=(CameraFlight)state;
            lock(CameraLock)
            {
                if(job.Complete||job.Readers!=0||!CameraFlights.Remove(job))return;
            }
            job.Cancel.Cancel();
        }
        private static CameraPreview AcquireCameraPreview(CacheEntry original,CancellationToken token)
        {
            var key=new CacheEntry{Path=original.Path,Created=original.Created,Modified=original.Modified,
                Length=original.Length,SourceIdentity=original.SourceIdentity,RawFrame=original.RawFrame,MaxEdge=Math.Max(2048,original.MaxEdge)};
            CameraFlight job;
            lock(CameraLock)
            {
                int i=CameraPreviews.FindIndex(item=>SameCamera(item.Input,key)&&
                    (item.Input.MaxEdge>=key.MaxEdge||Math.Max(item.W,item.H)<=Math.Max(item.Surface.PixelWidth,item.Surface.PixelHeight)));
                if(i>=0){var cached=CameraPreviews[i];CameraPreviews.RemoveAt(i);CameraPreviews.Insert(0,cached);return cached;}
                job=CameraFlights.Find(entry=>entry.Input.MaxEdge>=key.MaxEdge&&SameCamera(entry.Input,key));
                if(job==null)
                {
                    job=new CameraFlight{Input=key};CameraFlights.Add(job);
                    Task.Factory.StartNew(()=>ProduceCameraPreview(job),CancellationToken.None,TaskCreationOptions.None,TaskScheduler.Default);
                }
                job.Readers++;
                if(job.Idle!=null){job.Idle.Dispose();job.Idle=null;}
            }
            try
            {
                job.Ready.Wait(token);token.ThrowIfCancellationRequested();
                if(job.Error!=null)ExceptionDispatchInfo.Capture(job.Error).Throw();
                return job.Value;
            }
            finally
            {
                lock(CameraLock)
                {
                    if(--job.Readers==0&&!job.Complete)
                        job.Idle=new Timer(AbandonCameraPreview,job,250,Timeout.Infinite);
                }
            }
        }
        private static bool SameCamera(CacheEntry a,CacheEntry b)
        {
            return a.Length==b.Length&&a.Created==b.Created&&a.Modified==b.Modified&&a.SourceIdentity==b.SourceIdentity&&a.RawFrame==b.RawFrame&&
                String.Equals(a.Path,b.Path,StringComparison.OrdinalIgnoreCase);
        }
        private static BitmapSource CameraSize(BitmapSource source,int w,int h,CancellationToken cancel)
        {
            int sw=source.PixelWidth,sh=source.PixelHeight;
            if(w==sw&&h==sh)return source;
            var result=new WriteableBitmap(w,h,96,96,PixelFormats.Rgba64,null);
            var row=new ushort[sw*4];var dst=new ushort[w*4];var sums=new double[w*3];var linear=CameraLinear.Value;
            var first=new int[w];var end=new int[w];var weights=new double[w][];
            for(int x=0;x<w;x++)
            {
                long left=(long)x*sw,right=left+sw;
                first[x]=(int)(left/w);end[x]=(int)((right+w-1)/w);weights[x]=new double[end[x]-first[x]];
                for(int sx=first[x];sx<end[x];sx++)
                    weights[x][sx-first[x]]=(Math.Min(right,(long)(sx+1)*w)-Math.Max(left,(long)sx*w))/(double)sw;
            }
            for(int y=0;y<h;y++)
            {
                cancel.ThrowIfCancellationRequested();Array.Clear(sums,0,sums.Length);
                long top=(long)y*sh,bottom=top+sh;
                for(int sy=(int)(top/h);sy<(bottom+h-1)/h;sy++)
                {
                    source.CopyPixels(new Int32Rect(0,sy,sw,1),row,sw*8,0);
                    double wy=(Math.Min(bottom,(long)(sy+1)*h)-Math.Max(top,(long)sy*h))/(double)sh;
                    for(int x=0;x<w;x++)
                    {
                        var wx=weights[x];int outIndex=x*3;
                        for(int sx=first[x];sx<end[x];sx++)
                        {
                            int z=sx*4;double factor=wx[sx-first[x]]*wy;
                            sums[outIndex]+=linear[row[z]]*factor;sums[outIndex+1]+=linear[row[z+1]]*factor;
                            sums[outIndex+2]+=linear[row[z+2]]*factor;
                        }
                    }
                }
                for(int x=0;x<w;x++)
                {
                    int d=x*4,p=x*3;
                    for(int c=0;c<3;c++)dst[d+c]=ImagingRecipe.U16(ImagingRecipe.EncodeBounded(sums[p+c]));
                    dst[d+3]=65535;
                }
                result.WritePixels(new Int32Rect(0,y,w,1),dst,w*8,0);
            }
            result.Freeze();return result;
        }
    }
}
