using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Media.Imaging;

namespace AIPhotoStudio
{
    public static partial class Imaging
    {
        internal const long FullViewportCacheBytes=384L*1024*1024;
        internal sealed class DragCache
        {
            private readonly string[] keys=new string[4];
            private readonly BitmapSource[] surfaces=new BitmapSource[4];
            private readonly long[] access=new long[4];
            private long clock,generation;
            private readonly bool fullSize;
            internal DragCache(bool fullSize=false){this.fullSize=fullSize;}
            internal void Clear()
            {
                lock(keys){generation++;Array.Clear(keys,0,keys.Length);Array.Clear(surfaces,0,surfaces.Length);}
            }
            internal void TrimTo(long budget)
            {
                lock(keys)while(surfaces.Where(s=>s!=null).Distinct().Sum(s=>(long)s.PixelWidth*s.PixelHeight*8)>budget)
                {
                    int oldest=Enumerable.Range(0,surfaces.Length).Where(i=>surfaces[i]!=null).OrderBy(i=>access[i]).First();
                    keys[oldest]=null;surfaces[oldest]=null;
                }
            }
            internal BitmapSource Read(int stage,string key,Func<BitmapSource> render)
            {
                if(fullSize&&stage==1)return render();
                long started;
                lock(keys)
                {
                    if(keys[stage]==key){access[stage]=++clock;return surfaces[stage];}
                    started=generation;
                }
                var surface=render();
                long pixels=(long)surface.PixelWidth*surface.PixelHeight;
                if(surface.IsFrozen&&(fullSize?pixels*8<=FullViewportCacheBytes:pixels<=512*512))
                    lock(keys)
                    {
                        if(started!=generation)return surface;
                        surfaces[stage]=surface;keys[stage]=key;access[stage]=++clock;
                        if(fullSize)TrimTo(FullViewportCacheBytes);
                    }
                return surface;
            }
        }
        internal sealed class DragStages
        {
            internal readonly DragCache Cache;
            internal readonly string[] Keys;
            internal readonly string Neutral;
            internal DragStages(DragCache cache,string source,IDictionary<string,double> values)
            {
                Cache=cache;
                Neutral=source+"|track="+CameraRawModes.Selected(values);
                var groups=new[]{new StringBuilder(source),new StringBuilder(),new StringBuilder()};
                foreach(var pair in values.OrderBy(p=>p.Key,StringComparer.Ordinal))
                {
                    string k=pair.Key;
                    if(k.StartsWith("local_",StringComparison.Ordinal))continue;
                    int stage=k.StartsWith("body_",StringComparison.Ordinal)||k.StartsWith("mask_ref",StringComparison.Ordinal)?2:
                        k.StartsWith("portrait_",StringComparison.Ordinal)||k.StartsWith("segment_",StringComparison.Ordinal)?1:0;
                    groups[stage].Append('|').Append(k).Append('=').Append(pair.Value.ToString("R",CultureInfo.InvariantCulture));
                }
                string global=groups[0].ToString(),region=global+groups[1].ToString();
                Keys=new[]{global,region,region+groups[2].ToString()};
            }
        }
        internal static BitmapSource RenderDrag(string path,IDictionary<string,double> values,int edge,CancellationToken token,LocalEdits.MaskSet draft,DragCache cache)
        {
            if(draft==null&&LocalEdits.Hash(values)==null&&PortraitAssets.Hash(values)==null&&!BodyEdits.Active(values)&&SegmentAssets.Hash(values)==null)
                return Render(path,values,edge,token,draft);
            // Hold the handle through rendering so the key and pixels always describe one read-only file.
            using(var stream=ImagingInput.OpenRead(path))
            {
                var source=path+"|"+CameraIdentity(stream,token)+"|"+edge+"|"+GpuColorPass.CpuOnly;
                return Render(path,values,edge,token,draft,null,new DragStages(cache,source,values));
            }
        }
    }
}
