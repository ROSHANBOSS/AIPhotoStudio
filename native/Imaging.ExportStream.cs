using System.IO;
using System.Threading;

namespace AIPhotoStudio
{
    public static partial class Imaging
    {
        // WIC writes in chunks. Check the token between encoder writes as well
        // as during rendering, before the completed temporary file is published.
        private sealed class ExportStream : Stream
        {
            private readonly Stream output;
            private readonly CancellationToken cancel;
            internal ExportStream(Stream output,CancellationToken cancel){this.output=output;this.cancel=cancel;}
            public override bool CanRead {get{return output.CanRead;}}
            public override bool CanWrite {get{return output.CanWrite;}}
            public override bool CanSeek {get{return output.CanSeek;}}
            public override long Length {get{return output.Length;}}
            public override long Position {get{return output.Position;}set{output.Position=value;}}
            public override long Seek(long offset,SeekOrigin origin){return output.Seek(offset,origin);}
            public override int Read(byte[] buffer,int offset,int count){return output.Read(buffer,offset,count);}
            public override void SetLength(long length){cancel.ThrowIfCancellationRequested();output.SetLength(length);}
            public override void Write(byte[] buffer,int offset,int count){cancel.ThrowIfCancellationRequested();output.Write(buffer,offset,count);}
            public override void Flush(){cancel.ThrowIfCancellationRequested();output.Flush();}
        }
    }
}
