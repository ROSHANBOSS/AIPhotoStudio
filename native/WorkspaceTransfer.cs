using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace AIPhotoStudio
{
    public sealed partial class StudioWorkspace
    {
        private const string LibraryDbEntry="Projects/studio.sqlite3";
        private static readonly string[] AssetFolders={"Projects/Masks","Projects/Segments","Projects/Bodies","Projects/Faces","Presets/LUT"};
        private static readonly string[] AssetExt={".json",".alpha",".body",".mesh",".cube"};
        private const long ArchiveLimit=16L*1024*1024*1024;
        private const string LastRestoreKey="library/last-restored";
        private static string HexDigest(byte[] bytes){return BitConverter.ToString(bytes).Replace("-","").ToLowerInvariant();}
        private static string CopyDigest(Stream input,Stream output,long limit,CancellationToken token)
        {
            using(var sha=SHA256.Create())
            {
                byte[] block=new byte[65536];long count=0;int n;
                while((n=input.Read(block,0,block.Length))!=0)
                {
                    token.ThrowIfCancellationRequested();count+=n;
                    if(count>limit)throw new InvalidDataException("图库包内容超出声明的大小。");
                    output.Write(block,0,n);sha.TransformBlock(block,0,n,block,0);
                }
                sha.TransformFinalBlock(block,0,0);return HexDigest(sha.Hash);
            }
        }
        private static int ArchiveAsset(string key)
        {
            for(int i=0;i<AssetFolders.Length;i++)
                if(key.StartsWith(AssetFolders[i]+"/",StringComparison.Ordinal)&&key.EndsWith(AssetExt[i],StringComparison.Ordinal)
                    &&Regex.IsMatch(key.Substring(AssetFolders[i].Length+1),"^[a-f0-9]{64}"+Regex.Escape(AssetExt[i])+"\\z"))return i;
            return -1;
        }
        private static void PlainEntry(string path)
        {
            if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new IOException("图库路径包含指向其他位置的链接。");
        }
        private static string[] SchemaObjects(WorkspaceDb database)
        {
            return database.Rows("SELECT type,name,sql FROM sqlite_master WHERE name NOT GLOB 'sqlite_*' ORDER BY type,name")
                .Select(row=>String.Join("|",row)).ToArray();
        }
        private static void CheckResources(WorkspaceDb database,ISet<string> files,CancellationToken token)
        {
            var reader=new JavaScriptSerializer{MaxJsonLength=16*1024*1024};
            foreach(string table in new[]{"history","presets"})
            {
                long at=0;
                while(true)
                {
                    var page=database.Rows("SELECT rowid,parameters FROM "+table+" WHERE rowid>? ORDER BY rowid LIMIT 48",at);
                    if(page.Count==0)break;
                    foreach(var row in page)
                    {
                        token.ThrowIfCancellationRequested();at=(long)row[0];
                        var values=reader.Deserialize<Dictionary<string,double>>((string)row[1]);
                        string[] hashes={LocalEdits.Hash(values),SegmentAssets.Hash(values),BodyAssets.ResourceHash(values),PortraitAssets.Hash(values),Imaging.CubeHash(values)};
                        for(int i=0;i<hashes.Length;i++)if(hashes[i]!=null&&!files.Contains(AssetFolders[i]+"/"+hashes[i]+AssetExt[i]))
                            throw new InvalidDataException("编辑历史缺少配套资源："+AssetFolders[i]+"/"+hashes[i]+AssetExt[i]);
                    }
                }
            }
        }
        private static void DropMachineSettings(WorkspaceDb copy)
        {
            copy.Run("DELETE FROM settings WHERE name=? OR name GLOB 'delivery/v1/job/*' OR name GLOB 'delivery/v1/rows/*'",LastRestoreKey);
        }
        private static bool LocalOriginalAvailable(OriginalRecord record,CancellationToken token)
        {
            string path=record.Path;
            // Restoring an archive must not contact an old network server on its own.
            if(String.IsNullOrEmpty(path)||path.Length<3||!Char.IsLetter(path[0])||
                path[1]!=':'||path[2]!='\\'||path.IndexOf(':',2)>=0)return false;
            var drive=new DriveInfo(path.Substring(0,3));
            if(drive.DriveType!=DriveType.Fixed&&drive.DriveType!=DriveType.Removable)return false;
            return OriginalAvailability(record,token)==Availability.Connected;
        }
        internal object ExportLibrary(string destination,CancellationToken token)
        {
            string channel=Path.GetDirectoryName(Path.GetDirectoryName(databasePath));
            destination=Path.GetFullPath(destination);
            if(File.Exists(destination)||Directory.Exists(destination))throw new IOException("请选择新的图库包文件名。");
            string parent=Path.GetDirectoryName(destination),scratch=Path.Combine(parent,".aiphoto-"+NewId());
            Directory.CreateDirectory(scratch);
            string dbCopy=Path.Combine(scratch,"catalog.sqlite3"),zipPath=Path.Combine(scratch,"catalog.zip");
            var hashes=new Dictionary<string,string>(StringComparer.Ordinal);
            try
            {
                lock(gate)db.Transaction(true,delegate {WorkspaceDb.Backup(databasePath,dbCopy);return true;});
                long photos;long totalBytes=0;
                using(var copy=new WorkspaceDb(dbCopy,false)){DropMachineSettings(copy);photos=(long)copy.Scalar("SELECT count(*) FROM photos");}
                using(var output=new FileStream(zipPath,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                using(var zip=new ZipArchive(output,ZipArchiveMode.Create))
                {
                    Action<string,string> add=(key,path)=>{
                        PlainEntry(path);using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
                        {
                            totalBytes+=stream.Length;
                            if(totalBytes>ArchiveLimit||hashes.Count>=150000)throw new IOException("整库包目前支持 16 GiB、最多 15 万个资源。");
                            using(var dest=zip.CreateEntry(key,CompressionLevel.Fastest).Open())hashes.Add(key,CopyDigest(stream,dest,stream.Length,token));
                        }
                        if(key!=LibraryDbEntry && Path.GetFileNameWithoutExtension(key)!=hashes[key])throw new InvalidDataException("编辑资源内容校验失败："+key);
                    };
                    add(LibraryDbEntry,dbCopy);
                    foreach(string folder in AssetFolders)
                    {
                        string dir=Path.Combine(channel,folder);
                        if(!Directory.Exists(dir))continue;
                        PlainEntry(Path.GetDirectoryName(dir));PlainEntry(dir);
                        foreach(string file in Directory.EnumerateFiles(dir))
                        {
                            string key=folder+"/"+Path.GetFileName(file);
                            if(ArchiveAsset(key)>=0)add(key,file);
                        }
                    }
                    using(var copy=new WorkspaceDb(dbCopy,true))CheckResources(copy,new HashSet<string>(hashes.Keys),token);
                    var manifest=new JavaScriptSerializer{MaxJsonLength=32*1024*1024}.Serialize(new{format="AIPhotoStudio.library",version=1,files=hashes});
                    using(var writer=new StreamWriter(zip.CreateEntry("manifest.json").Open(),new UTF8Encoding(false)))writer.Write(manifest);
                }
                File.Move(zipPath,destination);
                return new{path=destination,photos,assets=hashes.Count-1};
            }
            finally{RemoveTransferScratch(scratch);}
        }
        private static void RemoveTransferScratch(string owned)
        {
            if(!Directory.Exists(owned))return;
            PlainEntry(owned);
            foreach(string sub in Directory.GetDirectories(owned))RemoveTransferScratch(sub);
            foreach(string f in Directory.GetFiles(owned)){PlainEntry(f);File.Delete(f);}
            Directory.Delete(owned,false);
        }
        private sealed class LibraryManifest
        {
            public string format{get;set;} public int version{get;set;} public Dictionary<string,string> files{get;set;}
        }
        internal object RestoreLibrary(string archive,string destination,CancellationToken token)
        {
            destination=Path.GetFullPath(destination);
            if(File.Exists(destination)||Directory.Exists(destination))throw new IOException("恢复需要一个新的图库目录，已有目录不能覆盖。");
            string scratch=Path.Combine(Path.GetDirectoryName(destination),".aiphoto-"+NewId());
            string channel=Path.Combine(scratch,Program.Channel);Directory.CreateDirectory(channel);
            int missing=0;var names=new List<string>();int count=0;string[] expected;
            lock(gate)expected=SchemaObjects(db);
            try
            {
                using(var file=File.OpenRead(archive))
                using(var zip=new ZipArchive(file,ZipArchiveMode.Read))
                {
                    var manifest=zip.GetEntry("manifest.json");
                    if(manifest==null||manifest.Length>32L*1024*1024||zip.Entries.Count>150002)throw new InvalidDataException("无效或过大的图库包。");
                    LibraryManifest info;
                    using(var reader=new StreamReader(manifest.Open(),Encoding.UTF8))info=new JavaScriptSerializer{MaxJsonLength=32*1024*1024}.Deserialize<LibraryManifest>(reader.ReadToEnd());
                    if(info==null||info.version!=1||info.format!="AIPhotoStudio.library"||info.files==null||!info.files.ContainsKey(LibraryDbEntry)
                        ||info.files.Count+1!=zip.Entries.Count)throw new InvalidDataException("图库包清单不完整或版本不兼容。");
                    var seen=new HashSet<string>(StringComparer.Ordinal);long total=0;
                    foreach(var entry in zip.Entries)
                    {
                        string key=entry.FullName;if(!seen.Add(key))throw new InvalidDataException("图库包包含重复项目。");
                        if(key=="manifest.json")continue;
                        string hash;
                        if(!info.files.TryGetValue(key,out hash)||(key!=LibraryDbEntry&&(ArchiveAsset(key)<0||Path.GetFileNameWithoutExtension(key)!=hash)))
                            throw new InvalidDataException("图库包含未知文件或资源名称不符。");
                        total=checked(total+entry.Length);
                        if(total>ArchiveLimit||(key!=LibraryDbEntry&&entry.Length>64L*1024*1024))throw new InvalidDataException("图库包或其中的资源过大。");
                        string target=Path.Combine(channel,key);Directory.CreateDirectory(Path.GetDirectoryName(target));
                        using(var input=entry.Open())using(var output=new FileStream(target,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                            if(CopyDigest(input,output,entry.Length,token)!=hash||output.Length!=entry.Length)throw new InvalidDataException("图库包文件校验失败："+key);
                    }
                    using(var restored=new WorkspaceDb(Path.Combine(channel,LibraryDbEntry),false))
                    {
                        restored.Run("PRAGMA trusted_schema=OFF");
                        long version=(long)restored.Scalar("PRAGMA user_version");
                        var expectedObjects=version==15?expected.Where(s=>!s.StartsWith("table|photo_notes|",StringComparison.Ordinal)):expected;
                        if((long)restored.Scalar("PRAGMA application_id")!=ApplicationId||(version!=15&&version!=Schema)||
                            !expectedObjects.SequenceEqual(SchemaObjects(restored))||!Equals(restored.Scalar("PRAGMA quick_check"),"ok")||restored.Rows("PRAGMA foreign_key_check").Count!=0)
                            throw new InvalidDataException("备份数据库格式或完整性检查未通过。");
                        CheckResources(restored,seen,token);DropMachineSettings(restored);
                        foreach(var row in restored.Rows(SourceColumns+"WHERE "+VisiblePhotos))
                        {
                            token.ThrowIfCancellationRequested();count++;var record=SourceFrom(row);
                            if(!LocalOriginalAvailable(record,token)){missing++;if(names.Count<20)names.Add(Path.GetFileName(record.Path));}
                        }
                    }
                }
                Directory.Move(scratch,destination);
                lock(gate)db.Run("INSERT OR REPLACE INTO settings(name,value) VALUES(?,?)",LastRestoreKey,destination);
                return new{path=destination,photos=count,missing,names};
            }
            finally{RemoveTransferScratch(scratch);}
        }
        internal string LastRestoredRoot(){lock(gate)return (string)db.Scalar("SELECT value FROM settings WHERE name=?",LastRestoreKey);}
    }
}
