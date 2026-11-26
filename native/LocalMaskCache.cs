using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace AIPhotoStudio
{
    public static partial class LocalEdits
    {
        private static readonly object maskCacheGate=new object();
        private const int ParsedBudget=8*1024*1024,GridBudget=24*1024*1024;
        private sealed class ParsedEntry
        {
            internal string Path;
            internal MaskSet Set;
            internal int Bytes;
        }
        private sealed class GridEntry
        {
            internal string Path;
            internal int Slot,Width,Height;
            internal bool Corrections;
            internal Grid Field;
            internal bool Matches(string path,int slot,int width,int height,bool corrections)
            {
                return Path==path&&Slot==slot&&Width==width&&Height==height&&Corrections==corrections;
            }
        }
        private static readonly List<ParsedEntry> parsedSets=new List<ParsedEntry>();
        private static readonly List<GridEntry> savedGrids=new List<GridEntry>();

        // Cached DTOs are private to LocalEdits. ReadSet and public stroke reads
        // return isolated copies; immutable base64 strings can be shared safely.
        private static MaskSet ReadOnlySet(IDictionary<string,double> values)
        {
            string hash=Hash(values);
            if(hash==null)return Legacy(new Stroke[0]);
            string path=Resource(hash);
            using(var file=OpenMask(path))
            {
                if(file.Length>MaxMaskBytes)throw new InvalidDataException("独立蒙版资源超过大小限制。");
                MaskSet hit=null;
                lock(maskCacheGate)
                {
                    int n=parsedSets.FindIndex(p=>p.Path==path);
                    if(n>=0)
                    {
                        var entry=parsedSets[n];parsedSets.RemoveAt(n);parsedSets.Insert(0,entry);hit=entry.Set;
                    }
                }
                if(hit!=null)
                {
                    // Revalidate even on a hit. On Windows this checks the file ID and
                    // change timestamp; other filesystems rehash the open file.
                    if(BodyAssets.SourceDigest(path)!=hash)throw new InvalidDataException("局部蒙版校验失败。");
                    return hit;
                }
                var bytes=VerifiedBytes(file,hash);
                var set=Decode(bytes);set.CachedPath=path;
                lock(maskCacheGate)
                {
                    var published=parsedSets.Find(p=>p.Path==path);
                    if(published!=null)return published.Set;
                    parsedSets.Insert(0,new ParsedEntry{Path=path,Set=set,Bytes=bytes.Length});
                    while(parsedSets.Count>6||parsedSets.Sum(p=>p.Bytes)>ParsedBudget)parsedSets.RemoveAt(parsedSets.Count-1);
                }
                return set;
            }
        }

        private static FileStream OpenMask(string path)
        {
            try{return WorkspacePaths.OpenRead(path,FileShare.Read);}
            catch(FileNotFoundException){throw new FileNotFoundException("找不到此配方的蒙版，请重新导入完整配方。",path);}
        }

        private static byte[] VerifiedBytes(Stream input,string hash)
        {
            if(input.Length>MaxMaskBytes)throw new InvalidDataException("独立蒙版资源超过大小限制。");
            byte[] bytes=new byte[checked((int)input.Length)];
            int offset=0;
            while(offset<bytes.Length)
            {
                int count=input.Read(bytes,offset,bytes.Length-offset);
                if(count==0)throw new EndOfStreamException("蒙版文件未完整写入。");
                offset+=count;
            }
            if(Digest(bytes)!=hash)throw new InvalidDataException("局部蒙版校验失败。");
            return bytes;
        }

        private static Grid CoverageGrid(MaskSet set,int slot,int width,int height,CancellationToken cancel,bool corrections)
        {
            cancel.ThrowIfCancellationRequested();
            string path=set.CachedPath;
            if(path!=null)lock(maskCacheGate)
            {
                int i=savedGrids.FindIndex(g=>g.Matches(path,slot,width,height,corrections));
                if(i>=0)
                {
                    var entry=savedGrids[i];savedGrids.RemoveAt(i);savedGrids.Insert(0,entry);
                    return entry.Field;
                }
            }
            var grid=new Grid(width,height,set.Strokes(slot),cancel,corrections?null:set.Alpha(slot),set.Guard(slot),corrections);
            if(path==null||grid.Bytes>GridBudget)return grid;
            lock(maskCacheGate)
            {
                var existing=savedGrids.Find(g=>g.Matches(path,slot,width,height,corrections));
                if(existing!=null)return existing.Field;
                savedGrids.Insert(0,new GridEntry{Path=path,Slot=slot,Width=width,Height=height,Corrections=corrections,Field=grid});
                while(savedGrids.Count>16||savedGrids.Sum(g=>g.Field.Bytes)>GridBudget)savedGrids.RemoveAt(savedGrids.Count-1);
            }
            return grid;
        }
    }
}
