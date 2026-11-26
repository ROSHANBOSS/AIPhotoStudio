using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace AIPhotoStudio
{
    internal sealed class CaptureInbox
    {
        private sealed class Entry
        {
            internal long Length,Time;
            internal bool Done;
        }
        private readonly Dictionary<string,Entry> seen=new Dictionary<string,Entry>(StringComparer.OrdinalIgnoreCase);
        private readonly ISet<string> extensions;
        internal readonly string DirectoryPath;
        internal int Imported,Failed;
        internal string LastFile="",LastError="";

        internal CaptureInbox(string dir,ISet<string> allowed)
        {
            DirectoryPath=Path.GetFullPath(dir);extensions=allowed;
            Read(null,CancellationToken.None);
        }
        internal void RegisterCapture(string path)
        {
            var file=new FileInfo(path);
            seen[path]=new Entry{Length=file.Length,Time=file.LastWriteTimeUtc.Ticks,Done=true};
            Imported++;LastFile=file.Name;
        }
        internal int Read(Action<string> import,CancellationToken token)
        {
            int added=0;
            var present=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(string path in Directory.EnumerateFiles(DirectoryPath))
            {
                token.ThrowIfCancellationRequested();
                if(!extensions.Contains(Path.GetExtension(path)))continue;
                present.Add(path);
                var file=new FileInfo(path);
                if(!file.Exists)continue;
                long length=file.Length,time=file.LastWriteTimeUtc.Ticks;
                Entry last;
                if(!seen.TryGetValue(path,out last)||last.Length!=length||last.Time!=time)
                {
                    seen[path]=new Entry{Length=length,Time=time,Done=import==null};
                    continue;
                }
                if(last.Done||length==0||import==null)continue;
                try
                {
                    // A writer which still has the file open prevents this shared-read handle.
                    using(var handle=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
                    {
                        if(handle.Length!=length||File.GetLastWriteTimeUtc(path).Ticks!=time)continue;
                        import(path);
                    }
                    last.Done=true;Imported++;added++;LastFile=file.Name;
                }
                catch(OperationCanceledException) { throw; }
                catch(Exception e)
                {
                    var io=e.GetBaseException() as IOException;
                    if(io!=null && TemporaryReadFailure(io))continue;
                    last.Done=true;Failed++;LastError=file.Name+" · "+e.GetBaseException().Message;
                }
            }
            foreach(string path in seen.Keys.Where(p=>!present.Contains(p)).ToArray())seen.Remove(path);
            return added;
        }
        private static bool TemporaryReadFailure(IOException error)
        {
            // A writer, a renamed file or a disconnected volume may become readable on a later poll.
            switch(unchecked((uint)error.HResult))
            {
                case 0x80070002: case 0x80070003: case 0x80070015: // file/path missing, device not ready
                case 0x80070020: case 0x80070021:                  // sharing/lock violation
                case 0x80070035: case 0x80070040: case 0x80070043: // network path/name unavailable
                case 0x8007048F: return true;                      // device disconnected
                default: return false;
            }
        }
    }
}
