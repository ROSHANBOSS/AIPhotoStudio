using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AIPhotoStudio
{
    public static partial class LocalEdits
    {
        internal sealed class AreaMark
        {
            public string kind {get;set;}
            public Mark[] points {get;set;}
            public double? radius {get;set;}

            internal Int32Rect Bounds(int width,int height)
            {
                var a=points[0];var b=points[1];
                int x=(int)Math.Floor(Math.Min(a.x,b.x)*width),y=(int)Math.Floor(Math.Min(a.y,b.y)*height);
                int right=(int)Math.Ceiling(Math.Max(a.x,b.x)*width),bottom=(int)Math.Ceiling(Math.Max(a.y,b.y)*height);
                return new Int32Rect(x,y,right-x,bottom-y);
            }
        }

        internal static AreaMark[] ReadAreas(object raw)
        {
            var entries=raw as IEnumerable;
            if(entries==null||raw is string)throw new InvalidDataException("缺少矩形选区。");
            var result=new List<AreaMark>();int totalPoints=0;
            foreach(object entry in entries)
            {
                var fields=entry as IDictionary<string,object>;object kind,points;
                if(result.Count>=64||fields==null||!fields.TryGetValue("kind",out kind)||
                    !fields.TryGetValue("points",out points)||!(points is IEnumerable)||points is string)
                    throw new InvalidDataException("请使用有效的区域标记，每张照片最多 64 个。");
                string name=kind as string;bool brush=name=="guardPaint"||name=="guardErase";
                if(!brush&&name!="redactRect"&&name!="protectRect")throw new InvalidDataException("不支持的区域标记。");
                object rawRadius;bool hasRadius=fields.TryGetValue("radius",out rawRadius);
                if(fields.Count!=(hasRadius?3:2)||!brush&&rawRadius!=null||brush&&rawRadius==null)
                    throw new InvalidDataException("区域标记的数据不完整。");
                double? radius=rawRadius==null?(double?)null:StudioWorkspace.Number(rawRadius);
                if(brush&&!(radius>=.003&&radius<=.45))throw new InvalidDataException("保护画笔半径超出范围。");
                var corners=new List<Mark>();
                foreach(object value in (IEnumerable)points)
                {
                    var p=value as IDictionary<string,object>;object rawX,rawY;
                    if(++totalPoints>4096||corners.Count>=(brush?512:2)||p==null||p.Count!=2||!p.TryGetValue("x",out rawX)||!p.TryGetValue("y",out rawY))
                        throw new InvalidDataException("标记坐标无效或笔迹点数过多。");
                    double x=StudioWorkspace.Number(rawX),y=StudioWorkspace.Number(rawY);
                    if(!(x>=0&&x<=1&&y>=0&&y<=1))throw new InvalidDataException("位置必须在照片内。");
                    corners.Add(new Mark{x=x,y=y});
                }
                if(corners.Count==0||!brush&&(corners.Count!=2||corners[0].x==corners[1].x||corners[0].y==corners[1].y))
                    throw new InvalidDataException("请拖出有宽度和高度的矩形。");
                result.Add(new AreaMark{kind=name,points=corners.ToArray(),radius=radius});
            }
            return result.ToArray();
        }

        internal static Int32Rect[] RedactionBounds(AreaMark[] marks,int w,int h,int orientation)
        {
            if(marks==null||marks.Length==0)return null;
            int width=orientation>=5?h:w,height=orientation>=5?w:h;
            return marks.Where(m=>m.kind=="redactRect").Select(m=>Imaging.StoredRect(m.Bounds(width,height),w,h,orientation)).ToArray();
        }

        // A source pixel that intersects a rectangle is replaced, including hidden RGB in transparent pixels.
        internal static void FillRedactions(ushort[] buffer,Int32Rect tile,Int32Rect[] boxes)
        {
            if(boxes==null)return;
            foreach(var box in boxes)
            {
                int left=Math.Max(tile.X,box.X),top=Math.Max(tile.Y,box.Y);
                int endX=Math.Min(tile.X+tile.Width,box.X+box.Width),endY=Math.Min(tile.Y+tile.Height,box.Y+box.Height);
                for(int y=top;y<endY;y++)
                {
                    int i=((y-tile.Y)*tile.Width+left-tile.X)*4;
                    for(int x=left;x<endX;x++,i+=4){buffer[i]=buffer[i+1]=buffer[i+2]=0;buffer[i+3]=65535;}
                }
            }
        }

        internal static BitmapSource CloseRedactions(BitmapSource pixels,AreaMark[] marks,ToneRegion region,CancellationToken cancel)
        {
            if(marks==null||!marks.Any(mark=>mark.kind=="redactRect"))return pixels;
            if(pixels.Format!=PixelFormats.Rgba64){pixels=new FormatConvertedBitmap(pixels,PixelFormats.Rgba64,null,0);pixels.Freeze();}
            int width=region==null?pixels.PixelWidth:region.Width,height=region==null?pixels.PixelHeight:region.Height;
            int x0=region==null?0:region.Area.X,y0=region==null?0:region.Area.Y;
            WriteableBitmap output=null;
            foreach(var box in RedactionBounds(marks,width,height,1))
            {
                int left=Math.Max(x0,box.X),top=Math.Max(y0,box.Y);
                int w=Math.Min(x0+pixels.PixelWidth,box.X+box.Width)-left,h=Math.Min(y0+pixels.PixelHeight,box.Y+box.Height)-top;
                if(w<=0||h<=0)continue;
                int rows=Math.Max(1,Math.Min(h,131072/w));var black=new ushort[w*rows*4];
                for(int i=3;i<black.Length;i+=4)black[i]=65535;
                ushort[] read=output==null?new ushort[black.Length]:null;
                for(int row=0;row<h;row+=rows)
                {
                    cancel.ThrowIfCancellationRequested();
                    var rect=new Int32Rect(left-x0,top-y0+row,w,Math.Min(rows,h-row));
                    if(output==null)
                    {
                        // Most boxes are already black from the source pass. Avoid copying a full camera image just to reapply one.
                        pixels.CopyPixels(rect,read,w*8,0);int count=w*rect.Height*4,index=0;
                        while(index<count&&read[index]==black[index])index++;
                        if(index==count)continue;
                        output=new WriteableBitmap(pixels);
                    }
                    output.WritePixels(rect,black,w*8,0);
                }
            }
            if(output==null)return pixels;
            output.Freeze();return output;
        }
    }
}
