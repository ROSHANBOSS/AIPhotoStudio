using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AIPhotoStudio
{
    // A tile stores the minimum of each channel per short run, followed by its exact
    // unsigned differences. Every tile is independently readable, including the alpha channel.
    internal sealed class PackedCameraSurface
    {
        private const int TileWidth=256,TileHeight=32,RunWidth=32;
        // Twelve header bytes per run plus two lookahead bytes for the bit reader.
        private const int TileCapacity=TileHeight*(TileWidth*8+(TileWidth/RunWidth)*12)+2;
        private static readonly byte[] BitWidth=BuildWidths();
        private readonly int width,height,columns;
        private readonly byte[][] tiles;
        internal long Bytes {get;private set;}
        private long InitialBytes {get{return 256L+tiles.Length*40L;}}

        private PackedCameraSurface(int w,int h)
        {
            width=w;height=h;columns=(w+TileWidth-1)/TileWidth;
            tiles=new byte[columns*((h+TileHeight-1)/TileHeight)][];
        }

        private static byte[] BuildWidths()
        {
            var widths=new byte[65536];
            for(int i=1;i<widths.Length;i++)widths[i]=(byte)(widths[i>>1]+1);
            return widths;
        }

        private sealed class BandBuffer
        {
            internal readonly ushort[] Pixels;
            internal readonly byte[] Codes=new byte[TileCapacity];
            internal BandBuffer(int width,int rows) {Pixels=new ushort[checked(width*rows*4)];}
        }

        private bool StoreTile(int band,int col,int rows,ushort[] pixels,byte[] codes,ref long used,long budget)
        {
            int x=col*TileWidth,n=Math.Min(TileWidth,width-x),bytes=0;
            for(int row=0;row<rows;row++)
                bytes=PackRow(pixels,(row*width+x)*4,n,codes,bytes);
            if(Interlocked.Add(ref used,bytes+2)>budget)return false;
            var tile=new byte[bytes+2];
            Buffer.BlockCopy(codes,0,tile,0,bytes);
            tiles[band*columns+col]=tile;
            return true;
        }

        // The RAW worker already sends rows in order. Keep only a few rows of uncompressed
        // pixels while decoding. If the budget is reached, restore earlier bands losslessly.
        internal static PackedCameraSurface ReadRows(int w,int h,long budget,Action<byte[],int> read,
            CancellationToken cancel,out BitmapSource fallback,bool requireFit=false)
        {
            cancel.ThrowIfCancellationRequested();
            var result=new PackedCameraSurface(w,h);
            int stride=checked(w*8),bandRows=Math.Min(TileHeight,h);
            var bytes=new byte[checked(stride*bandRows)];
            var pixels=new ushort[bytes.Length/2];
            int workers=Math.Min(result.columns,Math.Min(6,Environment.ProcessorCount));
            var buffers=new byte[workers][];
            for(int i=0;i<workers;i++)buffers[i]=new byte[TileCapacity];
            var options=new ParallelOptions{CancellationToken=cancel,MaxDegreeOfParallelism=workers};
            long used=result.InitialBytes;
            WriteableBitmap full=null;
            for(int y=0;y<h;y+=TileHeight)
            {
                cancel.ThrowIfCancellationRequested();
                int count=Math.Min(TileHeight,h-y),band=y/TileHeight;
                read(bytes,count*stride);
                if(full==null)
                {
                    Buffer.BlockCopy(bytes,0,pixels,0,count*stride);
                    var work=Parallel.For(0,workers,options,(i,state)=>{
                        for(int col=i;col<result.columns&&!state.IsStopped;col+=workers)
                            if(!result.StoreTile(band,col,count,pixels,buffers[i],ref used,budget)){state.Stop();break;}
                    });
                    if(work.IsCompleted)continue;
                    if(requireFit)throw new NotSupportedException("原像素层超过预留缓存大小，放大时将按需读取。");
                    full=new WriteableBitmap(w,h,96,96,PixelFormats.Rgba64,null);
                    for(int prior=0;prior<y;prior+=TileHeight)
                    {
                        var rect=new Int32Rect(0,prior,w,TileHeight);
                        result.Copy(rect,pixels,cancel);
                        full.WritePixels(rect,pixels,stride,0);
                        Array.Clear(result.tiles,prior/TileHeight*result.columns,result.columns);
                    }
                    result=null;
                }
                full.WritePixels(new Int32Rect(0,y,w,count),bytes,stride,0);
            }
            if(full!=null)full.Freeze();
            else result.Bytes=used;
            fallback=full;
            return result;
        }

        private static int PackRow(ushort[] pixels,int start,int count,byte[] output,int write)
        {
            for(int x=0;x<count;x+=RunWidth)
            {
                int length=Math.Min(RunWidth,count-x);
                int end=start+(x+length)*4;
                for(int channel=0;channel<4;channel++)
                {
                    int first=start+x*4+channel,min=pixels[first],max=min;
                    for(int i=first+4;i<end;i+=4)
                    {
                        int v=pixels[i];
                        if(v<min)min=v;else if(v>max)max=v;
                    }
                    int bits=BitWidth[max-min];
                    output[write++]=(byte)min;output[write++]=(byte)(min>>8);output[write++]=(byte)bits;
                    if(bits==0)continue;
                    uint pending=0;int shift=0;
                    for(int i=first;i<end;i+=4)
                    {
                        pending|=(uint)(pixels[i]-min)<<shift;shift+=bits;
                        if(shift>=16)
                        {
                            output[write++]=(byte)pending;output[write++]=(byte)(pending>>8);
                            pending>>=16;shift-=16;
                        }
                    }
                    if(shift>0)output[write++]=(byte)pending;
                    if(shift>8)output[write++]=(byte)(pending>>8);
                }
            }
            return write;
        }

        // Skip expensive full-frame packing when a small, spatially distributed sample
        // already exceeds the budget by a wide margin. The full pass enforces the real byte limit.
        private static bool MayFit(BitmapSource source,long budget,CancellationToken cancel)
        {
            int w=source.PixelWidth,h=source.PixelHeight,len=Math.Min(TileWidth,w);
            int nx=Math.Min(8,(w+len-1)/len),ny=Math.Min(16,h);
            var sample=new BandBuffer(len,1);long size=0;
            for(int y=0;y<ny;y++)
            {
                cancel.ThrowIfCancellationRequested();
                for(int x=0;x<nx;x++)
                {
                    int left=(int)((2L*x+1)*(w-len)/(2*nx));
                    int top=(int)((2L*y+1)*h/(2*ny));
                    source.CopyPixels(new Int32Rect(left,top,len,1),sample.Pixels,len*8,0);
                    size+=PackRow(sample.Pixels,0,len,sample.Codes,0);
                }
            }
            return (double)size*w*h/(nx*ny*len)<=budget*1.10;
        }

        internal static PackedCameraSurface TryCreate(BitmapSource source,long budget,CancellationToken cancel)
        {
            cancel.ThrowIfCancellationRequested();
            if(source.Format!=PixelFormats.Rgba64||!source.IsFrozen||!MayFit(source,budget,cancel))return null;
            var result=new PackedCameraSurface(source.PixelWidth,source.PixelHeight);
            int w=result.width,h=result.height;
            // Allow for the tile references, object headers and allocation alignment in addition to payload.
            long used=result.InitialBytes;
            var options=new ParallelOptions{CancellationToken=cancel,MaxDegreeOfParallelism=Math.Min(6,Environment.ProcessorCount)};
            var work=Parallel.For<BandBuffer>(0,(h+TileHeight-1)/TileHeight,options,
                ()=>new BandBuffer(w,Math.Min(TileHeight,h)),(band,state,buffer)=>
            {
                int y=band*TileHeight,rows=Math.Min(TileHeight,h-y);
                source.CopyPixels(new Int32Rect(0,y,w,rows),buffer.Pixels,w*8,0);
                for(int col=0;col<result.columns;col++)
                {
                    if(state.IsStopped)break;
                    cancel.ThrowIfCancellationRequested();
                    if(!result.StoreTile(band,col,rows,buffer.Pixels,buffer.Codes,ref used,budget)){state.Stop();break;}
                }
                return buffer;
            },buffer=>{});
            if(!work.IsCompleted)return null;
            result.Bytes=used;
            return result;
        }

        internal void Copy(Int32Rect area,ushort[] target,CancellationToken cancel)
        {
            int right=area.X+area.Width,bottom=area.Y+area.Height;
            for(int ty=area.Y/TileHeight;ty<=(bottom-1)/TileHeight;ty++)
            {
                cancel.ThrowIfCancellationRequested();
                for(int tx=area.X/TileWidth;tx<=(right-1)/TileWidth;tx++)
                {
                    byte[] tile=tiles[ty*columns+tx];int cursor=0;
                    int tileX=tx*TileWidth,tileRight=Math.Min(width,tileX+TileWidth);
                    for(int y=ty*TileHeight;y<Math.Min(bottom,(ty+1)*TileHeight);y++)
                        for(int x=tileX;x<tileRight;x+=RunWidth)
                        {
                            int n=Math.Min(RunWidth,tileRight-x),begin=Math.Max(x,area.X),end=Math.Min(x+n,right);
                            for(int channel=0;channel<4;channel++)
                            {
                                int min=tile[cursor]|tile[cursor+1]<<8,bits=tile[cursor+2];
                                cursor+=3;
                                if(y>=area.Y&&begin<end)
                                {
                                    int dest=((y-area.Y)*area.Width+begin-area.X)*4+channel;
                                    int mask=(1<<bits)-1,bit=(begin-x)*bits;
                                    if(bits==0)
                                        for(int j=begin;j<end;j++,dest+=4)target[dest]=(ushort)min;
                                    else for(int j=begin;j<end;j++,dest+=4,bit+=bits)
                                    {
                                        int p=cursor+(bit>>3),word=tile[p]|tile[p+1]<<8|tile[p+2]<<16;
                                        target[dest]=(ushort)(min+((word>>(bit&7))&mask));
                                    }
                                }
                                cursor+=(bits*n+7)/8;
                            }
                        }
                }
            }
        }
    }
}
