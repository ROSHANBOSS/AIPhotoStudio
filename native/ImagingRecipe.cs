using System;
using System.Collections.Generic;

namespace AIPhotoStudio
{
    internal sealed class ImagingRecipe
    {
        internal static readonly double[] Linear = MakeTransfer(false);
        private static readonly double[] Encoded = MakeTransfer(true);
        private static readonly double[] Centers = { 0, 30, 60, 120, 180, 240, 280, 325, 360 };
        private readonly Dictionary<string, double> values = new Dictionary<string, double>(StringComparer.Ordinal);
        private readonly double[] gains = new double[3];
        private readonly double[,] bands = new double[8, 3];
        private readonly double[,] grades = new double[3, 4];
        private readonly double[,] calibration = new double[3, 3];
        private readonly double[][] curves = new double[4][];
        private readonly double saturation, vibrance, contrast, highlights, shadows, whites, blacks, haze;
        private readonly double[] tone;
        private readonly ImagingCube cube;
        private readonly double cubeStrength;
        private readonly bool hsl, grading, calibrated, primaryAdjust, saturationAdjust, curvesAdjust;
        internal readonly double Noise, ChromaNoise, LumaNoise, Clarity, Sharpness;
        internal bool HasColor { get; private set; }

        internal bool IndependentChannels
        {
            get { return HasColor && tone == null && !saturationAdjust && !hsl && !grading && !calibrated && cube == null; }
        }

        // In this subset each output depends on just one 16-bit input channel.
        // Build the exhaustive transfer with the same rounding as the CPU renderer.
        internal ushort[] ChannelTransfer()
        {
            if (!IndependentChannels) throw new InvalidOperationException("此配方需要跨通道运算。");
            var table = new ushort[65536 * 4];
            for (int i = 0; i < 65536; i++) table[4*i] = table[4*i+1] = table[4*i+2] = (ushort)i;
            Apply(table, 0, table.Length);
            return table;
        }

        internal ImagingRecipe(IDictionary<string, double> requested, double inputExposure, bool beforeHsl=false)
        {
            if (requested != null)
            {
                foreach (var pair in requested)
                {
                    ControlSpec control;
                    if (!Specs.TryGetValue(pair.Key, out control) || double.IsNaN(pair.Value) || double.IsInfinity(pair.Value) ||
                        pair.Value < control.min || pair.Value > control.max)
                        throw new ArgumentException("无效或超出范围的图像参数 " + pair.Key);
                    if (pair.Value != 0) values.Add(pair.Key, pair.Value);
                }
            }
            double ev = Get("exposure") + inputExposure, temp = Pct("temperature"), tint = Pct("tint");
            gains[0] = Math.Pow(2, ev + .85 * temp + .30 * tint);
            gains[1] = Math.Pow(2, ev - .50 * tint);
            gains[2] = Math.Pow(2, ev - .85 * temp + .30 * tint);
            contrast = Math.Pow(2, Pct("contrast") * 1.28);
            highlights = Pct("highlights");
            shadows = Pct("shadows");
            whites = Pct("whites");
            blacks = Pct("blacks");
            haze = Pct("dehaze");
            if (contrast != 1 || highlights != 0 || shadows != 0 || whites != 0 || blacks != 0 || haze != 0)
            {
                tone = new double[32769];
                for (int i = 0; i < tone.Length; i++) tone[i] = ToneAt(i / 32768.0);
            }
            primaryAdjust = tone != null || ev != 0 || temp != 0 || tint != 0;
            saturation = Pct("saturation");
            vibrance = Pct("vibrance");
            saturationAdjust = saturation != 0 || vibrance != 0;
            for (int i = 0; !beforeHsl && i < Imaging.Bands.Length; i++)
            {
                string prefix = "hsl_" + Imaging.Bands[i];
                bands[i, 0] = Get(prefix + "_hue");
                bands[i, 1] = Pct(prefix + "_sat");
                bands[i, 2] = Pct(prefix + "_lum");
                if (bands[i, 0] != 0 || bands[i, 1] != 0 || bands[i, 2] != 0) hsl = true;
            }
            for (int i = 0; !beforeHsl && i < Imaging.Zones.Length; i++)
            {
                string prefix = "grade_" + Imaging.Zones[i];
                double r, g, b;
                HslToRgb(Get(prefix + "_hue"), 1, .5, out r, out g, out b);
                double luma = Luma(r, g, b), amount = Pct(prefix + "_sat");
                grades[i, 0] = (r - luma) * amount;
                grades[i, 1] = (g - luma) * amount;
                grades[i, 2] = (b - luma) * amount;
                grades[i, 3] = Pct(prefix + "_lum") * .19;
                if (amount != 0 || grades[i, 3] != 0) grading = true;
            }
            for (int i = 0; i < Imaging.Primaries.Length; i++)
            {
                string prefix = "cal_" + Imaging.Primaries[i];
                double angle = Get(prefix + "_hue") * Math.PI / 180, stretch = 1 + Pct(prefix + "_sat");
                if (angle == 0 && stretch == 1) continue;
                calibrated = true;
                double r = i == 0 ? 1 : 0, g = i == 1 ? 1 : 0, b = i == 2 ? 1 : 0;
                double y = Luma(r, g, b), u = b - y, v = r - y;
                double u2 = (u * Math.Cos(angle) - v * Math.Sin(angle)) * stretch;
                double v2 = (u * Math.Sin(angle) + v * Math.Cos(angle)) * stretch;
                calibration[i, 0] = v2 - v;
                calibration[i, 2] = u2 - u;
                calibration[i, 1] = -(.2126 * calibration[i, 0] + .0722 * calibration[i, 2]) / .7152;
            }
            for (int i = 0; i < Imaging.Channels.Length; i++)
            {
                string prefix = "curve_" + Imaging.Channels[i] + "_";
                double low = Pct(prefix + "25"), mid = Pct(prefix + "50"), high = Pct(prefix + "75");
                if (low == 0 && mid == 0 && high == 0) continue;
                curves[i] = BuildCurve(low, mid, high);
                curvesAdjust = true;
            }
            if(beforeHsl)
            {
                HasColor=primaryAdjust||saturationAdjust||calibrated||curvesAdjust;
                return;
            }
            Noise = Pct("noise");
            ChromaNoise=Pct("noise_chroma");
            LumaNoise=Pct("noise_luma");
            Clarity = Pct("clarity");
            Sharpness = Pct("sharpness");
            string cubeHash = Imaging.CubeHash(values);
            cubeStrength = Pct("lut_strength");
            if (cubeStrength > 0 && cubeHash != null) cube = Imaging.ResolveCube(cubeHash);
            HasColor = primaryAdjust || saturationAdjust || hsl || grading || calibrated || curvesAdjust || cube != null;
        }

        private static readonly Dictionary<string, ControlSpec> Specs = IndexSpecs();
        private static Dictionary<string, ControlSpec> IndexSpecs()
        {
            var index = new Dictionary<string, ControlSpec>(StringComparer.Ordinal);
            foreach (var spec in Imaging.Controls) index.Add(spec.key, spec);
            return index;
        }

        private double Get(string key) { double value; return values.TryGetValue(key, out value) ? value : 0; }
        private double Pct(string key) { return .01 * Get(key); }
        internal static double Luma(double r, double g, double b) { return .2126 * r + .7152 * g + .0722 * b; }
        internal static double Bound(double v) { return v < 0 ? 0 : v > 1 ? 1 : v; }
        internal static ushort U16(double v) { return (ushort)(Bound(v) * 65535 + .5); }
        internal static double Ramp(double lo, double hi, double value)
        {
            double t = Bound((value - lo) / (hi - lo));
            return t * t * (3 - 2 * t);
        }
        private static double ToLinear(double v) { return v <= .04045 ? v / 12.92 : Math.Pow((v + .055) / 1.055, 2.4); }
        private static double ToEncoded(double v) { return v <= .0031308 ? 12.92 * v : 1.055 * Math.Pow(v, 1 / 2.4) - .055; }
        private static double[] MakeTransfer(bool encode)
        {
            var table = new double[65536];
            for (int i = 0; i < table.Length; i++)
            {
                double value = i / 65535.0;
                table[i] = encode ? ToEncoded(value) : ToLinear(value);
            }
            return table;
        }
        private static double Lookup(double[] table, double v)
        {
            double index = Bound(v) * (table.Length - 1);
            int i = (int)index;
            return i == table.Length - 1 ? table[i] : table[i] + (table[i + 1] - table[i]) * (index - i);
        }
        internal static double EncodeBounded(double linear) { return Lookup(Encoded, linear); }

        private double ToneAt(double y)
        {
            double level = ToEncoded(y);
            double ev = shadows * (1 - Ramp(.08, .68, level)) + highlights * Ramp(.38, .94, level) * 1.12 +
                whites * Ramp(.62, 1.02, level) * .78;
            double shaped = ToEncoded(y * Math.Pow(2, ev));
            shaped = (shaped - .46) * contrast + .46;
            shaped += .16 * blacks * (1 - Ramp(0, .57, shaped));
            shaped = (shaped - .065 * haze) * (1 + .22 * haze);
            return ToLinear(Math.Max(0, shaped));
        }

        private static double[] BuildCurve(double low, double mid, double high)
        {
            double[] points = { 0, .25 + low * .235, .5 + mid * .235, .75 + high * .235, 1 };
            double[] slopes = new double[5];
            slopes[0] = points[1] - points[0];
            slopes[4] = points[4] - points[3];
            for (int i = 1; i < 4; i++)
            {
                double left = points[i] - points[i - 1], right = points[i + 1] - points[i];
                slopes[i] = left * right <= 0 ? 0 : 2 * left * right / (left + right);
            }
            var table = new double[4097];
            for (int i = 0; i < table.Length; i++)
            {
                int j = Math.Min(3, i / 1024);
                double t = i / 1024.0 - j, t2 = t * t, t3 = t2 * t;
                table[i] = Bound((2 * t3 - 3 * t2 + 1) * points[j] + (t3 - 2 * t2 + t) * slopes[j] +
                    (-2 * t3 + 3 * t2) * points[j + 1] + (t3 - t2) * slopes[j + 1]);
            }
            return table;
        }

        private static void HslToRgb(double hue, double sat, double light, out double r, out double g, out double b)
        {
            double h = (hue % 360 + 360) % 360 / 60;
            double c = (1 - Math.Abs(2 * light - 1)) * sat;
            double x = c * (1 - Math.Abs(h % 2 - 1)), m = light - .5 * c;
            switch ((int)h)
            {
                case 0: r = c; g = x; b = 0; break;
                case 1: r = x; g = c; b = 0; break;
                case 2: r = 0; g = c; b = x; break;
                case 3: r = 0; g = x; b = c; break;
                case 4: r = x; g = 0; b = c; break;
                default: r = c; g = 0; b = x; break;
            }
            r += m; g += m; b += m;
        }

        internal struct HueBands
        {
            internal int Left;
            internal double Hue, Light, Saturation, Blend, Influence;
            internal int Right { get { return (Left+1)&7; } }
            internal int Strongest { get { return Blend>.5?Right:Left; } }
            internal double Weight(int band)
            {
                return Influence*(band==Left?1-Blend:band==Right?Blend:0);
            }
        }

        internal static HueBands MeasureHue(double r,double g,double b)
        {
            double min = Math.Min(r, Math.Min(g, b)), max = Math.Max(r, Math.Max(g, b));
            double delta = max - min, light = (max + min) * .5;
            if (delta < .00002) return new HueBands{Hue=-1};
            double sat = delta / (1 - Math.Abs(2 * light - 1));
            double h = 60 * (max == r ? (g - b) / delta : max == g ? 2 + (b - r) / delta : 4 + (r - g) / delta);
            if (h < 0) h += 360;
            int left = 0;
            while (left < 7 && h >= Centers[left + 1]) left++;
            return new HueBands{Hue=h,Light=light,Saturation=sat,Left=left,
                Blend=Ramp(Centers[left], Centers[left+1], h),Influence=Ramp(.01,.12,sat)};
        }

        private void ApplyBands(ref double r, ref double g, ref double b)
        {
            var mix=MeasureHue(r,g,b);
            if(mix.Hue<0)return;
            int left=mix.Left,right=mix.Right;
            double t=mix.Blend,influence=mix.Influence,light=mix.Light,sat=mix.Saturation,h=mix.Hue;
            double hueOffset = bands[left, 0] + (bands[right, 0] - bands[left, 0]) * t;
            double satOffset = bands[left, 1] + (bands[right, 1] - bands[left, 1]) * t;
            double lumOffset = bands[left, 2] + (bands[right, 2] - bands[left, 2]) * t;
            if (hueOffset == 0 && satOffset == 0 && lumOffset == 0) return;
            light += lumOffset * influence * (lumOffset > 0 ? 1 - light : light) * .48;
            sat = Bound(sat + satOffset * influence * (satOffset > 0 ? 1 - sat : sat));
            HslToRgb(h + hueOffset * influence, sat, light, out r, out g, out b);
        }

        internal void Apply(ushort[] channels, int begin, int end)
        {
            if (!HasColor) return;
            for (int p = begin; p < end; p += 4)
            {
                double r, g, b;
                if (primaryAdjust)
                {
                    r = Linear[channels[p]] * gains[0];
                    g = Linear[channels[p + 1]] * gains[1];
                    b = Linear[channels[p + 2]] * gains[2];
                    if (tone != null)
                    {
                        double y = Luma(r, g, b), target = y <= 1 ? Lookup(tone, y) : ToneAt(y);
                        double scale = (target + .002) / (y + .002);
                        double bias = target - scale * y;
                        r = r * scale + bias; g = g * scale + bias; b = b * scale + bias;
                    }
                    r = EncodeBounded(r); g = EncodeBounded(g); b = EncodeBounded(b);
                }
                else
                {
                    r = channels[p] / 65535.0; g = channels[p + 1] / 65535.0; b = channels[p + 2] / 65535.0;
                }
                if (calibrated)
                {
                    double y = Luma(r, g, b), dr = r - y, dg = g - y, db = b - y;
                    r = Bound(r + dr * calibration[0, 0] + dg * calibration[1, 0] + db * calibration[2, 0]);
                    g = Bound(g + dr * calibration[0, 1] + dg * calibration[1, 1] + db * calibration[2, 1]);
                    b = Bound(b + dr * calibration[0, 2] + dg * calibration[1, 2] + db * calibration[2, 2]);
                }
                if (curvesAdjust)
                {
                    if (curves[0] != null) { r = Lookup(curves[0], r); g = Lookup(curves[0], g); b = Lookup(curves[0], b); }
                    if (curves[1] != null) r = Lookup(curves[1], r);
                    if (curves[2] != null) g = Lookup(curves[2], g);
                    if (curves[3] != null) b = Lookup(curves[3], b);
                }
                if (saturationAdjust)
                {
                    double y = Luma(r, g, b);
                    double chroma = Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b));
                    double amount = (1 + saturation) * (1 + vibrance * (1 - chroma) * .88);
                    r = Bound(y + (r - y) * amount); g = Bound(y + (g - y) * amount); b = Bound(y + (b - y) * amount);
                }
                if (hsl) ApplyBands(ref r, ref g, ref b);
                if (grading)
                {
                    double y = Luma(r, g, b);
                    double low = 1 - Ramp(.10, .57, y), high = Ramp(.45, .94, y), mid = 1 - low - high;
                    double envelope = .07 + .86 * y * (1 - y);
                    double shift = (low * grades[0, 3] + mid * grades[1, 3] + high * grades[2, 3]) * (.35 + 2.6 * y * (1 - y));
                    r += shift + envelope * (low * grades[0, 0] + mid * grades[1, 0] + high * grades[2, 0]);
                    g += shift + envelope * (low * grades[0, 1] + mid * grades[1, 1] + high * grades[2, 1]);
                    b += shift + envelope * (low * grades[0, 2] + mid * grades[1, 2] + high * grades[2, 2]);
                }
                if (cube != null) cube.Apply(ref r, ref g, ref b, cubeStrength);
                channels[p] = U16(r); channels[p + 1] = U16(g); channels[p + 2] = U16(b);
            }
        }
    }
}
