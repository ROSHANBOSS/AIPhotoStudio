using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace AIPhotoStudio
{
    // Only files created by this thumbnail store are eligible for the library cleanup button.
    internal sealed class GalleryPreviewCache
    {
        private const int MaxFrame=256*1024;
        private const long Limit=64L*1024*1024;
        private static readonly byte[] Magic=Encoding.ASCII.GetBytes("APS-thumb-v2\n");
        private readonly string folder;
        private readonly object gate=new object();
        private int writes,epoch;
        internal GalleryPreviewCache(string channel){folder=Path.Combine(channel,"Cache","PhotoPreviews");}
        private static string Digest(string descriptor)
        {
            using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(descriptor))).Replace("-","").ToLowerInvariant();
        }
        private bool FolderReady(bool create)
        {
            foreach(string dir in new[]{Path.GetDirectoryName(Path.GetDirectoryName(folder)),Path.GetDirectoryName(folder),folder})
            {
                if(!Directory.Exists(dir)){if(!create)return false;Directory.CreateDirectory(dir);}
                if((File.GetAttributes(dir)&FileAttributes.ReparsePoint)!=0)return false;
            }
            return true;
        }
        private string FileFor(string id)
        {
            Guid parsed;if(!Guid.TryParseExact(id,"N",out parsed))throw new ArgumentException("照片编号无效。");
            return Path.Combine(folder,parsed.ToString("N")+".apthumb");
        }
        private static bool Owned(FileInfo f)
        {
            Guid parsed;
            if(f.Extension!=".apthumb"||!Guid.TryParseExact(Path.GetFileNameWithoutExtension(f.Name),"N",out parsed)||
                (f.Attributes&FileAttributes.ReparsePoint)!=0||f.Length<Magic.Length+129||f.Length>MaxFrame)return false;
            using(var input=new FileStream(f.FullName,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))
            using(var reader=new BinaryReader(input))return Magic.SequenceEqual(reader.ReadBytes(Magic.Length));
        }
        internal string Read(string id,string descriptor,out int generation)
        {
            lock(gate)
            {
                generation=epoch;
                try
                {
                    var f=new FileInfo(FileFor(id));if(!FolderReady(false)||!f.Exists||!Owned(f))return null;
                    using(var input=new FileStream(f.FullName,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete))
                    using(var reader=new BinaryReader(input))
                    {
                        if(!reader.ReadBytes(Magic.Length).SequenceEqual(Magic)||Encoding.ASCII.GetString(reader.ReadBytes(64))!=Digest(descriptor))return null;
                        string contentHash=Encoding.ASCII.GetString(reader.ReadBytes(64));
                        string data=Encoding.ASCII.GetString(reader.ReadBytes(MaxFrame));
                        return data.StartsWith("data:image/png;base64,",StringComparison.Ordinal)&&Digest(data)==contentHash?data:null;
                    }
                }
                catch(IOException){return null;}catch(UnauthorizedAccessException){return null;}
            }
        }
        internal void Save(string id,string descriptor,string image,int generation)
        {
            lock(gate)
            {
                if(generation!=epoch||!image.StartsWith("data:image/png;base64,",StringComparison.Ordinal)||image.Length+Magic.Length+128>MaxFrame)return;
                string tmp=null;
                try
                {
                    if(!FolderReady(true))return;
                    string dest=FileFor(id);
                    tmp=Path.Combine(folder,Guid.NewGuid().ToString("N")+".tmp");
                    using(var output=new FileStream(tmp,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                    using(var writer=new BinaryWriter(output))
                    {
                        writer.Write(Magic);writer.Write(Encoding.ASCII.GetBytes(Digest(descriptor)));
                        writer.Write(Encoding.ASCII.GetBytes(Digest(image)));writer.Write(Encoding.ASCII.GetBytes(image));
                    }
                    if(File.Exists(dest)){if(!Owned(new FileInfo(dest)))return;File.Replace(tmp,dest,null);}else File.Move(tmp,dest);
                    if(++writes%24==1)
                    {
                        var list=Files().OrderBy(f=>f.LastWriteTimeUtc).ToList();long bytes=list.Sum(f=>f.Length);
                        foreach(var f in list){if(bytes<=Limit)break;long n=f.Length;f.Delete();bytes-=n;}
                    }
                }
                catch(IOException){}catch(UnauthorizedAccessException){}
                finally{if(tmp!=null&&File.Exists(tmp)){try{File.Delete(tmp);}catch(IOException){}catch(UnauthorizedAccessException){}}}
            }
        }
        private List<FileInfo> Files()
        {
            var result=new List<FileInfo>();
            if(FolderReady(false))foreach(var f in new DirectoryInfo(folder).EnumerateFiles("*.apthumb"))
            {
                try{if(Owned(f))result.Add(f);}catch(IOException){}catch(UnauthorizedAccessException){}
            }
            return result;
        }
        internal long Measure(){lock(gate)return Files().Sum(f=>f.Length);}
        internal long Clear()
        {
            lock(gate)
            {
                epoch++;long released=0;
                foreach(var f in Files())
                {
                    try{long bytes=f.Length;f.Delete();released+=bytes;}catch(IOException){}catch(UnauthorizedAccessException){}
                }
                return released;
            }
        }
    }
}
