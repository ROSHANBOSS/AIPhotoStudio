using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;

namespace AIPhotoStudio
{
    // Rectangle edges are evaluated on the output pixel lattice, independent of the coarser body mesh.
    // A small row cache also serves bilinear donor checks without building a full image-sized mask.
    internal sealed class BodyGuard
    {
        private sealed class Shape
        {
            internal Int32Rect Box;
            internal double[] X,Y;
            internal double Radius;
            internal bool Erase;
        }
        private readonly Shape[] shapes;
        private readonly int width,height;
        private readonly CancellationToken cancel;
        private readonly Func<int,int,double> previous;
        private readonly Int32Rect bounds;
        private readonly int[] tags=Enumerable.Repeat(-1,16).ToArray();
        private readonly Dictionary<int,int> rowIndex=new Dictionary<int,int>(16);
        private readonly float[][] rows=new float[16][];
        private readonly float[] brush;
        private int nextRow,lastRow;
        internal BodyGuard(LocalEdits.AreaMark[] marks,int w,int h,Func<int,int,double> legacy,CancellationToken token)
        {
            width=w;height=h;previous=legacy;cancel=token;brush=new float[w];
            shapes=marks.Where(m=>m.kind!="redactRect").Select(m=>{
                if(m.kind=="protectRect")return new Shape{Box=m.Bounds(w,h)};
                double radius=m.radius.Value*Math.Min(w,h);var x=m.points.Select(p=>p.x*w-.5).ToArray();var y=m.points.Select(p=>p.y*h-.5).ToArray();
                int l=Math.Max(0,(int)Math.Floor(x.Min()-radius)),t=Math.Max(0,(int)Math.Floor(y.Min()-radius));
                int r=Math.Min(w,(int)Math.Ceiling(x.Max()+radius)+1),b=Math.Min(h,(int)Math.Ceiling(y.Max()+radius)+1);
                return new Shape{Box=new Int32Rect(l,t,r-l,b-t),X=x,Y=y,Radius=radius,Erase=m.kind=="guardErase"};
            }).ToArray();
            int left=shapes.Min(s=>s.Box.X),top=shapes.Min(s=>s.Box.Y);
            bounds=new Int32Rect(left,top,shapes.Max(s=>s.Box.X+s.Box.Width)-left,shapes.Max(s=>s.Box.Y+s.Box.Height)-top);
        }
        internal double At(int x,int y)
        {
            if(previous==null&&(x<bounds.X||x>=bounds.X+bounds.Width||y<bounds.Y||y>=bounds.Y+bounds.Height))return 0;
            int index=lastRow;
            if(tags[index]!=y&&!rowIndex.TryGetValue(y,out index))
            {
                index=nextRow;nextRow=(nextRow+1)%rows.Length;
                if(tags[index]>=0)rowIndex.Remove(tags[index]);
                var row=rows[index]??(rows[index]=new float[width]);
                if(previous==null)Array.Clear(row,0,width);else for(int i=0;i<width;i++)row[i]=(float)previous(i,y);
                foreach(var shape in shapes)
                {
                    var box=shape.Box;if(y<box.Y||y>=box.Y+box.Height)continue;
                    if(shape.X==null){for(int i=box.X;i<box.X+box.Width;i++)row[i]=1;continue;}
                    BrushRow(shape,y,row);
                }
                tags[index]=y;rowIndex[y]=index;
            }
            lastRow=index;
            return rows[index][x];
        }
        private void BrushRow(Shape shape,int y,float[] row)
        {
            var box=shape.Box;Array.Clear(brush,box.X,box.Width);double radius=shape.Radius;
            for(int n=0;n<shape.X.Length;n++)
            {
                if((n&31)==0)cancel.ThrowIfCancellationRequested();
                int start=Math.Max(0,n-1);double ax=shape.X[start],ay=shape.Y[start],bx=shape.X[n],by=shape.Y[n];
                if(y<Math.Min(ay,by)-radius||y>Math.Max(ay,by)+radius)continue;
                int l=Math.Max(box.X,(int)Math.Ceiling(Math.Min(ax,bx)-radius)),r=Math.Min(box.X+box.Width-1,(int)Math.Floor(Math.Max(ax,bx)+radius));
                double dx=bx-ax,dy=by-ay,len=dx*dx+dy*dy;
                for(int i=l;i<=r;i++)
                {
                    double t=len==0?0:Math.Max(0,Math.Min(1,((i-ax)*dx+(y-ay)*dy)/len));
                    double u=i-ax-t*dx,v=y-ay-t*dy;
                    double fall=Math.Max(0,Math.Min(1,(1-Math.Sqrt(u*u+v*v)/radius)*4));
                    brush[i]=Math.Max(brush[i],(float)(fall*fall*(3-2*fall)));
                }
            }
            for(int i=box.X;i<box.X+box.Width;i++)row[i]=shape.Erase?row[i]*(1-brush[i]):row[i]+(1-row[i])*brush[i];
        }
        internal bool SamplesProtected(double x,double y)
        {
            int l=(int)x,t=(int)y;bool right=x>l&&l+1<width,down=y>t&&t+1<height;
            return At(l,t)>=.9999||right&&At(l+1,t)>=.9999||down&&At(l,t+1)>=.9999||right&&down&&At(l+1,t+1)>=.9999;
        }
    }
}
