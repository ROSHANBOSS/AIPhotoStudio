using System;
using System.Collections.Generic;
using System.IO;
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
        private const long MaxOutputPixels = 80000000L;
        private const int MaxCacheItemBytes = 24 * 1024 * 1024;
        private static readonly object CacheLock = new object();
        private static readonly List<CacheEntry> Cache = new List<CacheEntry>();
        // Two small neutral images (at most 324 KiB) keep swatches and filmstrip
        // thumbnails from evicting either the canvas or its interactive preview.
        private static readonly List<CacheEntry> SmallPreviews = new List<CacheEntry>();
        private static List<CacheEntry> PreviewCache(int edge){return edge<=144?SmallPreviews:Cache;}

        private sealed class CacheEntry
        {
            internal string Path;
            internal string SourceIdentity;
            internal long Modified, Length, Created;
            internal int MaxEdge, Orientation, SourceEdge,RawFrame;
            internal BitmapSource Surface;
            internal bool Matches(CacheEntry other)
            {
                return String.Equals(Path, other.Path, StringComparison.OrdinalIgnoreCase) && MaxEdge == other.MaxEdge &&
                    Modified == other.Modified && Created == other.Created && Length == other.Length && SourceIdentity==other.SourceIdentity&&RawFrame==other.RawFrame;
            }
        }

        public static Dictionary<string, object> Inspect(string path)
        {
            string full = ImagingInput.FullPath(path), kind = ImagingInput.TypeFromExtension(full);
            using (var stream = ImagingInput.OpenRead(full))
            {
                if(VendorRaw.UsesWorker(full,kind,stream))return VendorRaw.Read(full,"inspect",0,CancellationToken.None).Metadata;
                if (kind == "DNG")
                {
                    var raw = RawInspection.Read(stream, full);
                    var layout = raw.DescribeDng(stream);
                    var properties = raw.Properties;
                    properties["format"] = "DNG"; properties["frameCount"] = 1; properties["orientation"] = layout.Orientation;
                    properties["width"] = layout.Orientation >= 5 ? layout.Height : layout.Width;
                    properties["height"] = layout.Orientation >= 5 ? layout.Width : layout.Height;
                    properties["storedWidth"] = layout.W; properties["storedHeight"] = layout.H;
                    properties["bitsPerPixel"] = (int)(long)properties["bitsPerSample"]; properties["pixelFormat"] = "Bayer CFA";
                    properties["hasColorProfile"] = false; properties["colorSpace"] = "Camera D65 matrix → sRGB";
                    properties["decoder"] = "AIPhotoStudio DNG Bayer v1"; properties["encodingProfileVerified"] = true;
                    foreach (string key in new[]{"cameraManufacturer","cameraModel","dateTaken"}) if (!properties.ContainsKey(key)) properties[key] = "";
                    return properties;
                }
                var input = ImagingInput.Read(stream, kind);
                var f = input.Frame;
                return new Dictionary<string, object>
                {
                    {"width", input.Orientation >= 5 ? f.PixelHeight : f.PixelWidth}, {"height", input.Orientation >= 5 ? f.PixelWidth : f.PixelHeight},
                    {"storedWidth", f.PixelWidth}, {"storedHeight", f.PixelHeight}, {"orientation", input.Orientation},
                    {"pixelFormat", f.Format.ToString()}, {"bitsPerPixel", f.Format.BitsPerPixel}, {"format", kind}, {"frameCount", input.FrameCount},
                    {"hasColorProfile", input.Profile != null}, {"colorSpace", input.Profile == null ? "sRGB (assumed for untagged integer RGB)" : "ICC → sRGB"},
                    {"cameraManufacturer", input.Make}, {"cameraModel", input.Model}, {"dateTaken", input.Date}, {"fileBytes", stream.Length}
                };
            }
        }

        private static BitmapSource AsFormat(BitmapSource source, PixelFormat format)
        {
            return source.Format == format ? source : new FormatConvertedBitmap(source, format, null, 0);
        }
        private static BitmapSource Scaled(BitmapSource source, int width, int height)
        {
            if (width == source.PixelWidth && height == source.PixelHeight) return source;
            return new TransformedBitmap(source, new ScaleTransform((double)width / source.PixelWidth, (double)height / source.PixelHeight));
        }
        private static BitmapSource ScaledRgba(BitmapSource source, int width, int height, bool hasAlpha)
        {
            if (hasAlpha && (source.PixelWidth != width || source.PixelHeight != height))
                source = AsFormat(source, PixelFormats.Prgba64);
            return AsFormat(Scaled(source, width, height), PixelFormats.Rgba64);
        }
        private static bool HasAlpha(BitmapSource source)
        {
            PixelFormat format = source.Format;
            if (format == PixelFormats.Bgra32 || format == PixelFormats.Pbgra32 || format == PixelFormats.Rgba64 ||
                format == PixelFormats.Prgba64 || format == PixelFormats.Rgba128Float || format == PixelFormats.Prgba128Float) return true;
            if (source.Palette != null) foreach (Color c in source.Palette.Colors) if (c.A < 255) return true;
            return false;
        }

        private sealed class SurfaceReader
        {
            internal readonly BitmapSource Pixels;
            private readonly CameraViewport camera;
            private readonly CancellationToken cancel;
            internal int StoredWidth { get { return camera==null?Pixels.PixelWidth:camera.Width; } }
            internal int StoredHeight { get { return camera==null?Pixels.PixelHeight:camera.Height; } }
            internal double DpiX {get{return camera==null?Pixels.DpiX:camera.DpiX;}}
            internal double DpiY {get{return camera==null?Pixels.DpiY:camera.DpiY;}}
            private readonly BitmapSource alpha;
            private ushort[] alphaBuffer;
            internal SurfaceReader(BitmapSource cached) { Pixels = cached; }
            internal SurfaceReader(CameraViewport input,CancellationToken token)
            {
                camera=input;cancel=token;
            }
            internal SurfaceReader(ImagingInput input, int width, int height)
            {
                if (input.Profile == null)
                {
                    // Let the decoder scale a native source before expanding to four 16-bit channels.
                    Pixels = ScaledRgba(input.Frame, width, height, HasAlpha(input.Frame));
                }
                else
                {
                    bool masked = HasAlpha(input.Frame);
                    if (masked) alpha = ScaledRgba(input.Frame, width, height, true);
                    PixelFormat f = input.Frame.Format;
                    BitmapSource color = f == PixelFormats.Cmyk32 || f == PixelFormats.Gray8 || f == PixelFormats.Gray16 ? input.Frame : AsFormat(input.Frame, PixelFormats.Rgba64);
                    color = new ColorConvertedBitmap(color, input.Profile, new ColorContext(PixelFormats.Bgra32), PixelFormats.Rgba64);
                    Pixels = ScaledRgba(color, width, height, masked);
                }
            }

            internal void Read(int y, int rows, ushort[] target)
            {
                Read(new Int32Rect(0,y,StoredWidth,rows),target);
            }

            internal void Read(Int32Rect rect, ushort[] target)
            {
                if(camera!=null){camera.Copy(rect,target,cancel);return;}
                int width=rect.Width;
                Pixels.CopyPixels(rect, target, width * 8, 0);
                if (alpha == null) return;
                int count = checked(width * rect.Height);
                if (alphaBuffer == null || alphaBuffer.Length < count * 4) alphaBuffer = new ushort[count * 4];
                alpha.CopyPixels(rect, alphaBuffer, width * 8, 0);
                for (int p = 3; p < count * 4; p += 4) target[p] = alphaBuffer[p];
            }
        }

        public static BitmapSource Render(string path, IDictionary<string, double> values, int maxEdge)
        {
            return Render(path, values, maxEdge, CancellationToken.None);
        }

        public static BitmapSource Render(string path, IDictionary<string, double> values, int maxEdge, CancellationToken cancel)
        {
            return Render(path,values,maxEdge,cancel,null);
        }

        internal static BitmapSource Render(string path, IDictionary<string, double> values, int maxEdge, CancellationToken cancel, LocalEdits.MaskSet draft,PortraitSpot[] spotDraft=null,DragStages stages=null,Int32Rect? region=null)
        {
            cancel.ThrowIfCancellationRequested();
            var masks=draft??(LocalEdits.Hash(values)==null?null:LocalEdits.ReadSet(values));
            if(masks!=null)LocalEdits.ValidateSource(masks,path);
            BodyAssets.ValidateFile(path, values);
            PortraitAssets.ValidateFile(path,values,spotDraft!=null);
            if(stages!=null&&SegmentAssets.Hash(values)!=null)SegmentAssets.Read(values);
            if(maxEdge<0 || maxEdge>65535)throw new ArgumentOutOfRangeException("maxEdge");
            bool framing=FrameGeometry.Active(values);
            var frame=framing && maxEdge>0?FrameGeometry.ForFile(path,values):null;
            int decodeEdge=frame==null?maxEdge:frame.DecodeEdge(maxEdge);
            var areas=masks==null?null:masks.areas;
            bool redact=areas!=null&&areas.Any(a=>a.kind=="redactRect");
            // Source redaction precedes every spatial filter. A cached intermediate from a
            // different live box must never be reused as a color, portrait or body surface.
            bool redEyes=masks!=null&&masks.HasV7Edits;
            if(redact||redEyes)stages=null;
            bool needsRange=LocalEdits.NeedsRangeReference(masks,values);
            bool neutralGlobal=needsRange&&!values.Any(p=>p.Value!=0&&GlobalColorKeys.Contains(p.Key));
            BitmapSource rangeReference=null;
            Func<BitmapSource> global=()=>RenderGlobal(path,values,decodeEdge,cancel,areas);
            Func<BitmapSource> regional=delegate {
                var baseImage=stages==null?global():stages.Cache.Read(0,stages.Keys[0],global);
                if(neutralGlobal)rangeReference=baseImage;
                if(redEyes)baseImage=SpotHealing.Apply(baseImage,masks.primary,cancel,null,true);
                var spots=PortraitSpots.Apply(baseImage,PortraitAssets.Resolve(values),spotDraft,cancel);
                var portrait=PortraitRetouch.Apply(PortraitEdits.Apply(spots,values,cancel),values,cancel);
                return SegmentAssets.Apply(portrait,values,cancel);
            };
            Func<BitmapSource> shaped=delegate {
                var selection=stages==null?regional():stages.Cache.Read(1,stages.Keys[1],regional);
                return BodyEdits.Apply(selection,values,cancel,masks==null?null:masks.primary,areas);
            };
            // Live brush strokes may also protect a body warp. Only saved masks have a content-addressed identity.
            BitmapSource body=stages==null||draft!=null?shaped():stages.Cache.Read(2,stages.Keys[2],shaped);
            if(needsRange&&rangeReference==null)
            {
                Func<BitmapSource> neutral=()=>RenderGlobal(path,CameraRawModes.Neutral(values),decodeEdge,cancel,areas);
                rangeReference=neutralGlobal&&stages!=null?stages.Cache.Read(0,stages.Keys[0],global):
                    region.HasValue&&stages!=null?stages.Cache.Read(3,stages.Neutral,neutral):neutral();
            }
            LocalEdits.ToneRegion localRegion=null;
            if(region.HasValue)
            {
                CheckViewport(region.Value,body.PixelWidth,body.PixelHeight);
                localRegion=new LocalEdits.ToneRegion(body.PixelWidth,body.PixelHeight,region.Value);
                body=new CroppedBitmap(body,region.Value);body.Freeze();
                if(rangeReference!=null){rangeReference=new CroppedBitmap(rangeReference,region.Value);rangeReference.Freeze();}
            }
            BitmapSource result=masks==null?body:draft==null?LocalEdits.ApplySaved(body,values,cancel,rangeReference,localRegion):
                LocalEdits.ApplySet(body,values,draft,cancel,rangeReference);
            if(masks!=null)LocalEdits.ValidateSource(masks,path);
            BodyAssets.ValidateFile(path, values);
            PortraitAssets.ValidateFile(path,values,spotDraft!=null);
            if(!framing)return result;
            if(frame==null)frame=FrameGeometry.Resolve(result.PixelWidth,result.PixelHeight,values);
            return frame.Apply(result,maxEdge,cancel);
        }

        internal static BitmapSource PortraitSearchImage(string path,IDictionary<string,double> values,CancellationToken cancel)
        {
            var neutral=new Dictionary<string,double>{{CameraRawModes.FrameKey,CameraRawModes.Selected(values)}};
            return PortraitSpots.Apply(RenderGlobal(path,neutral,1200,cancel),PortraitAssets.Resolve(values),null,cancel);
        }
        private static BitmapSource RenderGlobal(string path, IDictionary<string, double> values, int maxEdge, CancellationToken cancel,LocalEdits.AreaMark[] areas=null,bool beforeHsl=false)
        {
            cancel.ThrowIfCancellationRequested();
            if (maxEdge < 0 || maxEdge > 65535) throw new ArgumentOutOfRangeException("maxEdge");
            string full = ImagingInput.FullPath(path), kind = ImagingInput.TypeFromExtension(full);
            int rawFrame=CameraRawModes.Selected(values);
            if(rawFrame!=0&&kind!="CR3")throw new NotSupportedException("这份原片没有所选的感光轨。");
            var recipe = new ImagingRecipe(values, kind == "DNG" || VendorRaw.Supports(kind) ? RawInspection.DngReserveEv : 0,beforeHsl);
            using (var stream = ImagingInput.OpenRead(full))
            {
                var info = new FileInfo(full);
                var key = new CacheEntry { Path = full, Modified = info.LastWriteTimeUtc.Ticks, Created = info.CreationTimeUtc.Ticks,
                    Length = stream.Length, MaxEdge = maxEdge,RawFrame=rawFrame };
                if(maxEdge>0&&(kind=="DNG"||VendorRaw.Supports(kind)))key.SourceIdentity=CameraIdentity(stream,cancel);
                CacheEntry hit = null;
                if (maxEdge > 0)
                {
                    lock (CacheLock)
                    {
                        var cache=PreviewCache(maxEdge);
                        int i = cache.FindIndex(key.Matches);
                        if (i >= 0) { hit = cache[i]; cache.RemoveAt(i); cache.Insert(0, hit); }
                    }
                }
                SurfaceReader source;
                if (hit != null)
                {
                    key = hit;
                    source = new SurfaceReader(hit.Surface);
                }
                else if(maxEdge>0&&(kind=="DNG"||VendorRaw.Supports(kind)))
                {
                    var preview=AcquireCameraPreview(key,cancel);
                    key.Orientation=preview.Orientation;key.SourceEdge=Math.Max(preview.W,preview.H);
                    int width,height;TargetSize(preview.W,preview.H,maxEdge,out width,out height);
                    source=new SurfaceReader(CameraSize(preview.Surface,width,height,cancel));
                }
                else if(VendorRaw.UsesWorker(full,kind,stream))
                {
                    var developed=VendorRaw.Read(full,"render",maxEdge,cancel,rawFrame);
                    key.Orientation=Convert.ToInt32(developed.Metadata["orientation"]);
                    key.SourceEdge=Math.Max(Convert.ToInt32(developed.Metadata["storedWidth"]),Convert.ToInt32(developed.Metadata["storedHeight"]));
                    source=new SurfaceReader(developed.Surface);
                }
                else
                {
                    RawInspection raw = null; RawInspection.DngLayout layout = null; ImagingInput input = null;
                    int storedWidth, storedHeight;
                    if (kind == "DNG")
                    {
                        raw = RawInspection.Read(stream, full); layout = raw.DescribeDng(stream);
                        key.Orientation = layout.Orientation; storedWidth = layout.Width; storedHeight = layout.Height;
                    }
                    else
                    {
                        input = ImagingInput.Read(stream, kind); key.Orientation = input.Orientation;
                        storedWidth = input.Frame.PixelWidth; storedHeight = input.Frame.PixelHeight;
                    }
                    key.SourceEdge = Math.Max(storedWidth, storedHeight);
                    double scale = maxEdge == 0 ? 1 : Math.Min(1, (double)maxEdge / key.SourceEdge);
                    int width = Math.Max(1, (int)Math.Round(storedWidth * scale, MidpointRounding.AwayFromZero));
                    int height = Math.Max(1, (int)Math.Round(storedHeight * scale, MidpointRounding.AwayFromZero));
                    if ((long)width * height > MaxOutputPixels) throw new NotSupportedException("当前全尺寸渲染上限为 8000 万像素。");
                    source = raw == null ? new SurfaceReader(input,width,height) : new SurfaceReader(raw.DevelopDng(stream,layout,width,height,cancel));
                }
                var boxes=LocalEdits.RedactionBounds(areas,source.StoredWidth,source.StoredHeight,key.Orientation);
                if (hit != null && (boxes==null||boxes.Length==0) && !recipe.HasColor && recipe.Noise == 0 && recipe.ChromaNoise==0 && recipe.LumaNoise==0 && recipe.Clarity == 0 && recipe.Sharpness == 0)
                    return Oriented(hit.Surface, key.Orientation);
                return ProcessSurface(source, key, recipe, cancel,boxes);
            }
        }

        private static double Dpi(double input) { return input > 0 && input < 100000 ? input : 96; }

        private static BitmapSource ProcessSurface(SurfaceReader reader, CacheEntry key, ImagingRecipe recipe, CancellationToken cancel,Int32Rect[] boxes)
        {
            int w = reader.Pixels.PixelWidth, h = reader.Pixels.PixelHeight, stride = checked(8 * w);
            int band = Math.Min(h, Math.Max(4, Math.Min(80, 2097152 / stride)));
            var detail = new ImagingDetail(recipe, Math.Max(w, h), key.SourceEdge);
            int halo = detail.Radius;
            var tile = new ushort[(band + 2 * halo) * w * 4];
            var input = new ushort[(band + 2 * halo) * w * 4];
            var filtered = halo != 0 ? new ushort[band * w * 4] : null;
            var output = new WriteableBitmap(w, h, Dpi(reader.Pixels.DpiX), Dpi(reader.Pixels.DpiY), PixelFormats.Rgba64, null);
            WriteableBitmap cache = null;
            bool neutral = !recipe.HasColor && halo == 0 && (boxes==null||boxes.Length==0);
            if (key.Surface == null && key.MaxEdge > 0 && (long)stride * h <= MaxCacheItemBytes)
                cache = neutral ? output : new WriteableBitmap(w, h, output.DpiX, output.DpiY, PixelFormats.Rgba64, null);
            var parallel = new ParallelOptions { CancellationToken = cancel, MaxDegreeOfParallelism = Math.Min(6, Environment.ProcessorCount) };
            int previousStart = 0, previousEnd = 0;
            using (var gpu = GpuColorPass.TryCreate(recipe, (long)w * h, input.Length, key.MaxEdge > 0))
            for (int y = 0; y < h; y += band)
            {
                cancel.ThrowIfCancellationRequested();
                int count = Math.Min(band, h - y), start = Math.Max(0, y - halo), end = Math.Min(h, y + count + halo);
                int firstNew = Math.Max(previousEnd, start), overlap = firstNew - start, fresh = end - firstNew;
                if (overlap > 0) Buffer.BlockCopy(tile, (start - previousStart) * stride, tile, 0, overlap * stride);
                if (fresh > 0)
                {
                    reader.Read(firstNew, fresh, input);
                    if (cache != null && !neutral) cache.WritePixels(new Int32Rect(0, firstNew, w, fresh), input, stride, 0);
                    if(boxes!=null)LocalEdits.FillRedactions(input,new Int32Rect(0,firstNew,w,fresh),boxes);
                    if (recipe.HasColor && (gpu == null || !gpu.TryApply(input, w * fresh)))
                    {
                        if (w * fresh < 10000) recipe.Apply(input, 0, w * fresh * 4);
                        else Parallel.For(0, fresh, parallel, row => recipe.Apply(input, row * w * 4, (row + 1) * w * 4));
                    }
                    Buffer.BlockCopy(input, 0, tile, overlap * stride, fresh * stride);
                }
                var rect = new Int32Rect(0, y, w, count);
                if (halo != 0)
                {
                    int rowsAvailable = end - start, offset = y - start;
                    Parallel.For(0, count, parallel, i => detail.Row(tile, w, rowsAvailable, offset + i, filtered, i * w * 4));
                    output.WritePixels(rect, filtered, stride, 0);
                }
                else output.WritePixels(rect, tile, stride, 0);
                previousStart = start; previousEnd = end;
            }
            cancel.ThrowIfCancellationRequested();
            output.Freeze();
            if (cache != null)
            {
                if (!cache.IsFrozen) cache.Freeze();
                key.Surface = cache;
                lock (CacheLock)
                {
                    var entries=PreviewCache(key.MaxEdge);
                    entries.RemoveAll(key.Matches);
                    entries.Insert(0, key);
                    if (entries.Count > 2) entries.RemoveAt(entries.Count - 1);
                }
            }
            return Oriented(output, key.Orientation);
        }

        private static BitmapSource Oriented(BitmapSource source, int orientation)
        {
            if (orientation == 1) return source;
            Matrix matrix;
            switch (orientation)
            {
                case 2: matrix = new Matrix(-1, 0, 0, 1, 0, 0); break;
                case 3: matrix = new Matrix(-1, 0, 0, -1, 0, 0); break;
                case 4: matrix = new Matrix(1, 0, 0, -1, 0, 0); break;
                case 5: matrix = new Matrix(0, 1, 1, 0, 0, 0); break;
                case 6: matrix = new Matrix(0, 1, -1, 0, 0, 0); break;
                case 7: matrix = new Matrix(0, -1, -1, 0, 0, 0); break;
                case 8: matrix = new Matrix(0, -1, 1, 0, 0, 0); break;
                default: return source;
            }
            var result = new TransformedBitmap(source, new MatrixTransform(matrix));
            result.Freeze();
            return result;
        }

        private static BitmapSource CompositeMatte(BitmapSource input,string hex,CancellationToken cancel)
        {
            if(String.IsNullOrEmpty(hex))hex="#ffffff";
            uint color;
            if(hex.Length!=7 || hex[0]!='#' || !UInt32.TryParse(hex.Substring(1),System.Globalization.NumberStyles.AllowHexSpecifier,System.Globalization.CultureInfo.InvariantCulture,out color))
                throw new ArgumentException("JPEG 底色须为六位 RGB 颜色。");
            var matte=new[]{ImagingRecipe.Linear[(int)(color>>16)*257],ImagingRecipe.Linear[(int)((color>>8)&255)*257],ImagingRecipe.Linear[(int)(color&255)*257]};
            var source = AsFormat(input, PixelFormats.Rgba64);
            int w = source.PixelWidth, h = source.PixelHeight, rows = Math.Min(h, Math.Max(1, Math.Min(64, 2097152 / (8 * w))));
            var rgba = new ushort[w * 4 * rows];
            var rgb = new byte[w * 3 * rows];
            var result = new WriteableBitmap(w, h, source.DpiX, source.DpiY, PixelFormats.Rgb24, null);
            for (int y = 0; y < h; y += rows)
            {
                cancel.ThrowIfCancellationRequested();
                int n = Math.Min(h - y, rows);
                var rect = new Int32Rect(0, y, w, n);
                source.CopyPixels(rect, rgba, w * 8, 0);
                for (int i = 0, j = 0; i < n * w * 4; i += 4, j += 3)
                {
                    double a = rgba[i + 3] / 65535.0;
                    for (int k = 0; k < 3; k++)
                        rgb[j + k] = a == 1 ? (byte)((rgba[i + k] + 128) / 257) :
                            (byte)(ImagingRecipe.EncodeBounded(a * ImagingRecipe.Linear[rgba[i + k]] + (1 - a)*matte[k]) * 255 + .5);
                }
                result.WritePixels(rect, rgb, w * 3, 0);
            }
            result.Freeze();
            return result;
        }

        public static void Export(string path, string target, IDictionary<string, double> values)
        {
            Export(path, target, values, CancellationToken.None);
        }

        public static void Export(string path, string target, IDictionary<string, double> values, CancellationToken cancel)
        {
            Export(path,target,values,cancel,0,95);
        }

        internal static void Export(string path,string target,IDictionary<string,double> values,CancellationToken cancel,int longEdge,int quality)
        {
            WriteOutput(path,target,()=>Render(path,values,0,cancel),cancel,longEdge,quality,null,"srgb",null,null);
        }

        internal static ExportMeasure WriteOutput(string path,string target,Func<BitmapSource> render,CancellationToken cancel,int longEdge,int quality,
            DeliveryMark watermark,string colorSpace,Action<string,FileStream> staging,Action<FileStream> prepared,
            int maxBytes=0,int minEdge=0,string matte=null,int measuredBytes=0)
        {
            cancel.ThrowIfCancellationRequested();
            if(longEdge<0 || longEdge>65535 || quality<1 || quality>100)throw new ArgumentOutOfRangeException("导出尺寸或 JPEG 质量超出范围。");
            string original = ImagingInput.FullPath(path), destination = ImagingInput.FullPath(target), ext = Path.GetExtension(destination).ToLowerInvariant();
            if (String.Equals(original, destination, StringComparison.OrdinalIgnoreCase) || File.Exists(destination) || Directory.Exists(destination))
                throw new IOException("目标已存在。请为导出照片选择新的文件名。");
            BitmapEncoder encoder;
            bool jpeg = false;
            switch (ext)
            {
                case ".jpg": case ".jpeg": case ".jpe": encoder = new JpegBitmapEncoder { QualityLevel = quality }; jpeg = true; break;
                case ".png": encoder = new PngBitmapEncoder(); break;
                case ".tif": case ".tiff": encoder = new TiffBitmapEncoder { Compression = TiffCompressOption.Zip }; break;
                default: throw new NotSupportedException("可导出 JPEG、16 位 PNG 或 16 位 TIFF。");
            }
            if(maxBytes<0 || maxBytes>500000000 || minEdge<0 || minEdge>65535 || !jpeg&&maxBytes!=0)
                throw new ArgumentException("体积上限仅适用于 JPEG。");
            if(measuredBytes<0||measuredBytes>maxBytes)throw new ArgumentException("JPEG 试算值超出体积上限。");
            var profile=OutputProfile(colorSpace);var result=render();
            BitmapFrame frame;ExportMeasure details;
            if(maxBytes>0&&measuredBytes==0){
                var fits=FitJpeg(result,longEdge,quality,maxBytes,minEdge,false,watermark,colorSpace,profile,matte,cancel,out frame);
                if(fits.Count==0)throw new InvalidOperationException("所选尺寸下 JPEG 仍超过体积上限。请提高上限，或允许使用更小的尺寸。");
                details=fits[0];encoder=new JpegBitmapEncoder{QualityLevel=details.quality};
            }else{
                frame=PrepareExportFrame(result,longEdge,watermark,colorSpace,profile,jpeg,matte,cancel);
                details=new ExportMeasure{width=frame.PixelWidth,height=frame.PixelHeight,quality=jpeg?quality:0};
            }
            string temporary = Path.Combine(Path.GetDirectoryName(destination), ".aips-" + Guid.NewGuid().ToString("N") + ".tmp");
            bool owned = false;
            try
            {
                using (var file = new FileStream(temporary, FileMode.CreateNew, prepared==null?FileAccess.Write:FileAccess.ReadWrite, FileShare.None))
                {
                    owned = true;
                    if(staging!=null)staging(temporary,file);
                    cancel.ThrowIfCancellationRequested();
                    SaveFrame(encoder,frame,file,cancel);
                    details.bytes=file.Length;
                    if(maxBytes>0 && details.bytes>maxBytes)throw new IOException("实际编码文件超出体积上限，请重新选择尺寸。");
                    if(measuredBytes>0 && details.bytes!=measuredBytes)throw new IOException("实际文件与试算不一致，请重新试算再保存。");
                    file.Flush(true);
                    if(prepared!=null)prepared(file);
                }
                cancel.ThrowIfCancellationRequested();
                // File.Move on .NET Framework fails atomically if any file, link or alias occupies the destination.
                File.Move(temporary, destination);
                owned = false;
                return details;
            }
            finally { if (owned) File.Delete(temporary); }
        }

        public static int[][] Histogram(BitmapSource image)
        {
            if (image == null) throw new ArgumentNullException("image");
            var source = AsFormat(image, PixelFormats.Rgba64);
            var bins = new[] { new int[256], new int[256], new int[256] };
            int w = source.PixelWidth, h = source.PixelHeight;
            int rows = Math.Min(h, Math.Max(1, Math.Min(64, 1048576 / (8 * w))));
            var samples = new ushort[rows * w * 4];
            for (int y = 0; y < h; y += rows)
            {
                int n = Math.Min(rows, h - y);
                source.CopyPixels(new Int32Rect(0, y, w, n), samples, w * 8, 0);
                for (int i = 0; i < n * w * 4; i += 4)
                {
                    if (samples[i + 3] == 0) continue;
                    bins[0][(samples[i] + 128) / 257]++; bins[1][(samples[i + 1] + 128) / 257]++; bins[2][(samples[i + 2] + 128) / 257]++;
                }
            }
            return bins;
        }

        public static Dictionary<string, double> PickWhiteBalance(BitmapSource image, int x, int y, IDictionary<string, double> current)
        {
            if (image == null) throw new ArgumentNullException("image");
            if (x < 0 || x >= image.PixelWidth || y < 0 || y >= image.PixelHeight) throw new ArgumentOutOfRangeException("x/y");
            var source = AsFormat(image, PixelFormats.Rgba64);
            int left = Math.Max(0, x - 2), top = Math.Max(0, y - 2), width = Math.Min(image.PixelWidth, x + 3) - left, height = Math.Min(image.PixelHeight, y + 3) - top;
            var buffer = new ushort[width * height * 4];
            source.CopyPixels(new Int32Rect(left, top, width, height), buffer, width * 8, 0);
            double r = 0, g = 0, b = 0, weight = 0;
            for (int i = 0; i < buffer.Length; i += 4)
            {
                if (buffer[i] >= 65000 || buffer[i + 1] >= 65000 || buffer[i + 2] >= 65000) continue;
                double a = buffer[i + 3] / 65535.0;
                r += ImagingRecipe.Linear[buffer[i]] * a; g += ImagingRecipe.Linear[buffer[i + 1]] * a; b += ImagingRecipe.Linear[buffer[i + 2]] * a;
                weight += a;
            }
            if (weight < .1 || Math.Min(r, Math.Min(g, b)) < weight * .003)
                throw new InvalidOperationException("请点选有细节、未过曝且不透明的中性灰或白色区域。");
            double temp = 0, tint = 0;
            if (current != null) { current.TryGetValue("temperature", out temp); current.TryGetValue("tint", out tint); }
            temp += Math.Log(b / r, 2) * 100 / 1.7;
            tint += Math.Log(g / Math.Sqrt(r * b), 2) * 100 / .8;
            return new Dictionary<string, double> { {"temperature", Math.Max(-100, Math.Min(100, Math.Round(temp)))},
                {"tint", Math.Max(-100, Math.Min(100, Math.Round(tint)))} };
        }
    }
}
