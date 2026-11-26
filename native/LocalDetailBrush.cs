using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace AIPhotoStudio
{
    public static partial class LocalEdits
    {
        // The ordinary tone-mask grid is intentionally coarse. On an eye a single
        // interpolated cell can cross a thin frame or refill part of an erased iris.
        // Evaluate the same feathered strokes at pixel centers for the two detail brushes.
        private sealed class DetailBrush
        {
            private sealed class Segment
            {
                internal Stroke Stroke;
                internal EdgePlane Edge;
                internal int Width,Height;
                internal double X,Y,Dx,Dy,LengthSquared,Radius,Left,Top,Right,Bottom;
                internal Segment(Stroke stroke,Mark a,Mark b,int width,int height)
                {
                    Stroke=stroke;
                    var guided=stroke as EdgeStroke;Edge=guided==null?null:guided.Plane;Width=width;Height=height;
                    X=a.x*(width-1); Y=a.y*(height-1);
                    Dx=b.x*(width-1)-X; Dy=b.y*(height-1)-Y;
                    LengthSquared=Dx*Dx+Dy*Dy; Radius=stroke.radius*Math.Min(width,height);
                    Left=Math.Min(X,X+Dx)-Radius;Right=Math.Max(X,X+Dx)+Radius;
                    Top=Math.Min(Y,Y+Dy)-Radius;Bottom=Math.Max(Y,Y+Dy)+Radius;
                }
                internal double Weight(int x,int y)
                {
                    if(x<Left||x>Right||y<Top||y>Bottom)return 0;
                    if(Edge!=null&&!Edge.Contains((x+.5)/Width,(y+.5)/Height))return 0;
                    double px=x-X,py=y-Y;
                    double t=LengthSquared==0?0:Clamp((px*Dx+py*Dy)/LengthSquared,0,1);
                    double dx=px-t*Dx,dy=py-t*Dy;
                    double f=Clamp(3*(1-Math.Sqrt(dx*dx+dy*dy)/Radius),0,1);
                    return f*f*(3-2*f)*Stroke.strength;
                }
            }
            private readonly int width,height,columns,rows;
            private readonly List<Segment>[] tiles;
            internal DetailBrush(Stroke[] strokes,int width,int height,CancellationToken cancel)
            {
                this.width=width;this.height=height;
                int count=strokes.Sum(s=>s.points.Length);
                // Bound index size for a complex mask whose strokes all span the image.
                int side=Math.Min(40,Math.Max(1,(int)Math.Sqrt(240000.0/Math.Max(1,count))));
                columns=Math.Max(1,(int)Math.Ceiling((double)side*width/Math.Max(width,height)));
                rows=Math.Max(1,(int)Math.Ceiling((double)side*height/Math.Max(width,height)));
                tiles=new List<Segment>[columns*rows];
                foreach(var stroke in strokes)
                {
                    cancel.ThrowIfCancellationRequested();
                    if(stroke.strength==0)continue;
                    for(int k=0;k<Math.Max(1,stroke.points.Length-1);k++)
                    {
                        var s=new Segment(stroke,stroke.points[k],stroke.points[Math.Min(k+1,stroke.points.Length-1)],width,height);
                        int right=Column(s.Right),bottom=Row(s.Bottom);
                        for(int row=Row(s.Top);row<=bottom;row++)for(int col=Column(s.Left);col<=right;col++)
                        {
                            int index=row*columns+col;
                            if(tiles[index]==null)tiles[index]=new List<Segment>();
                            tiles[index].Add(s);
                        }
                    }
                }
            }
            private int Column(double x){return (int)Clamp(Math.Floor(x*columns/width),0,columns-1);}
            private int Row(double y){return (int)Clamp(Math.Floor(y*rows/height),0,rows-1);}
            private static double Blend(double mask,Stroke stroke,double weight)
            {
                return stroke.kind=="maskErase"?mask*(1-weight):mask+(1-mask)*weight;
            }
            internal double At(int x,int y,double initial=0)
            {
                var candidates=tiles[Row(y)*columns+Column(x)];
                if(candidates==null)return initial;
                Stroke previous=candidates[0].Stroke;
                double result=initial,weight=0;
                foreach(var s in candidates)
                {
                    if(s.Stroke!=previous){result=Blend(result,previous,weight);previous=s.Stroke;weight=0;}
                    if(weight<s.Stroke.strength)weight=Math.Max(weight,s.Weight(x,y));
                }
                return Blend(result,previous,weight);
            }
        }
    }
}
