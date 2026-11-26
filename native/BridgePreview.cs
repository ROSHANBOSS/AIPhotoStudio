using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AIPhotoStudio
{
    internal sealed partial class StudioBridge
    {
        private readonly SemaphoreSlim thumbnailSlot = new SemaphoreSlim(1);
        private readonly Imaging.DragCache dragCache=new Imaging.DragCache();
        private readonly Dictionary<CancellationTokenSource, string> selectedPreviews = new Dictionary<CancellationTokenSource, string>();
        private string lastPreviewSelection;

        private void CancelOtherPhotoPreviews(string selectedId)
        {
            lock (selectedPreviews)
            {
                if (lastPreviewSelection == selectedId) return;
                lastPreviewSelection = selectedId;
                Imaging.SelectCameraDetail(String.IsNullOrEmpty(selectedId)?null:work.CameraSelectionPath(selectedId));
                // A revision change on this photo can still use its shared neutral RAW
                // decode. Selection changes release workers belonging to the old photo.
                foreach (var entry in new List<KeyValuePair<CancellationTokenSource, string>>(selectedPreviews))
                    if (entry.Value != selectedId) entry.Key.Cancel();
                detailedStages.Clear();
                if(viewportSlot.Wait(0))
                {
                    try{cameraView=null;fullView=null;fullViewKey=null;}
                    finally{viewportSlot.Release();}
                }
            }
        }

        private static bool InteractivePreview(Dictionary<string, object> args)
        {
            return TextOf(args, "profile") == "interactive";
        }

        private static int PreviewEdge(Dictionary<string, object> args, bool interactive)
        {
            int maximum = interactive ? 512 : 2048;
            int requested = args.ContainsKey("maxEdge") ? Convert.ToInt32(args["maxEdge"]) : 1200;
            return Math.Max(32, Math.Min(maximum, requested));
        }

        private Dictionary<string, double> ReadPreviewValues(Dictionary<string, object> args, bool localDraft)
        {
            if (!args.ContainsKey("revision")) throw new ArgumentException("预览需要当前照片的修订。");
            object raw;
            var patch = args.TryGetValue("values", out raw) && raw != null ? Map(raw) : null;
            return work.PreviewValues(TextOf(args, "id"), Convert.ToInt64(args["revision"]), patch, localDraft);
        }

        private async Task<object> RenderPreview(string path, Dictionary<string, double> values, int edge, bool interactive, LocalEdits.MaskSet draft, string selectedId, bool thumbnail,PortraitSpot[] spotDraft=null,int clipping=0)
        {
            var lifetime = selectedId == null ? null : CancellationTokenSource.CreateLinkedTokenSource(closing.Token);
            if (lifetime != null) lock (selectedPreviews) selectedPreviews.Add(lifetime, selectedId);
            var token = lifetime == null ? closing.Token : lifetime.Token;
            bool thumbAcquired = false, slotAcquired = false;
            try
            {
                // A second background thumbnail waits outside the renderer queue, so
                // the visible canvas can use the other slot with a large catalog.
                if (thumbnail) { await thumbnailSlot.WaitAsync(token).ConfigureAwait(false); thumbAcquired = true; }
                await renderSlots.WaitAsync(token).ConfigureAwait(false);
                slotAcquired = true;
                return await Task.Run<object>(delegate
                {
                    var image = interactive && spotDraft==null ? Imaging.RenderDrag(path,values,edge,token,draft,dragCache) :
                        Imaging.Render(path, values, edge, token, draft,spotDraft);
                    token.ThrowIfCancellationRequested();
                    var frame=EncodeFrame(image, interactive, token, clipping);
                    if(!thumbnail&&selectedId!=null&&edge>=320)Imaging.CameraOverviewVisible(path);
                    return frame;
                }, token).ConfigureAwait(false);
            }
            finally
            {
                if (slotAcquired) renderSlots.Release();
                if (thumbAcquired) thumbnailSlot.Release();
                if (lifetime != null) lock (selectedPreviews) { selectedPreviews.Remove(lifetime); lifetime.Dispose(); }
            }
        }

        private static Task<object> EncodePreview(BitmapSource image)
        {
            return Task.Run<object>(() => EncodeFrame(image, false, CancellationToken.None));
        }

        private static string PreviewReply(string requestId, Dictionary<string, object> frame)
        {
            // Encode the caller-supplied identifier normally. The large image field is
            // our own fixed MIME prefix and RFC 4648 Base64, with no JSON escapes.
            var json = Json();
            object timing;
            return String.Concat("{\"requestId\":", json.Serialize(requestId), ",\"ok\":true,\"result\":{",
                frame.TryGetValue("cameraTiming",out timing)?"\"cameraTiming\":"+json.Serialize(timing)+",":"",
                "\"width\":",
                Convert.ToString(frame["width"], CultureInfo.InvariantCulture), ",\"height\":",
                Convert.ToString(frame["height"], CultureInfo.InvariantCulture), ",\"histogram\":",
                json.Serialize(frame["histogram"]), ",\"image\":\"", (string)frame["image"], "\"}}");
        }

        private static Dictionary<string, object> EncodeFrame(BitmapSource image, bool interactive, CancellationToken token,int clipping=0)
        {
            int width = image.PixelWidth, height = image.PixelHeight, stride = checked(width * 4);
            var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
            var display = new CachedBitmap(converted, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            int rows = Math.Max(1, Math.Min(height, 65536 / stride));
            var pixels = new byte[checked(stride * rows)];
            var bins = new int[256];
            bool opaque = true;
            for (int y = 0; y < height; y += rows)
            {
                token.ThrowIfCancellationRequested();
                int count = Math.Min(rows, height - y);
                display.CopyPixels(new System.Windows.Int32Rect(0, y, width, count), pixels, stride, 0);
                for (int i = 0; i < count * stride; i += 4)
                {
                    if (pixels[i + 3] != 255) opaque = false;
                    if (pixels[i + 3] != 0) bins[(54 * pixels[i + 2] + 183 * pixels[i + 1] + 19 * pixels[i] + 128) >> 8]++;
                }
            }
            // The encoder and the histogram share one BGRA conversion. Transparent
            // previews retain alpha and histogram scratch stays below the large-object heap.
            bool jpeg = interactive && opaque && clipping==0;
            BitmapEncoder encoder = jpeg ? (BitmapEncoder)new JpegBitmapEncoder { QualityLevel = 83 } : new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(ClippingDisplay.Mark(image,display,clipping,token)));
            using (var buffer = new MemoryStream())
            {
                encoder.Save(buffer);
                token.ThrowIfCancellationRequested();
                string encoded = Convert.ToBase64String(buffer.GetBuffer(), 0, checked((int)buffer.Length));
                return new Dictionary<string, object> {
                    {"image", (jpeg ? "data:image/jpeg;base64," : "data:image/png;base64,") + encoded},
                    {"histogram", bins}, {"width", width}, {"height", height}
                };
            }
        }
    }

    public sealed partial class StudioWorkspace
    {
        internal Dictionary<string,double> NeutralFor(string id,string path,long? expectedRevision)
        {
            if(!String.Equals(System.IO.Path.GetExtension(path),".cr3",StringComparison.OrdinalIgnoreCase))
                return new Dictionary<string,double>();
            lock(gate)
            {
                State state=Current(id);
                if(expectedRevision.HasValue)CheckRevision(state,expectedRevision.Value);
                return CameraRawModes.Neutral(state.Values);
            }
        }
        // Per-frame validation must not construct the whole catalog, history, and presets.
        internal void VerifyPreviewRevision(string id, long revision)
        {
            lock (gate)
            {
                object current = db.Scalar("SELECT p.revision FROM photos p JOIN settings s ON s.name='selected' AND s.value=p.id WHERE p.id=?", id);
                if (current == null) throw new InvalidOperationException("所选照片已改变。");
                if ((long)current != revision) throw new InvalidOperationException("照片已更新，请基于当前修订重新操作。");
            }
        }

        internal Dictionary<string, double> PreviewValues(string id, long revision, Dictionary<string, object> patch, bool localDraft)
        {
            lock (gate)
            {
                VerifyPreviewRevision(id, revision);
                if (patch != null && patch.Count > 0)
                {
                    var request = new Dictionary<string, object> { {"id", id}, {"revision", revision}, {"values", patch} };
                    var proposal = (Dictionary<string, object>)Dispatch("previewProposal", request);
                    return (Dictionary<string, double>)proposal["parameters"];
                }
                State state = Current(id);
                if (localDraft) CheckEngine(state.Engine);
                return state.Values;
            }
        }
    }
}
