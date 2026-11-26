using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AIPhotoStudio
{
    internal sealed partial class StudioBridge
    {
        private readonly IWin32Window owner;
        private readonly StudioWorkspace work;
        private readonly string channelDirectory;
        private readonly SemaphoreSlim renderSlots = new SemaphoreSlim(2);
        private readonly CancellationTokenSource closing = new CancellationTokenSource();
        private readonly HashSet<string> extensions = new HashSet<string>(".jpg .jpeg .png .tif .tiff .heic .heif .nef .nrw .cr2 .cr3 .crw .arw .sr2 .srf .raf .rw2 .orf .dng".Split(' '), StringComparer.OrdinalIgnoreCase);
        private readonly string samplePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Samples", "Stillwater.png");

        internal StudioBridge(IWin32Window owner, StudioWorkspace workspace, string root)
        {
            this.owner = owner;
            work = workspace;
            english=work.ReadLanguage().language=="en";
            channelDirectory = StudioWorkspace.ChannelDirectory(root, Program.Channel);
            galleryCache = new GalleryPreviewCache(channelDirectory);
            GpuColorPass.CpuOnly=work.CpuOnly;
            Imaging.ConfigureCubeLibrary(Path.Combine(channelDirectory,"Presets","LUT"));
            LocalEdits.Configure(Path.Combine(channelDirectory,"Projects","Masks"));
            SegmentAssets.Configure(Path.Combine(channelDirectory,"Projects","Segments"));
            BodyAssets.Configure(Path.Combine(channelDirectory,"Projects","Bodies"));
            PortraitAssets.Configure(Path.Combine(channelDirectory,"Projects","Faces"));
            StartOriginalIndex(false);
        }
        internal void Stop() { closing.Cancel();Imaging.SelectCameraDetail(null); }
        internal static JavaScriptSerializer Json() { return new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024, RecursionLimit = 64 }; }
        internal static string TextOf(Dictionary<string, object> a, string key)
        {
            object value;
            return a.TryGetValue(key, out value) && value is string ? (string)value : "";
        }
        internal static Dictionary<string, object> Map(object value)
        {
            return value as Dictionary<string, object> ?? Json().ConvertToType<Dictionary<string, object>>(value);
        }
        private static string[] Strings(object values)
        {
            var seq = values as IEnumerable;
            return seq == null || values is string ? new string[0] : seq.Cast<object>().OfType<string>().ToArray();
        }

        internal async Task<string> Receive(string payload)
        {
            string id = "",method="";
            try
            {
                if (payload == null || payload.Length > 4*LocalEdits.MaxMaskBytes) throw new ArgumentException("请求过大。");
                var msg = Json().Deserialize<Dictionary<string, object>>(payload);
                id = TextOf(msg, "requestId");
                if (id.Length < 1 || id.Length > 128) throw new ArgumentException("无效的请求编号。");
                object fields;
                var args = msg.TryGetValue("args", out fields) && fields is Dictionary<string, object> ? Map(fields) : new Dictionary<string, object>();
                method = TextOf(msg, "method");
                int limit=method=="draftWrite"?4*LocalEdits.MaxMaskBytes:method=="localSave"||method=="localPreview"?LocalEdits.MaxMaskBytes+65536:
                    method=="bodySave"||method=="relinkApply"?512*1024:256*1024;
                if(payload.Length>limit)throw new ArgumentException("请求过大。");
                string catalogBase=method=="commit"?TextOf(msg,"catalogBase"):null;
                object result = await Invoke(method, args,String.IsNullOrEmpty(catalogBase)?null:catalogBase).ConfigureAwait(false);
                if (method == "preview" || method == "localPreview" || method == "viewport")
                    return PreviewReply(id, (Dictionary<string, object>)result);
                // Each page obtains the complete control definitions on its first snapshot.
                // Direct bridge callers and freshly loaded pages still receive the full response.
                var snapshot = result as Dictionary<string, object>;
                object known, definitions;
                if (msg.TryGetValue("hasControlDefinitions", out known) && Object.Equals(known, true) &&
                    snapshot != null && snapshot.TryGetValue("controls", out definitions) && Object.ReferenceEquals(definitions, Imaging.Controls))
                {
                    snapshot.Remove("controls");
                    snapshot["reuseControlDefinitions"] = true;
                }
                var ordered=snapshot as StudioWorkspace.WorkspaceSnapshot;
                if(ordered!=null && msg.TryGetValue("catalogSync",out known) && Object.Equals(known,true))
                    return Json().Serialize(new {requestId=id,ok=true,result=result,catalogSession=ordered.Session,catalogOrder=ordered.Order});
                return Json().Serialize(new { requestId = id, ok = true, result = result });
            }
            catch (Exception ex)
            {
                return Json().Serialize(new { requestId = id, ok = false, error = LocalMessage(ex.GetBaseException().Message,method) });
            }
        }

        private Dictionary<string, object> Snapshot(object state = null)
        {
            var value = Map(state ?? work.Snapshot());
            CancelOtherPhotoPreviews(TextOf(value, "selectedId"));
            value["channel"] = Program.Channel;
            value["version"] = Program.Version;
            value["dataRoot"] = channelDirectory;
            value["peoplePreference"]=work.PeopleSetting(TextOf(value,"selectedId"));
            value["libraryTools"]=true;
            value["controls"] = Imaging.Controls;
            var document=value["document"] as IDictionary<string,object>;
            var parameters=document==null?null:document["parameters"] as IDictionary<string,double>;
            var bodyPeople=BodyAssets.Describe(parameters);
            value["bodyPeople"]=bodyPeople;
            var portraitPeople=PortraitAssets.Describe(parameters);
            value["portraitPeople"]=portraitPeople;
            value["people"]=PhotoPeople.Describe(parameters,bodyPeople,portraitPeople);
            value["portraitGeometry"]=portraitPeople.Length==0?null:PortraitAssets.Resolve(parameters).GeometryHash;
            value["portraitLandmarks"]=true;
            value["presets"] = Imaging.Presets.Select(p => new { name = p.Key, values = p.Value }).ToArray();
            value["providerName"] = Planner.Name(channelDirectory);
            value["provider"] = new { name = value["providerName"] };
            value["renderMode"] = GpuColorPass.CpuOnly ? "cpu" : "auto";
            value["capabilities"] = new { viewport = true, capture = true, batchApply = true, providerSettings = true, whiteBalance = true, colorBandPicking=true, recipes = true, importLut = true, localEdits = true, areaTools=true, portraitDetection = true, matchColor = true, copyRecipe = true, segmentation = true, libraryRemoval = true, body = true, setReview = true, relinkOriginals = true, deliveryPresets=true };
            return value;
        }

        internal async Task<object> Invoke(string method, Dictionary<string, object> args,string catalogBase=null)
        {
            string id = TextOf(args, "id");
            switch (method)
            {
                case "init": var initial=Snapshot();initial["draftIndex"]=work.DraftIndex();initial["languagePreference"]=ApplyLanguagePreference(work.ReadLanguage());return initial;
                case "setLanguagePreference": return SetLanguagePreference(args);
                case "draftRead": case "draftWrite": return work.EditDraft(id,TextOf(args,"kind"),TextOf(args,"content"),method=="draftWrite");
                case "copySignature": case "copyDetail": return await CompareSource(args,method=="copyDetail");
                case "libraryBackup": case "libraryRestore": case "libraryOpenRestored": case "libraryDisk":
                case "libraryClearPreviews": case "libraryThumbnail": case "libraryPeople": return await LibraryAction(method,args);
                case "captureWatch": case "captureStop": case "capturePoll": case "captureCameras": case "captureShutter":
                    return await CaptureAction(method,args);
                case "setRenderMode":
                    string mode=TextOf(args,"mode");
                    if(mode!="cpu" && mode!="auto")throw new ArgumentException("未知的渲染方式。");
                    work.CpuOnly=mode=="cpu"; GpuColorPass.CpuOnly=mode=="cpu";
                    return Snapshot();
                case "relinkScan": case "relinkCancel": case "relinkPick": case "relinkPreview": case "relinkApply": case "relinkPrepare":
                    return await RelinkAction(method, args);
                case "sample": return await Import(new[] { samplePath });
                case "importFiles":
                    using (var picker = new OpenFileDialog { Multiselect = true, CheckFileExists = true, Title = L("导入照片","Import photos"),
                        Filter = L("支持的照片|","Supported photos|") + String.Join(";", extensions.Select(e => "*" + e)) + L("|所有文件|*.*","|All files|*.*") })
                        return picker.ShowDialog(owner) == DialogResult.OK ? await Import(picker.FileNames) : Snapshot();
                case "importFolder":
                    using (var picker = new FolderBrowserDialog { Description = L("导入此文件夹中的照片（当前层）","Import photos from this folder (top level only)"), ShowNewFolderButton = false })
                        return picker.ShowDialog(owner) == DialogResult.OK ? await Import(Directory.EnumerateFiles(picker.SelectedPath).Where(p => extensions.Contains(Path.GetExtension(p)))) : Snapshot();
                case "preview": return await Preview(args);
                case "viewport": return await PreviewViewport(args);
                case "inputGamut": return await CheckInputGamut(id);
                case "cameraPoster": return await CameraPoster(args);
                case "localData": return MaskData(args);
                case "brushEdgeInput": return await BrushEdgeInput(args);
                case "areaData": return AreaData(args);
                case "areaSave": return SaveAreas(args);
                case "maskInput": return await MaskInput(args);
                case "maskRangeInput": return await RangeInput(args);
                case "maskRangePreview": return RangePreview(args);
                case "maskPreview": return MaskPreview(args);
                case "maskManage": return ManageMask(args);
                case "localPreview": return await LocalPreview(args);
                case "localSave": return SaveLocal(args);
                case "importLut": return ImportLut(args);
                case "analyzeFaces": return await AnalyzeFaces(args);
                case "portraitInput": return await BodyInput(args,true);
                case "portraitSave": return await PortraitSave(args);
                case "personRegionInput": return await PersonRegionInput(args);
                case "personRegionPreview": return await PersonRegionPreview(args);
                case "personRegionSave": return await PersonRegionSave(args);
                case "personRegionCancel": return CancelPersonRegion(args);
                case "portraitSpotFind": return await FindPortraitSpots(args);
                case "portraitKeepInput": return await PortraitKeepInput(args);
                case "portraitKeepSave": return SavePortraitKeep(args);
                case "portraitSpotPreview": return await PreviewPortraitSpots(args);
                case "portraitSpotCancel": return CancelPortraitSpots(args);
                case "portraitSpotSave": return SavePortraitSpots(args);
                case "segmentPreview": return await PreviewSegment(args);
                case "segmentSave": return SaveSegment(args);
                case "bodyInput": return await BodyInput(args);
                case "bodySave": return await BodySave(args);
                case "autoBody": return AutoBody(args);
                case "matchColor": return await MatchColor(args);
                case "setReviewRows": return work.SetReviewRows(args);
                case "setReviewEye": return work.SetReviewEye(args);
                case "setReviewSensitivity": return work.SetReviewSensitivity(args);
                case "setReviewField": work.SetReviewField(args); return true;
                case "setReviewInspect": return await InspectSetPhoto(args);
                case "setReviewExpression": return await ReadReviewExpression(args);
                case "setReviewFaces": return await Task.Run(()=>SetReviewFaces(args));
                case "setMatchStart": return await BeginSetMatch(args);
                case "setMatchAreaInput": return await SetMatchAreaInput(args);
                case "setMatchSuggest": return await SuggestSetMatchAreas(args);
                case "setMatchPhoto": return await FitSetPhoto(args);
                case "setMatchPreview": return await PreviewSetMatch(args);
                case "setMatchApply": return await ConfirmSetMatch(args);
                case "selectFace": return SelectFace(args);
                case "plan": return await MakePlan(args);
                case "discardProposal": return work.DiscardProposal(args);
                case "confirmPreset": return ConfirmPreset(args);
                case "pickWhite": return await PickWhite(args);
                case "sampleWhiteCandidate": return await SampleWhiteCandidate(args);
                case "colorBandInput": case "sampleColorBand": return await InspectColorBand(method,args);
                case "jpegExportPlan": return await PlanJpeg(args);
                case "prepareShareProof": return await PrepareShareProof(args);
                case "shareProofSample": return await ShareProofSample(args);
                case "releaseShareProof": return ReleaseProof(TextOf(args,"ticket"));
                case "copyFinalImage": return await CopyFinalImage(args);
                case "lastSingleExport": case "openLastSingleExport": return await ReadLastExport(args,method=="openLastSingleExport");
                case "cancelJpegPlan": return CancelJpegPlan(TextOf(args,"ticket"));
                case "export": work.EnsureNoPrivacyDrafts(new[]{id});return await ExportOne(id,ExportSettings.Read(args),args.ContainsKey("revision")?(long?)Convert.ToInt64(args["revision"]):null);
                case "batchExport": return await BatchExport(Strings(args["ids"]),ExportSettings.Read(args),TextOf(args,"batchToken"));
                case "deliveryPickLogo": case "deliveryLogoPreview":
                case "deliveryPresets": case "deliveryPhotoIds": case "deliverySave": case "deliverySelect": case "deliveryStart": case "deliveryResume": return await Delivery(method,args);
                case "batchExportStatus": return BatchStatus(TextOf(args,"batchToken"),false);
                case "cancelBatchExport": return BatchStatus(TextOf(args,"batchToken"),true);
                case "batchApply": return BatchApply(args);
                case "exportRecipe": return RecipeFile(args, false);
                case "importRecipe": return RecipeFile(args, true);
                case "providerSettings": Planner.Configure(owner, channelDirectory,english); return Snapshot();
                case "removedList": return work.Dispatch(method,args);
                case "setPhotoNote": case "select": case "favorite": case "removePhoto": case "restorePhoto": case "undo": case "redo": case "restore":
                case "resetParam": case "undoParam": case "redoParam": case "lock": case "savePreset":
                case "createProject": case "renameProject": case "setProjectPhoto": case "copyRecipe":
                    return Snapshot(work.Dispatch(method, args));
                case "commit": return UserCommit(args,catalogBase);
                default: throw new ArgumentException("此版本不支持该操作。");
            }
        }

        private async Task<object> Import(IEnumerable<string> files)
        {
            var errors = new List<string>();
            int added = 0;
            await Task.Run(delegate
            {
                foreach (string file in files)
                {
                    closing.Token.ThrowIfCancellationRequested();
                    try { work.ImportOriginal(file); added++; }
                    catch (Exception ex) { errors.Add(Path.GetFileName(file) + " · " + LocalMessage(ex.GetBaseException().Message,"importFiles")); }
                }
            });
            if (added == 0 && errors.Count > 0) throw new InvalidDataException(String.Join("\n", errors.Take(8)));
            if (added == 0) throw new InvalidDataException("所选文件夹中没有可导入的照片。");
            var result = Snapshot();
            result["importErrors"] = errors;
            if (errors.Count > 0 && owner != null) MessageBox.Show(owner, String.Join("\n", errors.Take(10)), L("部分照片未导入","Some photos could not be imported"));
            return result;
        }

        private Dictionary<string, object> Selected(Dictionary<string, object> args)
        {
            return work.SelectedDocument(TextOf(args,"id"),args.ContainsKey("revision")?(long?)Convert.ToInt64(args["revision"]):null);
        }

        private async Task<BitmapSource> Render(string path, Dictionary<string, double> values, int edge)
        {
            await renderSlots.WaitAsync(closing.Token);
            try { return await Task.Run(() => Imaging.Render(path, values, edge, closing.Token)); }
            finally { renderSlots.Release(); }
        }

        private static int[] Histogram(BitmapSource photo)
        {
            // Rec.709 luminance on the display-referred sRGB preview.
            var src = new FormatConvertedBitmap(photo, PixelFormats.Bgra32, null, 0);
            int[] bins = new int[256];
            int stride = src.PixelWidth * 4;
            byte[] row = new byte[stride];
            for (int y = 0; y < src.PixelHeight; y++)
            {
                src.CopyPixels(new System.Windows.Int32Rect(0, y, src.PixelWidth, 1), row, stride, 0);
                for (int off = 0; off < stride; off += 4)
                    if (row[off + 3] != 0) bins[(54 * row[off + 2] + 183 * row[off + 1] + 19 * row[off] + 128) >> 8]++;
            }
            return bins;
        }

        private async Task<object> Preview(Dictionary<string, object> args)
        {
            string id = TextOf(args, "id");
            bool original = args.ContainsKey("original") && Object.Equals(args["original"], true);
            bool interactive = !original && InteractivePreview(args);
            string path=work.PathFor(id);
            string frameView=TextOf(args,"frameView");
            var values = original && frameView=="output" ? FrameGeometry.NeutralValues(ReadPreviewValues(args,false),true) :
                original ? work.NeutralFor(id,path,args.ContainsKey("revision")?(long?)Convert.ToInt64(args["revision"]):null) : ReadPreviewValues(args, false);
            if(!original)values=LocalEdits.PreviewWithoutMask(values,BypassedMask(args));
            if(!original&&frameView=="source")values=FrameGeometry.SourceValues(values);
            int edge = PreviewEdge(args, interactive);
            var response = await RenderPreview(path, values, edge, interactive, null,
                original ? null : id, edge <= 144 && (original || TextOf(args,"profile")=="presetSwatch"),clipping:original?0:ClippingDisplay.Flags(args)).ConfigureAwait(false);
            if (!original) work.VerifyPreviewRevision(id, Convert.ToInt64(args["revision"]));
            return response;
        }

        private async Task<object> MakePlan(Dictionary<string, object> args)
        {
            var doc = Selected(args);
            string presetName;
            if(Planner.TryPresetName(TextOf(args,"text"),out presetName))return ProposePreset(args,presetName);
            var values = Json().ConvertToType<Dictionary<string,double>>(doc["parameters"]);
            var history = work.PlannerHistory(TextOf(args,"id"));
            if(TextOf(args,"scope")=="all"&&Planner.IsHalfGroup(TextOf(args,"text")))
            {
                var half=await Planner.PlanAsync(channelDirectory,TextOf(args,"text"),"all",values,Strings(doc["locks"]),null,history);
                return work.IssueProposal(args,(Dictionary<string,double>)half["values"],TextOf(half,"label"),TextOf(half,"message"));
            }
            var picture = await Render(work.PathFor(TextOf(args, "id")), FrameGeometry.SourceValues(values), 256);
            var bins = Histogram(picture);
            long total = bins.Sum(v => (long)v);
            var analysis = new Dictionary<string, object> { {"histogram",bins}, {"meanLuminance",total == 0 ? .5 : bins.Select((n,i) => (double)n*i).Sum()/(255*total)} };
            analysis["hasSegment"] = SegmentAssets.Supported(values);
            bool hasBody=BodyAssets.Supported(values);
            analysis["hasBody"] = hasBody;
            analysis["bodyPersonIndex"] = hasBody?BodyPersonIndex(args,values,false):-1;
            analysis["faceIndex"]=PortraitIndex(args,values);
            PhotoPeople.SetPlanTarget(args,values,analysis);
            var plan = await PlanSelectedMask(args,values,Strings(doc["locks"]),analysis,history,picture.PixelWidth,picture.PixelHeight);
            return work.IssueProposal(args,(Dictionary<string,double>)plan["values"],TextOf(plan,"label"),TextOf(plan,"message"));
        }

        private async Task<object> PickWhite(Dictionary<string, object> args)
        {
            var doc = Selected(args);
            var values = Json().ConvertToType<Dictionary<string,double>>(doc["parameters"]);
            var bitmap = await Render(work.PathFor(TextOf(args,"id")),TextOf(args,"frameView")=="source"?FrameGeometry.SourceValues(values):values,1200);
            int x = Coordinate(args["x"], bitmap.PixelWidth), y = Coordinate(args["y"], bitmap.PixelHeight);
            var patch = Imaging.PickWhiteBalance(bitmap,x,y,values);
            return CommitPatch(args,patch,"白平衡吸管");
        }
        private static int Coordinate(object value, int size)
        {
            double v = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            if (Double.IsNaN(v) || v < 0 || v > 1) throw new ArgumentException("取样点超出照片。");
            return Math.Min(size-1,(int)(v*size));
        }
        private object CommitPatch(Dictionary<string,object> args,Dictionary<string,double> patch,string label)
        {
            var cmd = new Dictionary<string,object> { {"id",args["id"]},{"revision",args["revision"]} };
            cmd["label"] = label; cmd["source"] = "manual"; cmd["requestId"] = Guid.NewGuid().ToString("N");
            cmd["values"] = Json().DeserializeObject(Json().Serialize(patch));
            return Snapshot(work.Dispatch("commit",cmd));
        }
    }
}
