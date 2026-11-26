using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AIPhotoStudio
{
    public static partial class Imaging
    {
        internal static bool CanReadViewport(IDictionary<string,double> values)
        {
            return !BodyEdits.Active(values) && PortraitAssets.Hash(values)==null && !PortraitControls.Active(values) &&
                SegmentAssets.Hash(values)==null && !Valued(values,"portrait_skin_light","portrait_skin_smooth","portrait_skin_red") &&
                LocalEdits.SupportsNeighborhood(LocalEdits.ViewportMask(values),values);
        }

        private static bool Valued(IDictionary<string,double> values,params string[] names)
        {
            double value;
            foreach(string name in names)if(values!=null&&values.TryGetValue(name,out value)&&value!=0)return true;
            return false;
        }

        internal static void CheckViewport(Int32Rect r,int w,int h)
        {
            if(r.X<0 || r.Y<0 || r.Width<1 || r.Height<1 || r.X>w-r.Width || r.Y>h-r.Height)
                throw new ArgumentOutOfRangeException("viewport","查看区域超出图像。");
        }

        // Map a rectangle on the upright display into the stored raster before applying
        // the same full-resolution neighborhood operators and EXIF transform as export.
        internal static Int32Rect StoredRect(Int32Rect r,int w,int h,int orientation)
        {
            int x=r.X,y=r.Y,a=r.Width,b=r.Height;
            switch(orientation)
            {
                case 2:return new Int32Rect(w-x-a,y,a,b);
                case 3:return new Int32Rect(w-x-a,h-y-b,a,b);
                case 4:return new Int32Rect(x,h-y-b,a,b);
                case 5:return new Int32Rect(y,x,b,a);
                case 6:return new Int32Rect(y,h-x-a,b,a);
                case 7:return new Int32Rect(w-y-b,h-x-a,b,a);
                case 8:return new Int32Rect(w-y-b,x,b,a);
                default:return r;
            }
        }

        internal static BitmapSource ReadViewport(string path,IDictionary<string,double> values,Int32Rect requested,CancellationToken cancel)
        {
            return ReadEditedViewport(path,values,requested,cancel,null);
        }

        internal static BitmapSource ReadEditedViewport(string path,IDictionary<string,double> values,Int32Rect requested,
            CancellationToken cancel,CameraViewport camera)
        {
            cancel.ThrowIfCancellationRequested();
            var masks=LocalEdits.ViewportMask(values);
            if(masks!=null)LocalEdits.ValidateSource(masks,path);
            BitmapSource result;
            using(var stream=ImagingInput.OpenRead(path))
            {
                SurfaceReader reader;
                int orientation;
                if(camera!=null){reader=new SurfaceReader(camera,cancel);orientation=camera.Orientation;}
                else
                {
                    var input=ImagingInput.Read(stream,ImagingInput.TypeFromExtension(path));
                    reader=new SurfaceReader(input,input.Frame.PixelWidth,input.Frame.PixelHeight);
                    orientation=input.Orientation;
                }
                int w=orientation>=5?reader.StoredHeight:reader.StoredWidth;
                int h=orientation>=5?reader.StoredWidth:reader.StoredHeight;
                var boxes=LocalEdits.RedactionBounds(masks==null?null:masks.areas,reader.StoredWidth,reader.StoredHeight,orientation);
                double reserve=camera==null?0:RawInspection.DngReserveEv;
                var recipe=new ImagingRecipe(values,reserve);
                bool independentReference=LocalEdits.NeedsRangeReference(masks,values)&&
                    values.Any(p=>p.Value!=0&&GlobalColorKeys.Contains(p.Key));
                var neutral=independentReference?new ImagingRecipe(CameraRawModes.Neutral(values),reserve):null;
                var skinLayers=masks==null?new LocalEdits.MaskLayer[0]:masks.layers.Where(layer=>layer.kind=="skin"&&
                    LocalEdits.Strength(values,layer.slot)!=0&&
                    Valued(values,LocalEdits.ToneKey(layer.slot,"finish"),LocalEdits.ToneKey(layer.slot,"skintone"))).ToArray();
                var measured=skinLayers.Length==0?null:new Dictionary<int,StudioFinish.SkinReference>();
                Func<Int32Rect,int,BitmapSource> readLayer=delegate(Int32Rect area,int measuringSlot)
                {
                    if(masks==null)return ViewportPixels(reader,orientation,area,recipe,cancel);
                    var region=LocalEdits.RequiredInput(masks,values,w,h,area,cancel);
                    var global=ViewportPixels(reader,orientation,region,recipe,cancel,boxes);
                    var reference=neutral==null?global:ViewportPixels(reader,orientation,region,neutral,cancel,boxes);
                    var pixels=LocalEdits.ApplySet(global,values,masks,cancel,reference,new LocalEdits.ToneRegion(w,h,region,measured,measuringSlot));
                    if(measuringSlot!=0||area==region)return pixels;
                    var crop=new CroppedBitmap(pixels,new Int32Rect(area.X-region.X,area.Y-region.Y,area.Width,area.Height));
                    crop.Freeze();return crop;
                };
                Func<Int32Rect,BitmapSource> read=delegate(Int32Rect area)
                {
                    // A face may be outside the viewport. Sample its own small region with all
                    // preceding layers applied before rendering the requested region.
                    foreach(var layer in skinLayers)
                        if(!measured.ContainsKey(layer.slot))readLayer(layer.faceSample.Bounds(w,h),layer.slot);
                    return readLayer(area,0);
                };
                if(FrameGeometry.Active(values))result=FrameGeometry.Resolve(w,h,values).ReadViewport(requested,read,cancel);
                else {CheckViewport(requested,w,h);result=read(requested);}
                if(masks!=null)LocalEdits.ValidateSource(masks,path);
            }
            return result;
        }

        internal sealed class CameraViewport
        {
            internal const long Budget=256L*1024*1024;
            private readonly string path;
            private readonly int track;
            internal readonly int Width,Height,Orientation;
            private sealed class WindowEntry
            {
                internal readonly Int32Rect Area;
                internal readonly BitmapSource Pixels;
                internal long Bytes {get{return (long)Area.Width*Area.Height*8;}}
                internal WindowEntry(Int32Rect area,BitmapSource pixels){Area=area;Pixels=pixels;}
                internal bool Contains(Int32Rect r)
                {
                    return r.X>=Area.X&&r.Y>=Area.Y&&r.X+r.Width<=Area.X+Area.Width&&r.Y+r.Height<=Area.Y+Area.Height;
                }
            }
            private readonly List<WindowEntry> windows=new List<WindowEntry>();
            private PackedCameraSurface packed;
            private bool triedPacking;
            internal readonly double DpiX,DpiY;
            internal BitmapSource Pixels {get{return windows[0].Pixels;}}
            internal Int32Rect StoredArea {get{return packed==null?windows[0].Area:new Int32Rect(0,0,Width,Height);}}
            internal int DecodeCount {get; private set;}
            internal bool OverviewReused;
            internal long SensorDevelopMs;
            internal long PackedBytes {get{return packed==null?0:packed.Bytes;}}
            internal long RetainedBytes {get{return packed==null?windows.Sum(entry=>entry.Bytes):packed.Bytes;}}

            internal CameraViewport(string file,int frame,BitmapSource pixels,int orientation)
            {
                path=file;track=frame;Orientation=orientation;
                Width=pixels.PixelWidth;Height=pixels.PixelHeight;DecodeCount=1;
                DpiX=pixels.DpiX;DpiY=pixels.DpiY;
                windows.Add(new WindowEntry(new Int32Rect(0,0,Width,Height),pixels));
            }
            internal CameraViewport(string file,int frame,VendorRaw.Result raw,bool attempted)
            {
                path=file;track=frame;DecodeCount=1;triedPacking=attempted;packed=raw.Packed;
                Width=Convert.ToInt32(raw.Metadata["outputWidth"]);Height=Convert.ToInt32(raw.Metadata["outputHeight"]);
                Orientation=Convert.ToInt32(raw.Metadata["orientation"]);DpiX=96;DpiY=96;
                if(raw.Metadata.ContainsKey("sensorDevelopMs"))SensorDevelopMs=Convert.ToInt64(raw.Metadata["sensorDevelopMs"]);
                if(packed==null)windows.Add(new WindowEntry(new Int32Rect(0,0,Width,Height),raw.Surface));
            }
            // Copy a detached window. A CroppedBitmap would still keep the full RAW surface alive.
            private BitmapSource Window(BitmapSource full,Int32Rect area,CancellationToken cancel)
            {
                var surface=new WriteableBitmap(area.Width,area.Height,full.DpiX,full.DpiY,PixelFormats.Rgba64,null);
                int stride=area.Width*8,band=Math.Max(1,Math.Min(64,1048576/stride));
                var buffer=new byte[band*stride];
                for(int y=0;y<area.Height;y+=band)
                {
                    cancel.ThrowIfCancellationRequested();
                    int count=Math.Min(band,area.Height-y);
                    full.CopyPixels(new Int32Rect(area.X,area.Y+y,area.Width,count),buffer,stride,0);
                    surface.WritePixels(new Int32Rect(0,y,area.Width,count),buffer,stride,0);
                }
                surface.Freeze();return surface;
            }
            internal void Copy(Int32Rect requested,ushort[] target,CancellationToken cancel)
            {
                if(!triedPacking)
                {
                    if((long)Width*Height*8>Budget)
                    {
                        packed=PackedCameraSurface.TryCreate(Pixels,Budget,cancel);
                        if(packed!=null)windows.Clear();
                    }
                    triedPacking=true;
                }
                if(packed!=null){packed.Copy(requested,target,cancel);return;}
                int found=windows.FindIndex(entry=>entry.Bytes<=Budget&&entry.Contains(requested));
                if(found>0)
                {
                    var cached=windows[found];windows.RemoveAt(found);windows.Insert(0,cached);
                }
                else if(found<0)
                {
                    BitmapSource full=Pixels;
                    if(StoredArea.Width!=Width||StoredArea.Height!=Height)
                    {
                        var fresh=DecodeCameraViewport(path,track,cancel,false);
                        if(fresh.Width!=Width||fresh.Height!=Height||fresh.Orientation!=Orientation)throw new InvalidOperationException("原片已变化，请重新读取细节。");
                        full=fresh.Pixels;DecodeCount++;
                    }
                    int padX=Math.Min(768,Math.Max(256,requested.Width/2)),padY=Math.Min(768,Math.Max(256,requested.Height/2));
                    int left=Math.Max(0,requested.X-padX),top=Math.Max(0,requested.Y-padY);
                    var area=new Int32Rect(left,top,Math.Min(Width,requested.X+requested.Width+padX)-left,
                        Math.Min(Height,requested.Y+requested.Height+padY)-top);
                    if((long)area.Width*area.Height*8>64L*1024*1024)area=requested;
                    if((long)area.Width*area.Height*8>Budget)
                    {
                        full.CopyPixels(requested,target,requested.Width*8,0);
                        return;
                    }
                    var entry=new WindowEntry(area,Window(full,area,cancel));
                    windows.RemoveAll(old=>old.Bytes>Budget||entry.Contains(old.Area));
                    while(windows.Count>=8||RetainedBytes+entry.Bytes>Budget)windows.RemoveAt(windows.Count-1);
                    windows.Insert(0,entry);
                }
                Pixels.CopyPixels(new Int32Rect(requested.X-StoredArea.X,requested.Y-StoredArea.Y,requested.Width,requested.Height),
                    target,requested.Width*8,0);
            }
            internal BitmapSource Read(IDictionary<string,double> values,Int32Rect rect,CancellationToken cancel)
            {
                return ViewportPixels(new SurfaceReader(this,cancel),Orientation,rect,new ImagingRecipe(values,RawInspection.DngReserveEv),cancel);
            }
        }

        internal static CameraViewport LoadCameraViewport(string path,int frame,CancellationToken cancel)
        {
            return DecodeCameraViewport(path,frame,cancel,true);
        }
        private static CameraViewport DecodeCameraViewport(string path,int frame,CancellationToken cancel,bool allowPacking)
        {
            string kind=ImagingInput.TypeFromExtension(path);
            using(var stream=ImagingInput.OpenRead(path))
            {
                if(VendorRaw.UsesWorker(path,kind,stream))
                {
                    if(allowPacking){
                        var cached=TakeOverviewDetail(path,frame,stream,cancel);
                        if(cached!=null)return cached;
                    }
                    var raw=VendorRaw.Read(path,"render",0,cancel,frame,allowPacking?CameraViewport.Budget:0);
                    return new CameraViewport(path,frame,raw,allowPacking);
                }
                var dng=RawInspection.Read(stream,path);var layout=dng.DescribeDng(stream);
                if((long)layout.Width*layout.Height>MaxOutputPixels)throw new NotSupportedException("此图超出全尺寸处理上限。");
                return new CameraViewport(path,frame,dng.DevelopDng(stream,layout,layout.Width,layout.Height,cancel),layout.Orientation);
            }
        }

        private static BitmapSource ViewportPixels(SurfaceReader reader,int orientation,Int32Rect requested,ImagingRecipe recipe,CancellationToken cancel,Int32Rect[] boxes=null)
        {
            int w=reader.StoredWidth,h=reader.StoredHeight,edge=Math.Max(w,h);
            CheckViewport(requested,orientation>=5?h:w,orientation>=5?w:h);
            var r=StoredRect(requested,w,h,orientation);
            var detail=new ImagingDetail(recipe,edge,edge);
            int x=Math.Max(0,r.X-detail.Radius),y=Math.Max(0,r.Y-detail.Radius);
            var area=new Int32Rect(x,y,Math.Min(w,r.X+r.Width+detail.Radius)-x,Math.Min(h,r.Y+r.Height+detail.Radius)-y);
            var buffer=new ushort[checked(area.Width*area.Height*4)];
            reader.Read(area,buffer);
            if(boxes!=null)LocalEdits.FillRedactions(buffer,area,boxes);
            var options=new ParallelOptions{CancellationToken=cancel,MaxDegreeOfParallelism=Math.Min(6,Environment.ProcessorCount)};
            if(recipe.HasColor)Parallel.For(0,area.Height,options,row=>recipe.Apply(buffer,row*area.Width*4,(row+1)*area.Width*4));
            if(detail.Radius>0)
            {
                var filtered=new ushort[buffer.Length];
                Parallel.For(0,area.Height,options,row=>detail.Row(buffer,area.Width,area.Height,row,filtered,row*area.Width*4));
                buffer=filtered;
            }
            cancel.ThrowIfCancellationRequested();
            var source=BitmapSource.Create(area.Width,area.Height,Dpi(reader.DpiX),Dpi(reader.DpiY),PixelFormats.Rgba64,null,buffer,area.Width*8);
            var cropped=new CroppedBitmap(source,new Int32Rect(r.X-x,r.Y-y,r.Width,r.Height));
            cropped.Freeze();
            return Oriented(cropped,orientation);
        }
    }
}
