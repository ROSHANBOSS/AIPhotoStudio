using System;
using System.Collections.Generic;
using System.IO;

namespace AIPhotoStudio
{
    public static partial class LocalEdits
    {
        // x,y are a source-normalized start or centre. dx,dy are a line vector
        // or positive ellipse radii. Distances along a line use image pixels.
        internal sealed class GradientGeometry
        {
            public double x {get;set;}
            public double y {get;set;}
            public double dx {get;set;}
            public double dy {get;set;}
            public double feather {get;set;}
            public bool invert {get;set;}

            internal void Check(string kind)
            {
                bool common=x>=0&&x<=1&&y>=0&&y<=1&&feather>=0&&feather<=1;
                bool axes=kind=="radial"
                    ?dx>=.002&&dx<=1&&dy>=.002&&dy<=1
                    :kind=="linear"&&x+dx>=-1e-9&&x+dx<=1+1e-9&&y+dy>=-1e-9&&y+dy<=1+1e-9&&dx*dx+dy*dy>=.003*.003;
                if(!common||!axes)throw new InvalidDataException("渐变的控制点、半径或羽化范围无效。");
            }
        }

        internal static bool GradientKind(string kind) {return kind=="linear"||kind=="radial";}

        internal static GradientGeometry ReadGradient(string kind,object raw)
        {
            var fields=raw as IDictionary<string,object>;
            if(fields==null||fields.Count!=6||!fields.ContainsKey("invert")||!(fields["invert"] is bool))
                throw new InvalidDataException("渐变需要完整的几何参数。");
            foreach(string key in new[]{"x","y","dx","dy","feather"})
            {
                object value;
                if(!fields.TryGetValue(key,out value)||!(value is int||value is long||value is double||value is decimal))
                    throw new InvalidDataException("渐变坐标和羽化范围必须是数字。");
            }
            var shape=MaskJson().ConvertToType<GradientGeometry>(raw);
            shape.Check(kind);
            return shape;
        }

        private sealed class GradientField
        {
            private readonly bool radial,invert;
            private readonly double ox,oy,kx,ky,feather;

            internal GradientField(string kind,GradientGeometry shape,int width,int height)
            {
                radial=kind=="radial";invert=shape.invert;feather=shape.feather;
                ox=shape.x*width-.5;oy=shape.y*height-.5;
                double vx=shape.dx*width,vy=shape.dy*height;
                if(radial){kx=1/vx;ky=1/vy;}
                else {double length=vx*vx+vy*vy;kx=vx/length;ky=vy/length;}
            }

            internal double At(double px,double py)
            {
                double u=(px-ox)*kx,v=(py-oy)*ky;
                double distance=radial?Math.Sqrt(u*u+v*v):u+v;
                double edge=radial?1:.5+.5*feather;
                double weight=feather==0?(distance<=edge?1:0):Clamp((edge-distance)/feather,0,1);
                return invert?1-weight:weight;
            }
        }

        internal static bool RequiresVersion7(IDictionary<string,double> values)
        {
            return Hash(values)!=null&&ReadOnlySet(values).HasGradients;
        }
    }
}
