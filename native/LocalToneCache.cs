using System;
using System.Collections.Generic;

namespace AIPhotoStudio
{
    public static partial class LocalEdits
    {
        private static readonly object toneGate=new object();
        private static readonly List<KeyValuePair<double,ushort[]>> localExposures=new List<KeyValuePair<double,ushort[]>>();
        private static readonly List<Tuple<double,double,ImagingRecipe>> localTones=new List<Tuple<double,double,ImagingRecipe>>();
        private static readonly double[] halfCode=ExposureBoundaries();

        // Midpoints between adjacent 16-bit sRGB output codes, expressed in linear light.
        private static double[] ExposureBoundaries()
        {
            var boundaries=new double[65535];
            for(int i=0;i<boundaries.Length;i++)
            {
                double code=(i+.5)/65535;
                boundaries[i]=code<=.04045?code/12.92:Math.Pow((code+.055)/1.055,2.4);
            }
            return boundaries;
        }

        internal static ushort[] ExposureFor(double ev)
        {
            if(ev==0)return null;
            lock(toneGate)
            {
                int index=localExposures.FindIndex(e=>e.Key==ev);
                if(index>=0)return localExposures[index].Value;
            }
            var table=new ushort[65536];
            double gain=Math.Pow(2,ev);
            for(int i=0,code=0;i<table.Length;i++)
            {
                double linear=Math.Min(1,ImagingRecipe.Linear[i]*gain);
                while(code<65535 && linear>halfCode[code])code++;
                // At a rounding boundary use the original double formula (including round-to-even).
                if((code<65535&&Math.Abs(halfCode[code]-linear)<1e-14)||
                    (code>0&&Math.Abs(halfCode[code-1]-linear)<1e-14))
                    table[i]=(ushort)Math.Round(65535*(linear<=.0031308?12.92*linear:1.055*Math.Pow(linear,1/2.4)-.055));
                else table[i]=(ushort)code;
            }
            lock(toneGate)
            {
                localExposures.Insert(0,new KeyValuePair<double,ushort[]>(ev,table));
                if(localExposures.Count>8)localExposures.RemoveAt(8);
            }
            return table;
        }

        private static ImagingRecipe ToneFor(double shadows,double highlights)
        {
            if(shadows==0&&highlights==0)return null;
            lock(toneGate)
            {
                var hit=localTones.Find(entry=>entry.Item1==shadows&&entry.Item2==highlights);
                if(hit!=null)return hit.Item3;
                var tone=new ImagingRecipe(new Dictionary<string,double>{{"shadows",shadows},{"highlights",highlights}},0);
                localTones.Insert(0,Tuple.Create(shadows,highlights,tone));
                if(localTones.Count>4)localTones.RemoveAt(4);
                return tone;
            }
        }
    }
}
