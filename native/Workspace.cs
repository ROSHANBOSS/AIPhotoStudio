using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;
using Microsoft.Win32.SafeHandles;

namespace AIPhotoStudio
{
    public sealed partial class StudioWorkspace : IDisposable
    {
        private const int ApplicationId = 0x41505354;
        internal const int Schema = 16;
        private readonly object gate = new object();
        private readonly WorkspaceDb db;
        private readonly string databasePath;
        private readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024, RecursionLimit = 32 };
        private readonly Dictionary<string, ControlSpec> controls;
        private readonly string catalogSession=NewId();
        private string issuedCatalogToken;
        private long snapshotOrder;
        internal sealed class WorkspaceSnapshot : Dictionary<string,object>
        {
            internal string Session;
            internal long Order;
        }

        public static string ChannelDirectory(string root, string channel)
        {
            if (channel != "development" && channel != "stable")
                throw new ArgumentException("运行通道必须为 development 或 stable。");
            return Path.Combine(WorkspacePaths.Full(root), channel);
        }

        public StudioWorkspace(string dataDirectory)
        {
            if (String.IsNullOrWhiteSpace(dataDirectory)) throw new ArgumentException("请指定本地数据目录。");
            controls = Imaging.Controls.ToDictionary(c => c.key, StringComparer.Ordinal);
            string projects = Path.Combine(WorkspacePaths.Full(dataDirectory), "Projects");
            Directory.CreateDirectory(WorkspacePaths.ForIO(projects));
            databasePath = Path.Combine(projects, "studio.sqlite3");
            db = new WorkspaceDb(databasePath, false);
            try { db.Transaction(true, Initialize); }
            catch { db.Dispose(); throw; }
        }

        private bool Initialize()
        {
            long version = (long)db.Scalar("PRAGMA user_version");
            long application = (long)db.Scalar("PRAGMA application_id");
            if (version == 0 && application == 0)
            {
                long existing = (long)db.Scalar("SELECT count(*) FROM sqlite_master WHERE name NOT LIKE 'sqlite_%'");
                if (existing != 0) throw new InvalidDataException("所选数据库已有其他表，请选择本产品的数据目录。");
                db.Run("CREATE TABLE photos (id TEXT PRIMARY KEY, path TEXT NOT NULL, path_key TEXT NOT NULL, identity TEXT UNIQUE, metadata TEXT NOT NULL, favorite INTEGER NOT NULL DEFAULT 0 CHECK(favorite IN (0,1)), revision INTEGER NOT NULL DEFAULT 0 CHECK(revision>=0), cursor INTEGER NOT NULL DEFAULT 0 CHECK(cursor>=0), engine TEXT NOT NULL)");
                db.Run("CREATE INDEX photos_path ON photos(path_key)");
                db.Run("CREATE TABLE history (photo_id TEXT NOT NULL REFERENCES photos(id), seq INTEGER NOT NULL CHECK(seq>=0), entry_id TEXT NOT NULL UNIQUE, label TEXT NOT NULL, origin TEXT NOT NULL, stamp TEXT NOT NULL, operation TEXT NOT NULL, parameters TEXT NOT NULL, locks TEXT NOT NULL, changes TEXT NOT NULL, PRIMARY KEY(photo_id,seq))");
                db.Run("CREATE TABLE settings (name TEXT PRIMARY KEY, value TEXT NOT NULL)");
                db.Run("CREATE TABLE presets (name_key TEXT PRIMARY KEY, name TEXT NOT NULL, engine TEXT NOT NULL, parameters TEXT NOT NULL)");
                db.Run("PRAGMA application_id=" + ApplicationId.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                if (application != ApplicationId || version < 1 || version > Schema)
                    throw new InvalidDataException("图库的格式或版本超出了本版本的支持范围，请使用创建它的版本打开。");
                if (version < Schema) SaveMigrationBackup(version);
            }
            if (version < 2)
            {
                // The request ledger survives a history branch being discarded.
                db.Run("CREATE TABLE requests (photo_id TEXT NOT NULL REFERENCES photos(id), request_id TEXT NOT NULL, fingerprint TEXT NOT NULL, PRIMARY KEY(photo_id,request_id))");
                db.Run("PRAGMA user_version=2");
            }
            if (version < 3)
            {
                db.Run("CREATE TABLE projects (id TEXT PRIMARY KEY, name TEXT NOT NULL, name_key TEXT NOT NULL UNIQUE)");
                db.Run("CREATE TABLE project_photos (project_id TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE, photo_id TEXT NOT NULL REFERENCES photos(id), PRIMARY KEY(project_id,photo_id))");
                db.Run("PRAGMA user_version=3");
            }
            // A v3 program does not know the photo-bound meaning of segment_ref.
            // Mark the new data protocol even though the SQL tables need no rewrite.
            if(version<4) db.Run("PRAGMA user_version=4");
            // v4 can read the shape of a stroke resource but cannot render a heal
            // stroke. Its future-version check must run before any write.
            if(version<5) db.Run("PRAGMA user_version=5");
            if (version < 6)
            {
                db.Run("CREATE TABLE IF NOT EXISTS removed_photos (photo_id TEXT PRIMARY KEY REFERENCES photos(id))");
                db.Run("PRAGMA user_version=6");
            }
            if(version<7) db.Run("PRAGMA user_version=7");
            // v7 clients cannot edit independent mask layers, face meshes or frame-specific parameters.
            if(version<8) db.Run("PRAGMA user_version=8");
            if(version<9) db.Run("PRAGMA user_version=9");
            if (version < 10)
            {
                db.Run("CREATE TABLE IF NOT EXISTS photo_sources (photo_id TEXT PRIMARY KEY REFERENCES photos(id), sha256 TEXT NOT NULL, bytes INTEGER NOT NULL, stamp TEXT)");
                db.Run("CREATE TABLE IF NOT EXISTS relink_receipts (token TEXT PRIMARY KEY, choices TEXT NOT NULL, changed INTEGER NOT NULL)");
                CreateSetReviewTable();
                db.Run("PRAGMA user_version=10");
            }
            if(version<11)
            {
                CreateSetReviewEyesTable();
                db.Run("PRAGMA user_version=11");
            }
            // Older renderers cannot preserve lens and fabric layers when the next edit is written.
            if(version<12) db.Run("PRAGMA user_version=12");
            // Dev.6 cannot replay a repair stroke with an explicit texture source.
            if(version<13) db.Run("PRAGMA user_version=13");
            // Older renderers cannot replay privacy or edge-aware masks and portrait keep marks.
            if(version<14) db.Run("PRAGMA user_version=14");
            if(version<15)
            {
                BuildEditIndex();
                db.Run("PRAGMA user_version=15");
            }
            if(version<16)
            {
                db.Run("CREATE TABLE IF NOT EXISTS photo_notes(photo_id TEXT PRIMARY KEY REFERENCES photos(id), taken TEXT NOT NULL, caption TEXT NOT NULL)");
                db.Run("PRAGMA user_version=16");
            }
            return true;
        }

        private void SaveMigrationBackup(long version)
        {
            string folder = Path.Combine(WorkspacePaths.DirectoryOf(databasePath), "Backups");
            Directory.CreateDirectory(WorkspacePaths.ForIO(folder));
            string token=NewId().Substring(0,12);
            string name = "v" + version + "-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture) + "-" + token + ".sqlite3";
            string completed = Path.Combine(folder, name);
            string staging = Path.Combine(folder,"."+token+".tmp");
            using (new FileStream(WorkspacePaths.ForIO(staging), FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            try
            {
                WorkspaceDb.Backup(databasePath, staging);
                File.Move(WorkspacePaths.ForIO(staging), WorkspacePaths.ForIO(completed));
            }
            finally { if (File.Exists(WorkspacePaths.ForIO(staging))) File.Delete(WorkspacePaths.ForIO(staging)); }
        }

        private static string NewId() { return Guid.NewGuid().ToString("N"); }
        private static string Now() { return DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture); }

        public string AddPhoto(string path, Dictionary<string, object> info)
        {
            if (info == null) throw new ArgumentNullException("info");
            string full = WorkspacePaths.Full(path);
            using (var file = new OriginalFile(full)) return RegisterPhoto(full, file, info);
        }

        internal string ImportOriginal(string path, bool select = true)
        {
            string full = WorkspacePaths.Full(path);
            using (var file = new OriginalFile(full))
                return RegisterPhoto(full, file, Imaging.Inspect(WorkspacePaths.ForIO(full)), select);
        }

        private string RegisterPhoto(string full, OriginalFile file, Dictionary<string,object> info, bool select = true)
        {
            string key = full.ToUpperInvariant();
            string digest = file.Digest();
            lock (gate)
            {
                string metadata = json.Serialize(info);
                if (metadata.Length > 256 * 1024) throw new ArgumentException("照片元数据超过可保存的大小。");
                return db.Transaction(true, delegate
                {
                    string id = ExistingImport(file, key, digest);
                    if (id == null)
                    {
                        id = NewId();
                        db.Run("INSERT INTO photos(id,path,path_key,identity,metadata,engine) VALUES(?,?,?,?,?,?)", id, full, key, file.Identity, metadata, Imaging.EngineVersion);
                        WriteFrame(id, 0, "导入原片", "import", Now(), "initial", Defaults(), new HashSet<string>(StringComparer.Ordinal), new Dictionary<string, double>());
                    }
                    else db.Run("UPDATE photos SET path=?,path_key=?,identity=?,metadata=? WHERE id=?", full, key, file.Identity, metadata, id);
                    SaveSource(id, file, digest);
                    db.Run("DELETE FROM removed_photos WHERE photo_id=?", id);
                    if (select || db.Scalar("SELECT value FROM settings WHERE name='selected'")==null) SetSelected(id);
                    return id;
                });
            }
        }

        public string PathFor(string photoId)
        {
            OriginalRecord record;
            lock (gate) record = SourceRecord(photoId);
            using (var file = new OriginalFile(record.Path))
                if (!MatchesOriginal(record, file))
                    throw new IOException("原路径的文件身份或内容已改变。请在图库中选择“找回原片”。");
            return WorkspacePaths.ForIO(record.Path);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FileInformation
        {
            internal uint attributes;
            internal System.Runtime.InteropServices.ComTypes.FILETIME created, accessed, written;
            internal uint volume, sizeHigh, sizeLow, links, indexHigh, indexLow;
        }
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation info);

        public Dictionary<string, double> ValuesFor(string photoId)
        {
            lock (gate)
                return db.Transaction(false, delegate
                {
                    State state = Current(photoId);
                    CheckEngine(state.Engine);
                    return state.Values;
                });
        }

        public string[] PlannerHistory(string photoId)
        {
            lock (gate) return db.Transaction(false, delegate
            {
                State state = Current(photoId);
                CheckEngine(state.Engine);
                List<string> entries = new List<string>();
                foreach (object[] row in db.Rows("SELECT h.changes,b.parameters FROM history h JOIN history b ON b.photo_id=h.photo_id AND b.seq=h.seq-1 WHERE h.photo_id=? AND h.seq<=? ORDER BY h.seq DESC LIMIT 12", photoId, state.Cursor))
                {
                    var changes = json.Deserialize<Dictionary<string, double>>((string)row[0]);
                    var before = StoredValues((string)row[1]);
                    if (changes.Count > 0)
                        entries.Add(String.Join(";", changes.Select(p => p.Key + "=" + before[p.Key].ToString("R", CultureInfo.InvariantCulture) + "->" + p.Value.ToString("R", CultureInfo.InvariantCulture))));
                }
                entries.Reverse();
                return entries.ToArray();
            });
        }

        public void Select(string photoId)
        {
            lock (gate) db.Transaction(true, delegate { SetSelected(photoId); return true; });
        }

        private void SetSelected(string photoId)
        {
            RequirePhoto(photoId);
            db.Run("INSERT OR REPLACE INTO settings(name,value) VALUES('selected',?)", photoId);
            adjustmentOffers.RemoveAll(p=>p.Photo!=photoId);
        }

        internal bool CpuOnly
        {
            get { lock(gate) return Object.Equals(db.Scalar("SELECT value FROM settings WHERE name='render-mode'"),"cpu"); }
            set { lock(gate) db.Run("INSERT OR REPLACE INTO settings(name,value) VALUES('render-mode',?)",value?"cpu":"auto"); }
        }

        private void RequirePhoto(string id)
        {
            if (id == null || db.Scalar("SELECT 1 FROM photos WHERE id=? AND id NOT IN (SELECT photo_id FROM removed_photos)", id) == null)
                throw new KeyNotFoundException("图库中找不到这张照片。");
        }

        private Dictionary<string, double> Defaults()
        {
            return controls.ToDictionary(c => c.Key, c => c.Value.initial, StringComparer.Ordinal);
        }

        private static bool OptionalNeutral(ControlSpec control)
        {
            if(PersonalRetouch.IsNewControl(control.key))return true;
            if(control.key==CameraRawModes.FrameKey || FrameGeometry.IsKey(control.key))return true;
            return control.group==PortraitControls.ResourceGroup || (control.initial == 0 && (control.group == "LUT 资源" || control.group == "3D LUT" ||
                control.group == "人脸区域" || control.group == "人像")) ||
                control.key.StartsWith("mask_ref",StringComparison.Ordinal) || control.key.StartsWith("local_",StringComparison.Ordinal) ||
                control.key.StartsWith("segment_",StringComparison.Ordinal) || control.key.StartsWith("body_",StringComparison.Ordinal);
        }

        private Dictionary<string,double> StoredValues(string serialized)
        {
            var values=json.Deserialize<Dictionary<string,double>>(serialized);
            foreach(ControlSpec control in controls.Values)
                if(OptionalNeutral(control) && !values.ContainsKey(control.key)) values.Add(control.key,control.initial);
            return values;
        }

        private static void CheckEngine(string version)
        {
            if (version != Imaging.EngineVersion)
                throw new InvalidOperationException("此配方使用图像引擎 " + version + "。请使用兼容版本编辑或导出。");
        }

        private sealed class State
        {
            internal string Id, Engine;
            internal long Revision;
            internal int Cursor;
            internal Dictionary<string, double> Values;
            internal HashSet<string> Locks;
        }

        private State Current(string id)
        {
            List<object[]> rows = db.Rows("SELECT p.revision,p.cursor,p.engine,h.parameters,h.locks FROM photos p JOIN history h ON h.photo_id=p.id AND h.seq=p.cursor WHERE p.id=? AND p.id NOT IN (SELECT photo_id FROM removed_photos)", id);
            if (rows.Count != 1) throw new KeyNotFoundException("找不到照片的当前编辑版本。");
            object[] row = rows[0];
            return new State
            {
                Id = id, Revision = (long)row[0], Cursor = Convert.ToInt32(row[1]), Engine = (string)row[2],
                Values = StoredValues((string)row[3]),
                Locks = new HashSet<string>(json.Deserialize<string[]>((string)row[4]), StringComparer.Ordinal)
            };
        }

        public object Snapshot()
        {
            lock (gate) return db.Transaction(false, ReadSnapshot);
        }

        private const string CatalogColumns="SELECT p.id,p.path,p.metadata,p.favorite,p.revision,h.parameters,EXISTS(SELECT 1 FROM edited_frames e WHERE e.photo_id=p.id AND e.seq=p.cursor),n.taken,n.caption FROM photos p LEFT JOIN photo_notes n ON n.photo_id=p.id LEFT JOIN history h ON p.path LIKE '%.cr3' AND h.photo_id=p.id AND h.seq=p.cursor ";
        private const string VisiblePhotos="p.id NOT IN (SELECT photo_id FROM removed_photos)";

        private Dictionary<string,object> CatalogPhoto(object[] row)
        {
            var photo=json.Deserialize<Dictionary<string,object>>((string)row[2]);
            string path=(string)row[1];
            photo["id"]=row[0];photo["name"]=path.Substring(path.LastIndexOf('\\')+1);
            photo["folder"]=WorkspacePaths.DirectoryOf(path);photo["path"]=path;
            photo["favorite"]=(long)row[3]!=0;photo["revision"]=row[4];
            photo["hasEdits"]=(long)row[6]!=0;
            photo["libraryDate"]=row[7]??"";photo["caption"]=row[8]??"";
            if(row[5]!=null&&photo.ContainsKey("frameCount")&&Convert.ToInt32(photo["frameCount"])>1)
            {
                photo["selectedRawFrame"]=CameraRawModes.Selected(StoredValues((string)row[5]));
                if(photo.ContainsKey("referenceSampleId")&&Object.Equals(photo["referenceSampleId"],4611))
                    photo["compressionMode"]=CameraRawModes.Find("CR3").Description;
            }
            return photo;
        }

        // data_version observes commits made by other connections. total_changes() observes
        // our own writes, including membership, metadata, project and preset changes.
        // Call only inside the transaction that supplies the snapshot or validates its base.
        private string CatalogToken()
        {
            object[] version=db.Rows("SELECT data_version,total_changes() FROM pragma_data_version")[0];
            return catalogSession+"/"+Convert.ToString(version[0],CultureInfo.InvariantCulture)+"/"+Convert.ToString(version[1],CultureInfo.InvariantCulture);
        }
        private WorkspaceSnapshot NewSnapshot(){return new WorkspaceSnapshot{Session=catalogSession,Order=++snapshotOrder};}

        private object ReadCommitSnapshot(string id,string catalogBase)
        {
            var row=db.Rows(CatalogColumns+"WHERE p.id=? AND "+VisiblePhotos,id);
            if(row.Count!=1)return ReadSnapshot();
            var result=NewSnapshot();
            result["catalogBase"]=catalogBase;
            result["catalogPhoto"]=CatalogPhoto(row[0]);
            result["catalogToken"]=issuedCatalogToken=CatalogToken();
            result["selectedId"]=id;
            result["document"]=Document(Current(id));
            return result;
        }

        internal Dictionary<string,object> SelectedDocument(string id,long? revision)
        {
            lock(gate)return db.Transaction(false,delegate{
                if(!Equals(id,db.Scalar("SELECT value FROM settings WHERE name='selected'")))throw new InvalidOperationException("所选照片已改变。");
                var state=Current(id);
                if(state.Revision!=revision)throw new InvalidOperationException("照片已更新，请基于当前修订重新操作。");
                return (Dictionary<string,object>)Document(state);
            });
        }

        private object ReadSnapshot()
        {
            string selected = (string)db.Scalar("SELECT value FROM settings WHERE name='selected'");
            var catalog=db.Rows(CatalogColumns+"WHERE "+VisiblePhotos+" ORDER BY p.rowid").Select(CatalogPhoto).ToList();
            List<object> presets = new List<object>();
            foreach (object[] p in db.Rows("SELECT name,engine,parameters FROM presets ORDER BY name_key"))
                presets.Add(new Dictionary<string, object> { { "name", p[0] }, { "engineVersion", p[1] }, { "values", StoredValues((string)p[2]) } });
            var result=NewSnapshot();
            result["catalog"]=catalog;result["selectedId"]=selected??"";result["userPresets"]=presets;result["projects"]=ReadProjects();
            result["document"]=selected==null?null:Document(Current(selected));
            issuedCatalogToken=catalog.Count>=100?CatalogToken():null;
            if(issuedCatalogToken!=null)result["catalogToken"]=issuedCatalogToken;
            return result;
        }

        private List<object> ReadProjects()
        {
            List<object> result = new List<object>();
            Dictionary<string, List<string>> members = new Dictionary<string, List<string>>();
            foreach (object[] row in db.Rows("SELECT p.id,p.name,m.photo_id FROM projects p LEFT JOIN project_photos m ON m.project_id=p.id AND m.photo_id NOT IN (SELECT photo_id FROM removed_photos) ORDER BY p.rowid,m.rowid"))
            {
                string id = (string)row[0];
                List<string> photos;
                if (!members.TryGetValue(id, out photos))
                {
                    photos = new List<string>();
                    members.Add(id, photos);
                    result.Add(new Dictionary<string, object> { { "id", id }, { "name", row[1] }, { "photoIds", photos } });
                }
                if (row[2] != null) photos.Add((string)row[2]);
            }
            return result;
        }

        private void ProjectCommand(string method, Dictionary<string, object> args)
        {
            if (method == "createProject") Fields(args, "name");
            else if (method == "renameProject") Fields(args, "projectId", "name");
            else if (method == "setProjectPhoto") Fields(args, "projectId", "id", "member");
            else Fields(args, "projectId");
            string id = method == "createProject" ? NewId() : TextArg(args, "projectId", 100);
            if (method != "createProject" && db.Scalar("SELECT 1 FROM projects WHERE id=?", id) == null)
                throw new ArgumentException("项目不存在。");
            if (method == "deleteProject") { db.Run("DELETE FROM projects WHERE id=?", id); return; }
            if (method == "setProjectPhoto")
            {
                string photo = TextArg(args, "id", 100);
                RequirePhoto(photo);
                if (BoolArg(args, "member")) db.Run("INSERT OR IGNORE INTO project_photos VALUES(?,?)", id, photo);
                else db.Run("DELETE FROM project_photos WHERE project_id=? AND photo_id=?", id, photo);
                return;
            }
            string name = TextArg(args, "name", 100).Trim(), key = name.ToUpperInvariant();
            if (db.Scalar("SELECT 1 FROM projects WHERE name_key=? AND id<>?", key, id) != null)
                throw new InvalidOperationException("已有同名项目。");
            if (method == "createProject") db.Run("INSERT INTO projects(id,name,name_key) VALUES(?,?,?)", id, name, key);
            else db.Run("UPDATE projects SET name=?,name_key=? WHERE id=?", name, key, id);
        }

        private object Document(State s)
        {
            List<object> history = new List<object>();
            var undoCounts=new Dictionary<string,int>(StringComparer.Ordinal);
            var redoCounts=new Dictionary<string,int>(StringComparer.Ordinal);
            foreach (object[] h in db.Rows("SELECT seq,entry_id,label,origin,stamp,changes,operation FROM history WHERE photo_id=? ORDER BY seq", s.Id))
            {
                string[] changed=json.Deserialize<Dictionary<string,double>>((string)h[5]).Keys.ToArray();
                string operation=(string)h[6];
                if((long)h[0]<=s.Cursor)
                    foreach(string key in changed)
                    {
                        ControlSpec spec;
                        if(s.Locks.Contains(key) || !controls.TryGetValue(key,out spec) || spec.group=="人脸区域" || spec.group=="LUT 资源" || spec.group=="局部资源" || spec.group=="分区资源" || spec.group=="人体资源") continue;
                        int undo,redo; undoCounts.TryGetValue(key,out undo); redoCounts.TryGetValue(key,out redo);
                        if(operation=="undoParam") { undo--; redo++; }
                        else if(operation=="redoParam") { undo++; redo--; }
                        else { undo++; redo=0; }
                        undoCounts[key]=undo; redoCounts[key]=redo;
                    }
                history.Add(new Dictionary<string, object>
                {
                    { "index", h[0] }, { "id", h[1] }, { "label", h[2] }, { "source", h[3] }, { "when", h[4] },
                    { "changed", changed }, { "operation", operation }
                });
            }
            return new Dictionary<string, object>
            {
                { "photoId", s.Id }, { "revision", s.Revision }, { "parameters", s.Values },
                { "locks", OrderedLocks(s.Locks) }, { "engineVersion", s.Engine }, { "compatible", s.Engine == Imaging.EngineVersion },
                { "history", history }, { "cursor", s.Cursor }, { "canUndo", s.Cursor > 0 }, { "canRedo", s.Cursor + 1 < history.Count },
                { "undoableParams", undoCounts.Where(p=>p.Value>0).Select(p=>p.Key).ToArray() },
                { "redoableParams", redoCounts.Where(p=>p.Value>0).Select(p=>p.Key).ToArray() }
            };
        }

        private static string[] OrderedLocks(HashSet<string> locks)
        {
            return locks.OrderBy(k => k, StringComparer.Ordinal).ToArray();
        }

        private void WriteFrame(string id, int seq, string label, string source, string when, string operation, Dictionary<string, double> values, HashSet<string> locks, Dictionary<string, double> changes)
        {
            db.Run("INSERT INTO history(photo_id,seq,entry_id,label,origin,stamp,operation,parameters,locks,changes) VALUES(?,?,?,?,?,?,?,?,?,?)",
                id, seq, NewId(), label, source, when, operation, json.Serialize(values), json.Serialize(OrderedLocks(locks)), json.Serialize(changes));
            if(seq>0) IndexEditFrame(id,seq,values);
        }

        public void Dispose() { lock (gate) db.Dispose(); }
    }
}
