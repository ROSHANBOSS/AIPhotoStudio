using System;

namespace AIPhotoStudio
{
    // Only used at selection time. The resulting feathered division is stored with the person's guard.
    internal sealed class GarmentDivision
    {
        private readonly double cx,cy,dx,dy,width;
        private readonly bool upper;
        internal string Name { get { return upper?"上装":"下装"; } }
        private GarmentDivision(bool upper,double x,double y,double dx,double dy,double width)
        {
            this.upper=upper;cx=x;cy=y;this.dx=dx;this.dy=dy;this.width=width;
        }
        internal static GarmentDivision From(BodyGeometry person,string part,double? manual)
        {
            if(part!="upper"&&part!="lower")throw new ArgumentException("请选择上装或下装。");
            if(person==null||person.Box==null)throw new InvalidOperationException("请先识别并选中要修衣服的人物。");
            var box=person.Box;
            if(manual.HasValue)
            {
                double fraction=manual.Value;
                if(!(fraction>=.05&&fraction<=.95))throw new ArgumentException("衣物分界需要位于人物高度的 5% 至 95%。");
                return new GarmentDivision(part=="upper",0,box.y+box.h*fraction,0,1,Math.Max(.006,box.h*.018));
            }
            if(!person.Visible[11]||!person.Visible[12]||!person.Visible[23]||!person.Visible[24])
                throw new InvalidOperationException("当前腰线不清楚。取消「按髋部定位」后可手动指定分界。");
            var points=person.Points;
            double x=(points[23].X+points[24].X)*.5,y=(points[23].Y+points[24].Y)*.5;
            double vx=x-(points[11].X+points[12].X)*.5,vy=y-(points[11].Y+points[12].Y)*.5;
            // The normal to the line of the two hips follows the subject, including a tilted pose.
            double nx=points[23].Y-points[24].Y,ny=points[24].X-points[23].X;
            double length=Math.Sqrt(nx*nx+ny*ny);
            if(length<.008||Math.Abs(vx*nx+vy*ny)/length<.025)throw new InvalidOperationException("髋部锚点不稳定，请手动指定衣物分界。");
            if(vx*nx+vy*ny<0)length=-length;
            return new GarmentDivision(part=="upper",x,y,nx/length,ny/length,Math.Max(.006,Math.Abs((vx*nx+vy*ny)/length)*.045));
        }
        internal double At(double x,double y)
        {
            double position=((x-cx)*dx+(y-cy)*dy)/width;
            double lower=ImagingRecipe.Ramp(-1,1,position);
            return upper?1-lower:lower;
        }
    }
}
