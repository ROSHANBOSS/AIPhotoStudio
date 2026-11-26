using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Web.Script.Serialization;
using Microsoft.Win32.SafeHandles;

namespace AIPhotoStudio
{
    public sealed partial class StudioWorkspace
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct BasicFileInfo { internal long Created, Accessed, Written, Changed; internal uint Attributes; }

        [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ReadBasicInfo(SafeFileHandle handle, int kind, out BasicFileInfo info, uint size);

        // Deny writers, renames and deletes for the lifetime of this handle. The hash,
        // Windows identity and length all describe the same bytes.
        private sealed class OriginalFile : IDisposable
        {
            private readonly FileStream stream;
            private string digest;
            internal readonly string Identity, Stamp;
            internal readonly long Bytes;

            internal OriginalFile(string path)
            {
                stream = WorkspacePaths.OpenRead(path, FileShare.Read);
                try
                {
                    Bytes = stream.Length;
                    FileInformation info;
                    if (GetFileInformationByHandle(stream.SafeFileHandle, out info) && (info.indexHigh != 0 || info.indexLow != 0))
                        Identity = info.volume.ToString("x8") + "-" + info.indexHigh.ToString("x8") + info.indexLow.ToString("x8");
                    BasicFileInfo times;
                    if (Identity != null && ReadBasicInfo(stream.SafeFileHandle, 0, out times, (uint)Marshal.SizeOf(typeof(BasicFileInfo))) && times.Changed > 0)
                        Stamp = Identity + "/" + times.Created.ToString("x") + "/" + times.Written.ToString("x") + "/" + times.Changed.ToString("x");
                }
                catch { stream.Dispose(); throw; }
            }

            internal string Digest(CancellationToken token = default(CancellationToken))
            {
                if (digest != null) return digest;
                using (var hash = SHA256.Create())
                {
                    if (!token.CanBeCanceled) return digest = Convert.ToBase64String(hash.ComputeHash(stream));
                    byte[] block = new byte[65536];
                    int count;
                    while ((count=stream.Read(block,0,block.Length))>0)
                    {
                        token.ThrowIfCancellationRequested();
                        hash.TransformBlock(block,0,count,null,0);
                    }
                    hash.TransformFinalBlock(block,0,0);
                    digest=Convert.ToBase64String(hash.Hash);
                }
                return digest;
            }
            public void Dispose() { stream.Dispose(); }
        }

        private sealed class OriginalRecord
        {
            internal string Id, Path, Identity, Hash, Stamp, Metadata;
            internal long Revision;
            internal long? Bytes;
            private Dictionary<string,object> info;
            internal Dictionary<string,object> Info
            {
                get { return info ?? (info = new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(Metadata)); }
            }
            internal long? KnownBytes
            {
                get
                {
                    if (Bytes.HasValue) return Bytes;
                    object raw; long size;
                    return Info.TryGetValue("fileBytes", out raw) && Int64.TryParse(Convert.ToString(raw), out size) && size >= 0 ? (long?)size : null;
                }
            }
        }

        // Older photo-bound edits already saved SHA-256 in face-source; those photos
        // can still be identified even if the old drive was unplugged before migration.
        private const string SourceColumns = "SELECT p.id,p.path,p.identity,p.revision,p.metadata,COALESCE(f.sha256,b.value),f.bytes,f.stamp,p.rowid FROM photos p " +
            "LEFT JOIN photo_sources f ON f.photo_id=p.id LEFT JOIN settings b ON b.name='face-source/'||p.id ";
        private static OriginalRecord SourceFrom(object[] r)
        {
            return new OriginalRecord { Id=(string)r[0], Path=(string)r[1], Identity=(string)r[2], Revision=(long)r[3],
                Metadata=(string)r[4], Hash=(string)r[5], Bytes=(long?)r[6], Stamp=(string)r[7] };
        }
        private OriginalRecord SourceRecord(string id)
        {
            var rows = db.Rows(SourceColumns + "WHERE p.id=? AND " + VisiblePhotos, id);
            if (rows.Count == 0) throw new KeyNotFoundException("图库中找不到这张照片。");
            return SourceFrom(rows[0]);
        }

        private static bool MatchesOriginal(OriginalRecord record, OriginalFile file, CancellationToken token = default(CancellationToken))
        {
            if (record.Identity != null && record.Identity != file.Identity) return false;
            if (record.KnownBytes.HasValue && record.KnownBytes.Value != file.Bytes) return false;
            return record.Hash == null || (file.Stamp != null && file.Stamp == record.Stamp) || file.Digest(token) == record.Hash;
        }

        private string ExistingImport(OriginalFile file, string key, string digest)
        {
            if (file.Identity != null)
            {
                var rows = db.Rows(SourceColumns + "WHERE p.identity=?", file.Identity);
                if (rows.Count != 0)
                {
                    var record = SourceFrom(rows[0]);
                    if ((record.Hash == null || record.Hash == digest) && MatchesOriginal(record, file)) return record.Id;
                    // An in-place replacement or a recycled file index gets fresh edits.
                    // The old entry retains its digest and can still be relinked.
                    db.Run("UPDATE photos SET identity=NULL WHERE id=?", record.Id);
                }
            }
            return db.Rows(SourceColumns + "WHERE p.path_key=? AND p.identity IS NULL ORDER BY p.rowid", key)
                .Select(SourceFrom).Where(r => r.Hash == digest).Select(r => r.Id).FirstOrDefault();
        }

        private void SaveSource(string id, OriginalFile file, string hash)
        {
            db.Run("INSERT OR REPLACE INTO photo_sources(photo_id,sha256,bytes,stamp) VALUES(?,?,?,?)", id, hash, file.Bytes, file.Stamp);
        }

        private static bool FileAccessError(Exception ex)
        {
            return ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException;
        }

        internal object OriginalCoverage()
        {
            lock (gate)
            {
                var row=db.Rows("SELECT COUNT(*),COUNT(COALESCE(f.sha256,b.value)) FROM photos p " +
                    "LEFT JOIN photo_sources f ON f.photo_id=p.id LEFT JOIN settings b ON b.name='face-source/'||p.id WHERE " + VisiblePhotos)[0];
                return new { total=row[0], known=row[1] };
            }
        }

        // Index the old library off the UI thread. Each page releases the workspace lock
        // before file I/O. Unavailable files remain eligible on later runs.
        internal int BackfillOriginals(CancellationToken token)
        {
            long cursor = 0;
            int added = 0;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                List<object[]> rows;
                lock (gate) rows = db.Rows(SourceColumns + "WHERE p.rowid>? AND f.photo_id IS NULL AND " +
                    "(p.identity IS NOT NULL OR b.value IS NOT NULL) AND " + VisiblePhotos + " ORDER BY p.rowid LIMIT 24", cursor);
                if (rows.Count == 0) return added;
                foreach (var row in rows)
                {
                    cursor = (long)row[8];
                    token.ThrowIfCancellationRequested();
                    var record = SourceFrom(row);
                    try
                    {
                        using (var file = new OriginalFile(record.Path))
                        {
                            if (!MatchesOriginal(record, file, token)) continue;
                            string hash = file.Digest(token);
                            lock (gate)
                            {
                                token.ThrowIfCancellationRequested();
                                db.Run("INSERT OR IGNORE INTO photo_sources(photo_id,sha256,bytes,stamp) " +
                                    "SELECT p.id,?,?,? FROM photos p WHERE p.id=? AND p.path=? AND p.identity IS ? AND " + VisiblePhotos,
                                    hash, file.Bytes, file.Stamp, record.Id, record.Path, record.Identity);
                                added += Convert.ToInt32(db.Scalar("SELECT changes()"));
                            }
                        }
                    }
                    catch (Exception ex) { if (!FileAccessError(ex)) throw; }
                }
            }
        }
    }
}
