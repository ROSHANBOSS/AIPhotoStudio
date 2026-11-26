using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace AIPhotoStudio
{
    public static partial class LocalEdits
    {
        // Only guided strokes serialize an extra field. Ordinary and sample-repair
        // strokes retain their original four-field representation for older readers.
        private sealed class EdgeStroke : Stroke
        {
            public string edge {get;set;}
            internal EdgePlane Plane;
        }

        internal static bool UsesBrushV6(Stroke stroke){return stroke is EdgeStroke;}

        private sealed class EdgePlane
        {
            private const int Header=44;
            private readonly byte[] bytes;
            private readonly int width,height,left,top,columns,rows;
            internal readonly string Source;
            private int U16(int offset){return bytes[offset]|bytes[offset+1]<<8;}
            internal EdgePlane(string encoded)
            {
                if(encoded==null||encoded.Length<60||encoded.Length>4*((Header+768*768/8+2)/3))
                    throw new InvalidDataException("贴边笔迹尺寸无效。");
                try{bytes=Convert.FromBase64String(encoded);}catch(FormatException){throw new InvalidDataException("贴边笔迹编码无效。");}
                if(bytes.Length<Header)throw new InvalidDataException("贴边笔迹数据不完整。");
                Source=Convert.ToBase64String(bytes,0,32);
                width=U16(32);height=U16(34);left=U16(36);top=U16(38);columns=U16(40);rows=U16(42);
                if(width<1||width>768||height<1||height>768||columns<1||rows<1||
                    left+columns>width||top+rows>height||bytes.Length!=Header+(columns*rows+7)/8)
                    throw new InvalidDataException("贴边选区超出原片取样范围。");
            }
            internal bool Contains(double nx,double ny)
            {
                int x=(int)Clamp(Math.Floor(nx*width),0,width-1)-left;
                int y=(int)Clamp(Math.Floor(ny*height),0,height-1)-top;
                if(x<0||y<0||x>=columns||y>=rows)return false;
                int bit=y*columns+x;
                return (bytes[Header+(bit>>3)]&(1<<(bit&7)))!=0;
            }
        }

        private static Stroke BindEdge(Stroke stroke,string encoded)
        {
            if(stroke==null||(stroke.kind!="maskPaint"&&stroke.kind!="maskErase"))
                throw new InvalidDataException("贴边只适用于蒙版画笔和擦除。");
            return new EdgeStroke{kind=stroke.kind,points=stroke.points,radius=stroke.radius,strength=stroke.strength,
                edge=encoded,Plane=new EdgePlane(encoded)};
        }
        private static Stroke[] ReadStrokeEdges(object raw,Stroke[] strokes)
        {
            if(raw is Stroke[]||strokes==null)return strokes;
            int index=0;
            foreach(object entry in (IEnumerable)raw)
            {
                var fields=entry as IDictionary<string,object>;object encoded;
                if(fields!=null&&fields.TryGetValue("edge",out encoded))strokes[index]=BindEdge(strokes[index],encoded as string);
                index++;
            }
            return strokes;
        }
        private static string BoundBrushSource(Stroke[] strokes,string hash)
        {
            foreach(var stroke in strokes.OfType<EdgeStroke>())
            {
                if(hash!=null && hash!=stroke.Plane.Source)throw new InvalidDataException("贴边笔迹属于另一份原片。");
                hash=stroke.Plane.Source;
            }
            return hash;
        }
        private static Stroke CopyStroke(Stroke stroke)
        {
            var edge=stroke as EdgeStroke;
            Stroke copy=edge==null?new Stroke():new EdgeStroke{edge=edge.edge,Plane=edge.Plane};
            copy.kind=stroke.kind;copy.radius=stroke.radius;copy.strength=stroke.strength;
            copy.points=stroke.points.Select(p=>new Mark{x=p.x,y=p.y}).ToArray();return copy;
        }
    }

    internal sealed partial class StudioBridge
    {
        private sealed class BrushGuideFrame{internal string Digest;internal int Track;internal object Reply;}
        private BrushGuideFrame brushGuideFrame;
        private async Task<object> BrushEdgeInput(Dictionary<string,object> args)
        {
            string id=TextOf(args,"id"),path=work.PathFor(id);
            long revision=Convert.ToInt64(args["revision"]);
            work.VerifyPreviewRevision(id,revision);
            var neutral=work.NeutralFor(id,path,revision);
            int track=CameraRawModes.Selected(neutral);
            string hash=await Task.Run(()=>BodyAssets.SourceDigest(path));
            var cached=brushGuideFrame;
            if(cached!=null&&cached.Digest==hash&&cached.Track==track){
                work.VerifyPreviewRevision(id,revision);return cached.Reply;
            }
            var image=await Render(path,neutral,768);
            var frame=Map(await EncodePreview(image));
            if(hash!=await Task.Run(()=>BodyAssets.SourceDigest(path)))throw new InvalidOperationException("原片在贴边取样期间发生变化，请重新开启贴边。");
            work.VerifyPreviewRevision(id,revision);
            var reply=new {digest=hash,image=frame["image"]};
            brushGuideFrame=new BrushGuideFrame{Digest=hash,Track=track,Reply=reply};return reply;
        }
    }
}
