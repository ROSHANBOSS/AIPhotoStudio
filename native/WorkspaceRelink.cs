using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace AIPhotoStudio
{
    public sealed partial class StudioWorkspace
    {
        private readonly object relinkGate = new object();
        private RelinkPlan relinkPlan;

        private sealed class CandidateFile
        {
            internal int Index;
            internal string Path, Identity, Hash;
            internal long Bytes;
            internal Dictionary<string,object> Info;
            internal bool Inspected;
        }
        private sealed class CandidateOption
        {
            internal CandidateFile File;
            internal string Match, Detail;
        }
        private sealed class MissingOriginal
        {
            internal OriginalRecord Original;
            internal string Status;
            internal Availability Observed;
            internal bool Blocked { get { return Observed==Availability.Blocked; } }
            internal bool More;
            internal int? Suggested, Picked;
            internal List<CandidateOption> Options = new List<CandidateOption>();
            internal HashSet<int> Viewed = new HashSet<int>();
        }
        private sealed class RelinkPlan
        {
            internal string Token, Folder;
            internal DateTime Created;
            internal int Connected, Scanned, Skipped;
            internal List<MissingOriginal> Rows = new List<MissingOriginal>();
            internal List<CandidateFile> Files = new List<CandidateFile>();
            internal ILookup<string,string> Paths, Identities;
        }
        internal sealed class RelinkChoice
        {
            public int Row, Candidate;
            public bool Manual;
        }

        private enum Availability { Connected, Missing, Unverified, Blocked }

        private static Availability OriginalAvailability(OriginalRecord record, CancellationToken token = default(CancellationToken))
        {
            try
            {
                using (var file = new OriginalFile(record.Path))
                {
                    if (!MatchesOriginal(record,file,token)) return Availability.Missing;
                    return record.Hash==null && record.Identity==null ? Availability.Unverified : Availability.Connected;
                }
            }
            catch (FileNotFoundException) { return Availability.Missing; }
            catch (DirectoryNotFoundException) { return Availability.Missing; }
            catch (Exception ex)
            {
                if (!FileAccessError(ex)) throw;
                var os = ex.InnerException as Win32Exception;
                // Includes an unplugged disk, disconnected share or unavailable volume.
                return os != null && new[] {15,21,53,55,67,1167,1231}.Contains(os.NativeErrorCode)
                    ? Availability.Missing : Availability.Blocked;
            }
        }

        internal object ScanRelink(string folder, ISet<string> extensions, CancellationToken token)
        {
            lock (relinkGate)
            {
                var plan = new RelinkPlan { Folder=WorkspacePaths.Full(folder) };
                List<OriginalRecord> records;
                lock (gate)
                {
                    var owners = db.Rows("SELECT id,path,identity FROM photos");
                    plan.Paths = owners.ToLookup(r=>(string)r[1], r=>(string)r[0], StringComparer.OrdinalIgnoreCase);
                    plan.Identities = owners.Where(r=>r[2]!=null).ToLookup(r=>(string)r[2], r=>(string)r[0]);
                    records = db.Rows(SourceColumns + "WHERE " + VisiblePhotos + " ORDER BY p.rowid").Select(SourceFrom).ToList();
                }
                foreach (var record in records)
                {
                    token.ThrowIfCancellationRequested();
                    var availability = OriginalAvailability(record,token);
                    if (availability == Availability.Connected) plan.Connected++;
                    else plan.Rows.Add(new MissingOriginal { Original=record, Observed=availability });
                }
                if (plan.Rows.Any(r=>!r.Blocked)) FindCandidates(plan, extensions, token);
                PrepareChoices(plan,token);
                return PublishRelink(plan);
            }
        }

        private static void FindCandidates(RelinkPlan plan, ISet<string> extensions, CancellationToken token)
        {
            var wanted = plan.Rows.Where(r=>!r.Blocked).Select(r=>r.Original).ToList();
            var names = new HashSet<string>(wanted.Select(r=>Path.GetFileName(r.Path)), StringComparer.OrdinalIgnoreCase);
            var sizes = new HashSet<long>(wanted.Where(r=>r.KnownBytes.HasValue).Select(r=>r.KnownBytes.Value));
            bool unknownSize = wanted.Any(r=>r.Hash!=null && !r.KnownBytes.HasValue);
            var directories = new Stack<string>();
            directories.Push(plan.Folder);
            while (directories.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                string directory = directories.Pop();
                try
                {
                    foreach (var entry in Directory.EnumerateFileSystemEntries(WorkspacePaths.ForIO(directory)))
                    {
                        token.ThrowIfCancellationRequested();
                        try
                        {
                            var attributes = File.GetAttributes(entry);
                            if ((attributes & FileAttributes.ReparsePoint) != 0) { plan.Skipped++; continue; }
                            string path = WorkspacePaths.Full(entry);
                            if ((attributes & FileAttributes.Directory) != 0) { directories.Push(path); continue; }
                            if (!extensions.Contains(Path.GetExtension(path))) continue;
                            plan.Scanned++;
                            if (unknownSize || names.Contains(Path.GetFileName(path)) || sizes.Contains(new FileInfo(entry).Length))
                                AddCandidate(plan, path, token);
                        }
                        catch (Exception ex) { if (!FileAccessError(ex)) throw; plan.Skipped++; }
                    }
                }
                catch (Exception ex)
                {
                    if (!FileAccessError(ex) || directory == plan.Folder) throw;
                    plan.Skipped++;
                }
            }
        }

        private static CandidateFile AddCandidate(RelinkPlan plan, string path, CancellationToken token = default(CancellationToken))
        {
            using (var file = new OriginalFile(path))
            {
                var candidate = new CandidateFile { Index=plan.Files.Count, Path=path, Identity=file.Identity, Bytes=file.Bytes, Hash=file.Digest(token) };
                plan.Files.Add(candidate);
                return candidate;
            }
        }

        private static string FormatFamily(string path)
        {
            string ext = Path.GetExtension(path).ToUpperInvariant();
            switch (ext)
            {
                case ".JPE": case ".JPEG": return ".JPG";
                case ".TIF": return ".TIFF";
                case ".HEIF": return ".HEIC";
                default: return ext;
            }
        }

        private static string MetadataText(Dictionary<string,object> info, string key)
        {
            object value;
            return info != null && info.TryGetValue(key, out value) ? Convert.ToString(value) : "";
        }

        private static bool CanManuallyCompare(OriginalRecord record, CandidateFile candidate)
        {
            if (!candidate.Inspected)
            {
                candidate.Inspected = true;
                try
                {
                    using (var file=new OriginalFile(candidate.Path))
                    {
                        if (file.Identity!=candidate.Identity || file.Digest()!=candidate.Hash) return false;
                        candidate.Info=Imaging.Inspect(WorkspacePaths.ForIO(candidate.Path));
                    }
                }
                catch (Exception ex)
                {
                    if (!FileAccessError(ex) && !(ex is NotSupportedException) && !(ex is InvalidDataException) &&
                        !(ex is ArgumentException) && !(ex is InvalidOperationException) && !(ex is OverflowException) &&
                        !(ex is FormatException) && !(ex is System.Runtime.InteropServices.COMException)) throw;
                }
            }
            if (candidate.Info == null || (record.KnownBytes.HasValue && record.KnownBytes != candidate.Bytes)) return false;
            foreach (var field in new[] {"width","height","dateTaken","cameraModel"})
            {
                string before = MetadataText(record.Info, field), after = MetadataText(candidate.Info, field);
                if (field == "width" || field == "height") { if (before.Length==0 || before!=after) return false; }
                else if (before.Length>0 && after.Length>0 && !String.Equals(before,after,StringComparison.OrdinalIgnoreCase)) return false;
            }
            return true;
        }

        private static CandidateOption DescribeOption(RelinkPlan plan, OriginalRecord record, CandidateFile file)
        {
            var option = new CandidateOption { File=file };
            if (record.Hash != null && record.Hash != file.Hash) { option.Match="different"; option.Detail="内容指纹不同"; }
            else if (FormatFamily(record.Path) != FormatFamily(file.Path)) { option.Match="different"; option.Detail="文件格式不同"; }
            else if (plan.Paths[file.Path].Any(id=>id!=record.Id) || (file.Identity!=null && plan.Identities[file.Identity].Any(id=>id!=record.Id)))
            { option.Match="occupied"; option.Detail="已属于另一条图库记录"; }
            else if (record.Hash != null) { option.Match="verified"; option.Detail="SHA-256 一致"; }
            else if (CanManuallyCompare(record,file)) { option.Match="manual"; option.Detail="尺寸等信息相符，旧库没有指纹"; }
            else { option.Match="different"; option.Detail="文件无法解码，或容量、尺寸、拍摄信息不同"; }
            return option;
        }

        private static void PrepareChoices(RelinkPlan plan, CancellationToken token = default(CancellationToken))
        {
            var byHash = plan.Files.ToLookup(f=>f.Hash);
            var byName = plan.Files.ToLookup(f=>Path.GetFileName(f.Path), StringComparer.OrdinalIgnoreCase);
            var bySize = plan.Files.ToLookup(f=>f.Bytes);
            var originalsByHash = plan.Rows.Where(r=>r.Original.Hash!=null && !r.Blocked).ToLookup(r=>r.Original.Hash);
            foreach (var row in plan.Rows)
            {
                token.ThrowIfCancellationRequested();
                row.Options.Clear(); row.Viewed.Clear(); row.Suggested=null; row.More=false;
                if (row.Blocked) { row.Status="blocked"; continue; }
                var record = row.Original;
                IEnumerable<CandidateFile> matches = record.Hash == null ? Enumerable.Empty<CandidateFile>() : byHash[record.Hash];
                matches = matches.Concat(byName[Path.GetFileName(record.Path)]);
                if (record.Hash==null && record.KnownBytes.HasValue) matches = matches.Concat(bySize[record.KnownBytes.Value]);
                if (row.Picked.HasValue) matches = new[] {plan.Files[row.Picked.Value]}.Concat(matches);
                // A directory of thousands of identical copies must not multiply the bridge payload.
                // A native picker can bring any omitted copy to the front for this row.
                foreach (var file in matches.Distinct().Take(25))
                {
                    token.ThrowIfCancellationRequested();
                    if (row.Options.Count==24) { row.More=true; break; }
                    row.Options.Add(DescribeOption(plan,record,file));
                }
                var verified = row.Options.Where(o=>o.Match=="verified").ToList();
                if (record.Hash==null) row.Status="unverified";
                else if (verified.Count>0)
                {
                    bool unique = byHash[record.Hash].Count()==1 && originalsByHash[record.Hash].Count()==1 && plan.Skipped==0;
                    row.Status = unique ? "exact" : "review";
                    if (unique) row.Suggested = verified[0].File.Index;
                }
                else row.Status = row.Options.Count==0 ? "missing" : row.Options.Any(o=>o.Match=="occupied") ? "occupied" : "different";
            }
        }

        private object PublishRelink(RelinkPlan plan)
        {
            plan.Token = NewId(); plan.Created = DateTime.UtcNow; relinkPlan = plan;
            return new { token=plan.Token, folder=plan.Folder, scanned=plan.Scanned, connected=plan.Connected, skipped=plan.Skipped,
                rows=plan.Rows.Select((r,i)=>new {
                    row=i, path=r.Original.Path, bytes=r.Original.KnownBytes, hash=r.Original.Hash,
                    width=MetadataText(r.Original.Info,"width"), height=MetadataText(r.Original.Info,"height"),
                    camera=MetadataText(r.Original.Info,"cameraModel"), date=MetadataText(r.Original.Info,"dateTaken"),
                    status=r.Status, suggested=r.Suggested, more=r.More, candidates=r.Options.Select(o=>new {
                        index=o.File.Index, path=o.File.Path, bytes=o.File.Bytes, hash=o.File.Hash, match=o.Match, detail=o.Detail,
                        width=MetadataText(o.File.Info,"width"), height=MetadataText(o.File.Info,"height"),
                        camera=MetadataText(o.File.Info,"cameraModel"), date=MetadataText(o.File.Info,"dateTaken")
                    }).ToArray()
                }).ToArray() };
        }

        private RelinkPlan PendingRelink(string token)
        {
            if (relinkPlan == null || relinkPlan.Token != token || DateTime.UtcNow-relinkPlan.Created > TimeSpan.FromHours(1))
                throw new InvalidOperationException("这次重新定位预览已过期，请重新扫描。");
            return relinkPlan;
        }
        private static MissingOriginal PlanRow(RelinkPlan plan, int row)
        {
            if (row<0 || row>=plan.Rows.Count || plan.Rows[row].Blocked) throw new ArgumentException("照片不在可重新定位的预览中。");
            return plan.Rows[row];
        }
        private static CandidateOption PlanOption(MissingOriginal row, int candidate)
        {
            var option = row.Options.FirstOrDefault(o=>o.File.Index==candidate);
            if (option==null) throw new ArgumentException("文件不在这张照片的候选清单中。");
            return option;
        }

        internal object PickRelinkFile(string token, int rowIndex, string path)
        {
            lock (relinkGate)
            {
                var plan = PendingRelink(token);
                var row = PlanRow(plan,rowIndex);
                path = WorkspacePaths.Full(path);
                var file = plan.Files.FirstOrDefault(f=>String.Equals(f.Path,path,StringComparison.OrdinalIgnoreCase)) ?? AddCandidate(plan,path);
                row.Picked=file.Index;
                PrepareChoices(plan);
                return PublishRelink(plan);
            }
        }

        internal T ViewRelinkFile<T>(string token, int row, int candidate, Func<string,T> render)
        {
            CandidateFile saved;
            lock (relinkGate) saved=PlanOption(PlanRow(PendingRelink(token),row),candidate).File;
            using (var file = new OriginalFile(saved.Path))
            {
                if (file.Identity!=saved.Identity || file.Digest()!=saved.Hash) throw new IOException("候选文件在扫描后发生变化，请重新扫描。");
                T image=render(WorkspacePaths.ForIO(saved.Path));
                lock (relinkGate) PlanRow(PendingRelink(token),row).Viewed.Add(candidate);
                return image;
            }
        }

        internal static RelinkChoice[] ReadRelinkChoices(Dictionary<string,object> args)
        {
            return Sequence(Arg(args,"choices"),10000).Select(item=>
            {
                var data=Map(item);
                Fields(data,"row","candidate","manual");
                return new RelinkChoice { Row=checked((int)IntegerArg(data,"row")), Candidate=checked((int)IntegerArg(data,"candidate")),
                    Manual=data.ContainsKey("manual") && BoolArg(data,"manual") };
            }).ToArray();
        }

        private int? CompletedRelink(string token, string fingerprint)
        {
            var rows=db.Rows("SELECT choices,changed FROM relink_receipts WHERE token=?",token);
            if (rows.Count==0) return null;
            if ((string)rows[0][0]!=fingerprint) throw new InvalidOperationException("这次预览已用另一组选择提交，请重新扫描。");
            return Convert.ToInt32(rows[0][1]);
        }

        internal int ApplyRelink(string token, RelinkChoice[] choices, CancellationToken cancel)
        {
            if (choices.Length==0 || choices.Select(c=>c.Row).Distinct().Count()!=choices.Length)
                throw new ArgumentException("请选择照片，每张仅能选择一份原片。");
            string fingerprint;
            using (var sha=SHA256.Create())
                fingerprint=Convert.ToBase64String(sha.ComputeHash(Encoding.ASCII.GetBytes(String.Join(";",choices.OrderBy(c=>c.Row)
                    .Select(c=>c.Row.ToString("x")+":"+c.Candidate.ToString("x")+(c.Manual?"m":"v"))))));
            lock (relinkGate)
            {
                lock (gate) { var done=CompletedRelink(token,fingerprint); if (done.HasValue) return done.Value; }
                var plan=PendingRelink(token);
                var handles=new List<OriginalFile>();
                var files=new List<CandidateFile>();
                var identities=new HashSet<string>();
                var paths=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    foreach (var choice in choices)
                    {
                        cancel.ThrowIfCancellationRequested();
                        var row=PlanRow(plan,choice.Row);
                        var option=PlanOption(row,choice.Candidate);
                        if (option.Match!="verified" && !(option.Match=="manual" && choice.Manual && row.Viewed.Contains(choice.Candidate)))
                            throw new InvalidOperationException("需逐张核对无指纹的候选文件。指纹或元数据不符的文件无法绑定。");
                        var saved=option.File;
                        var file=new OriginalFile(saved.Path);
                        handles.Add(file); files.Add(saved);
                        if (file.Digest(cancel)!=saved.Hash || file.Identity!=saved.Identity || file.Bytes!=saved.Bytes)
                            throw new IOException("候选文件在扫描后发生变化，请重新扫描。");
                        if (!paths.Add(saved.Path) || (file.Identity!=null && !identities.Add(file.Identity)))
                            throw new InvalidOperationException("多条图库记录选择了同一份文件，请分别指定候选或跳过。");
                    }
                    lock (gate) return db.Transaction(true,delegate
                    {
                        var done=CompletedRelink(token,fingerprint); if (done.HasValue) return done.Value;
                        for (int i=0;i<choices.Length;i++)
                        {
                            cancel.ThrowIfCancellationRequested();
                            var row=plan.Rows[choices[i].Row];
                            var original=row.Original;
                            var live=SourceRecord(original.Id);
                            var file=files[i];
                            var state=OriginalAvailability(live,cancel);
                            if (live.Path!=original.Path || live.Identity!=original.Identity || live.Hash!=original.Hash ||
                                live.Revision!=original.Revision || state!=row.Observed)
                                throw new InvalidOperationException("原片路径、修订或可用状态已改变，请重新扫描。");
                            string key=file.Path.ToUpperInvariant();
                            if (db.Scalar("SELECT 1 FROM photos WHERE id<>? AND (path_key=? OR identity=?) LIMIT 1",live.Id,key,file.Identity)!=null)
                                throw new InvalidOperationException("候选文件已属于另一条图库记录，请重新扫描。");
                            string metadata=original.Hash==null ? json.Serialize(file.Info) : live.Metadata;
                            db.Run("UPDATE photos SET path=?,path_key=?,identity=?,metadata=? WHERE id=?",file.Path,key,file.Identity,metadata,live.Id);
                            SaveSource(live.Id,handles[i],file.Hash);
                        }
                        db.Run("INSERT INTO relink_receipts(token,choices,changed) VALUES(?,?,?)",token,fingerprint,choices.Length);
                        return choices.Length;
                    });
                }
                finally { foreach (var handle in handles) handle.Dispose(); }
            }
        }
    }
}
