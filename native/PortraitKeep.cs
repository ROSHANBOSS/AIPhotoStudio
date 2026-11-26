using System;
using System.Collections.Generic;
using System.Linq;

namespace AIPhotoStudio
{
    internal static class PortraitKeep
    {
        internal const int Limit=96;
        internal static bool Safe(PortraitScene scene,PortraitSpot mark)
        {
            if(mark==null||mark.face<0||mark.face>=scene.Faces.Length||!(mark.x>=0&&mark.x<=1&&mark.y>=0&&mark.y<=1&&mark.radius>0))return false;
            var face=scene.Faces[mark.face];
            double aspect=face.Aspect,r=mark.radius*Math.Min(1,aspect);
            double faceSize=Math.Min(face.Box.w*aspect,face.Box.h);
            if(r<.008*faceSize||r>.13*faceSize||face.Regions.Value.Outline.At(mark.x,mark.y)<.70)return false;
            foreach(var other in scene.Faces.Where(f=>f!=face))
            {
                var b=other.Box;
                if(mark.x+r/aspect<b.x||mark.x-r/aspect>b.x+b.w||mark.y+r<b.y||mark.y-r>b.y+b.h)continue;
                // The box is conservative for overlapping people. An uncertain mark stays editable in the draft.
                return false;
            }
            return true;
        }
        internal static bool Overlaps(PortraitScene scene,PortraitSpot repair)
        {
            double aspect=scene.Faces[repair.face].Aspect;
            foreach(var mark in scene.KeepMarks){
                if(mark.face!=repair.face)continue;
                double dx=(mark.x-repair.x)*aspect,dy=mark.y-repair.y,r=(mark.radius+repair.radius)*Math.Min(aspect,1);
                if(dx*dx+dy*dy<r*r)return true;
            }
            return false;
        }

        // Bucket the saved circles so skin rendering normally inspects none, even on a large portrait.
        internal sealed class Field
        {
            private const int Side=32;
            private readonly List<PortraitSpot>[] cells=new List<PortraitSpot>[Side*Side];
            private readonly double aspect,shortEdge;
            internal Field(PortraitSpot[] marks,double aspect)
            {
                this.aspect=aspect;shortEdge=Math.Min(aspect,1);
                foreach(var p in marks){
                    double r=p.radius*shortEdge;
                    int right=Cell(p.x+r/aspect),bottom=Cell(p.y+r);
                    for(int row=Cell(p.y-r);row<=bottom;row++)for(int col=Cell(p.x-r/aspect);col<=right;col++){
                        int index=row*Side+col;
                        if(cells[index]==null)cells[index]=new List<PortraitSpot>();
                        cells[index].Add(p);
                    }
                }
            }
            private static int Cell(double t){return Math.Max(0,Math.Min(Side-1,(int)Math.Floor(t*Side)));}
            internal double SkinWeight(double x,double y)
            {
                var candidates=cells[Cell(y)*Side+Cell(x)];if(candidates==null)return 1;
                double weight=1;
                foreach(var p in candidates){
                    double dx=(x-p.x)*aspect,dy=y-p.y,r=p.radius*shortEdge;
                    double distance=(dx*dx+dy*dy)/(r*r);
                    if(distance<=.64)return 0;
                    if(distance<1)weight=Math.Min(weight,PortraitRegion.Ease((Math.Sqrt(distance)-.8)*5));
                }
                return weight;
            }
        }
    }
}
