using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AIPhotoStudio
{
    public static class BodyEdits
    {
        public const int MaxPeople = 16;
        internal static readonly string[] Keys = { "body_shoulders", "body_arms", "body_waist", "body_abdomen", "body_hips",
            "body_thighs", "body_calves", "body_legshape", "body_leglength", "body_proportion" };
        private static readonly string[] Labels = { "收肩", "瘦手臂", "收腰", "收腹", "收臀", "瘦大腿", "瘦小腿", "调整腿型", "拉长双腿", "提高腰线" };
        private static readonly string[,] PersonKeys = new string[MaxPeople, Keys.Length];
        private static readonly Dictionary<string, int> Indices = new Dictionary<string, int>(StringComparer.Ordinal);
        public static readonly ControlSpec[] Controls = MakeControls();

        public static string KeyFor(int index, string baseKey)
        {
            if (index < 0 || index >= MaxPeople) throw new ArgumentOutOfRangeException("index");
            int region;
            if (baseKey == null || !Indices.TryGetValue(baseKey, out region) || region >= Keys.Length)
                throw new ArgumentException("无效的人体美型部位。", "baseKey");
            return PersonKeys[index, region];
        }

        public static int PersonIndex(string actualKey)
        {
            int found;
            return actualKey != null && Indices.TryGetValue(actualKey, out found) ? found / Keys.Length : -1;
        }

        internal static int RegionIndex(string actualKey)
        {
            int found;
            return actualKey != null && Indices.TryGetValue(actualKey, out found) ? found % Keys.Length : -1;
        }

        public static string BaseKey(string actualKey)
        {
            int region = RegionIndex(actualKey);
            return region >= 0 ? Keys[region] : null;
        }

        internal static bool Active(IDictionary<string, double> values)
        {
            if (values == null) return false;
            foreach (var pair in values)
                if (pair.Value != 0 && Indices.ContainsKey(pair.Key)) return true;
            return false;
        }

        private static ControlSpec[] MakeControls()
        {
            var controls = new List<ControlSpec>();
            for (int i = 0; i < 5; i++) controls.Add(new ControlSpec { key = "body_ref" + i, label = "人体资源 " + (i + 1),
                group = "人体资源", min = 0, max = i == 4 ? 281474976710655L : 4503599627370495L, step = 1, initial = 0 });
            for (int person = 0; person < MaxPeople; person++)
                for (int i = 0; i < Keys.Length; i++)
                {
                    string key = person == 0 ? Keys[i] : "body_p" + (person + 1).ToString(CultureInfo.InvariantCulture) + "_" + Keys[i].Substring(5);
                    PersonKeys[person, i] = key;
                    Indices.Add(key, person * Keys.Length + i);
                    controls.Add(new ControlSpec { key = key, label = person == 0 ? Labels[i] : "人物 " + (person + 1) + " · " + Labels[i],
                        group = "人体美型", min = -100, max = 100, step = 1, initial = 0 });
                }
            return controls.ToArray();
        }

        private struct V
        {
            internal double X, Y;
            internal V(double x, double y) { X = x; Y = y; }
            public static V operator +(V a, V b) { return new V(a.X + b.X, a.Y + b.Y); }
            public static V operator -(V a, V b) { return new V(a.X - b.X, a.Y - b.Y); }
            public static V operator *(V a, double s) { return new V(a.X * s, a.Y * s); }
            internal double Length { get { return Math.Sqrt(X * X + Y * Y); } }
            internal V Unit { get { double size = Length; return size < .000001 ? new V(0, 0) : this * (1 / size); } }
            internal V Normal { get { return new V(-Y, X); } }
            internal double Dot(V b) { return X * b.X + Y * b.Y; }
        }

        // Each local basis has continuous first derivatives at its compact support boundary.
        private sealed class Influence
        {
            internal V Centre, Along, Normal, Translate;
            internal double Radius, Length, Squeeze;
            internal V Delta(V position)
            {
                V offset = position - Centre;
                double lateral = offset.Dot(Normal), longitudinal = offset.Dot(Along);
                double r = lateral * lateral / (Radius * Radius) + longitudinal * longitudinal / (Length * Length);
                if (r >= 1) return new V();
                double falloff = (1 - r) * (1 - r);
                return (Normal * (Squeeze * lateral) + Translate) * falloff;
            }
        }

        private static double Smooth(double n)
        {
            n = Math.Max(0, Math.Min(1, n));
            return n * n * (3 - 2 * n);
        }

        private sealed class Anatomy
        {
            private readonly int width, height;
            private readonly BodyGeometry pose;
            private readonly byte[] mask;
            private readonly V[] p;
            private readonly double[] amount;
            private readonly List<Influence> fields = new List<Influence>();

            internal Anatomy(BodyGeometry pose, int width, int height, double[] amount)
            {
                this.width = width; this.height = height; this.pose = pose; this.mask = pose.Mask; this.amount = amount;
                p = pose.Points.Select(point => new V(point.X * width - .5, point.Y * height - .5)).ToArray();
            }

            private double Extent(V centre, V side, double limit)
            {
                double stride = Math.Max(1, limit / 20);
                for (double d = stride; d <= limit; d += stride)
                {
                    V sample = centre + side * d;
                    double x = (sample.X + .5) / width, y = (sample.Y + .5) / height;
                    if (BodyAssets.Probability(mask, x, y) < .46 || OccupiedByOther(pose, x, y)) return d;
                }
                return limit;
            }

            private void Add(int index, V centre, V axis, double radius, double length, double pinch, V move)
            {
                if (amount[index] == 0 || radius < 1 || length < 1 || axis.Length < .001) return;
                V along = axis.Unit;
                fields.Add(new Influence { Centre = centre, Along = along, Normal = along.Normal, Radius = radius,
                    Length = length, Squeeze = amount[index] * pinch, Translate = move * amount[index] });
            }

            private void WidthChange(int index, V centre, V axis, double estimate, double cap, double length, double pinch)
            {
                if (amount[index] == 0) return;
                V side = axis.Unit.Normal;
                double across = (Extent(centre, side, cap) + Extent(centre, side * -1, cap)) * .5;
                double radius = Math.Max(estimate * .54, Math.Min(cap, across)) * 1.7;
                Add(index, centre, axis, radius, length, pinch, new V());
            }

            private void Limb(int index, int a, int b, double approximateRadius, double cap, double pinch)
            {
                if (!pose.Legacy && !pose.HasBone(a, b)) return;
                V bone = p[b] - p[a];
                WidthChange(index, p[a] + bone * .53, bone, approximateRadius, cap, bone.Length * .72, pinch);
            }

            internal List<Influence> Build()
            {
                V shoulder = (p[11] + p[12]) * .5, hip = (p[23] + p[24]) * .5;
                V spine = hip - shoulder, down = spine.Unit;
                double trunk = spine.Length, breadth = (p[11] - p[12]).Length;
                double pelvis = (p[23] - p[24]).Length;
                if (pose.Torso)
                    WidthChange(0, shoulder + spine * .05, spine, breadth * .52, breadth * .73, trunk * .45, .46);
                else
                    WidthChange(0, shoulder, (p[11] - p[12]).Normal, breadth * .52, breadth * .73, breadth * .39, .46);
                WidthChange(2, shoulder + spine * .54, spine, pelvis * .55, breadth * .68, trunk * .43, .53);
                WidthChange(3, shoulder + spine * .81, spine, pelvis * .6, breadth * .72, trunk * .35, .48);
                WidthChange(4, hip + spine * .07, spine, pelvis * .67, breadth * .8, trunk * .45, .47);
                for (int side = 0; side < 2; side++)
                {
                    Limb(1, 11 + side, 13 + side, breadth * .14, breadth * .25, .54);
                    Limb(1, 13 + side, 15 + side, breadth * .105, breadth * .18, .41);
                    Limb(5, 23 + side, 25 + side, pelvis * .26, breadth * .32, .55);
                    Limb(6, 25 + side, 27 + side, pelvis * .17, breadth * .22, .56);
                    if (!pose.Legacy && !(pose.HasBone(23 + side, 25 + side) && pose.HasBone(25 + side, 27 + side))) continue;
                    V h = p[23 + side], knee = p[25 + side], ankle = p[27 + side];
                    V leg = ankle - h, axis = leg.Unit;
                    V aligned = h + axis * (knee - h).Dot(axis);
                    V correction = knee - aligned;
                    double distance = correction.Length, limit = leg.Length * .065;
                    if (distance > limit) correction = correction * (limit / distance);
                    Add(7, knee, leg, breadth * .42, leg.Length * .40, 0, correction * .72);
                }
                V feet = (p[27] + p[28]) * .5, legAxis = feet - hip;
                Add(8, hip, legAxis, Math.Max(breadth, pelvis) * 1.05, legAxis.Length * .96,
                    0, legAxis.Unit * (legAxis.Length * .075));
                Add(9, shoulder + spine * .66, spine, breadth * .90, trunk * .74, 0, down * (trunk * .09));
                return fields;
            }
        }

        private sealed class Field
        {
            internal readonly BodyGeometry Pose;
            internal int Nx, Ny;
            internal double Sx, Sy, ReachY;
            internal V[] Deltas;
            internal Field(int width, int height, BodyGeometry pose, List<Influence> bases, CancellationToken cancel)
            {
                Pose = pose;
                int longest = Math.Max(width, height);
                Nx = Math.Max(2, (int)Math.Ceiling(320.0 * width / longest));
                Ny = Math.Max(2, (int)Math.Ceiling(320.0 * height / longest));
                Sx = (double)(width - 1) / (Nx - 1); Sy = (double)(height - 1) / (Ny - 1);
                Deltas = new V[Nx * Ny];
                double limit = Math.Min(width, height) * .05;
                for (int y = 0; y < Ny; y++)
                {
                    cancel.ThrowIfCancellationRequested();
                    for (int x = 0; x < Nx; x++)
                    {
                        V q = new V(x * Sx, y * Sy);
                        double probability = BodyAssets.Probability(pose.Mask, (q.X + .5) / width, (q.Y + .5) / height);
                        if (probability <= .042) continue;
                        if (pose.OtherPeople != null)
                        {
                            double other = BodyAssets.Probability(pose.OtherPeople, (q.X + .5) / width, (q.Y + .5) / height);
                            probability *= 1 - Smooth(other / .042);
                            if (probability <= .042) continue;
                        }
                        V shift = new V();
                        foreach (Influence field in bases) shift = shift + field.Delta(q);
                        double movement = shift.Length;
                        if (movement > limit) shift = shift * (limit / movement);
                        shift = shift * Smooth((probability - .042) / .66);
                        Deltas[y * Nx + x] = shift;
                    }
                }
                // Bound both gradient columns for each bilinear cell. The inverse map cannot fold the image.
                double gx = 0, gy = 0;
                for (int y = 0; y < Ny; y++)
                    for (int x = 0; x < Nx; x++)
                    {
                        int i = y * Nx + x;
                        if (x + 1 < Nx) gx = Math.Max(gx, (Deltas[i + 1] - Deltas[i]).Length / Sx);
                        if (y + 1 < Ny) gy = Math.Max(gy, (Deltas[i + Nx] - Deltas[i]).Length / Sy);
                    }
                double bound = Math.Sqrt(gx * gx + gy * gy), scale = bound > .78 ? .78 / bound : 1;
                for (int i = 0; i < Deltas.Length; i++)
                {
                    Deltas[i] = Deltas[i] * scale;
                    ReachY = Math.Max(ReachY, Math.Abs(Deltas[i].Y));
                }
            }
        }

        public static BitmapSource Apply(BitmapSource oriented, IDictionary<string, double> values, CancellationToken cancel,LocalEdits.Stroke[] draftMarks=null)
        {
            var masks=Active(values)?LocalEdits.ViewportMask(values):null;
            return Apply(oriented,values,cancel,draftMarks,masks==null?null:masks.areas);
        }
        internal static BitmapSource Apply(BitmapSource oriented, IDictionary<string, double> values, CancellationToken cancel,LocalEdits.Stroke[] draftMarks,LocalEdits.AreaMark[] areas)
        {
            if (oriented == null) throw new ArgumentNullException("oriented");
            if (values == null || oriented.PixelWidth < 3 || oriented.PixelHeight < 3) return oriented;
            if (!Active(values)) return oriented;
            BodyScene scene = BodyAssets.ResolveScene(values);
            if (scene == null) throw new InvalidOperationException("请先识别这张照片中的人物。");
            BodyAssets.RequireAvailable(scene, values);
            int width = oriented.PixelWidth, height = oriented.PixelHeight;
            var fields = new List<Field>();
            for (int person = 0; person < scene.Subjects.Length; person++)
            {
                var amounts = new double[Keys.Length];
                for (int i = 0; i < Keys.Length; i++)
                {
                    double value;
                    if (values.TryGetValue(PersonKeys[person, i], out value)) amounts[i] = Math.Max(-1, Math.Min(1, value / 100));
                }
                if (amounts.All(value => value == 0)) continue;
                BodyGeometry pose = scene.Subjects[person];
                fields.Add(new Field(width, height, pose, new Anatomy(pose, width, height, amounts).Build(), cancel));
            }
            var marks = draftMarks??LocalEdits.Read(values);
            Func<int, int, double> protect = marks != null && marks.Any(mark => mark.kind == "protect") ?
                LocalEdits.RepairCoverage(width, height, marks, true, cancel) : null;
            BitmapSource source = oriented;
            if (source.Format != PixelFormats.Rgba64)
                source = new FormatConvertedBitmap(source, PixelFormats.Rgba64, null, 0);
            var guard=areas!=null&&areas.Any(m=>m.kind!="redactRect")?new BodyGuard(areas,width,height,protect,cancel):null;
            return Resample(source, fields, protect, guard, cancel);
        }

        private static bool OccupiedByOther(BodyGeometry pose, double x, double y)
        {
            return pose.OtherPeople != null && BodyAssets.Probability(pose.OtherPeople, x, y) >= .042;
        }

        private static bool OtherSample(BodyGeometry pose, double x, double y, int width, int height)
        {
            if (pose.OtherPeople == null) return false;
            int left = (int)x, top = (int)y;
            double u = (left + .5) / width, v = (top + .5) / height;
            if (OccupiedByOther(pose, u, v)) return true;
            bool right = x > left && left + 1 < width, down = y > top && top + 1 < height;
            return (right && OccupiedByOther(pose, u + 1.0 / width, v)) ||
                (down && OccupiedByOther(pose, u, v + 1.0 / height)) ||
                (right && down && OccupiedByOther(pose, u + 1.0 / width, v + 1.0 / height));
        }

        private static double Luma(ushort[] rows, int i)
        {
            return (rows[i] * .2126 + rows[i + 1] * .7152 + rows[i + 2] * .0722) / 65535;
        }

        private static BitmapSource Resample(BitmapSource source, List<Field> fields, Func<int, int, double> protect, BodyGuard guard,
            CancellationToken cancel)
        {
            int w = source.PixelWidth, h = source.PixelHeight, stride = checked(w * 8);
            var output = new WriteableBitmap(w, h, source.DpiX > 0 ? source.DpiX : 96, source.DpiY > 0 ? source.DpiY : 96,
                PixelFormats.Rgba64, null);
            var gx = new int[w]; var fx = new double[w];
            Field layout = fields[0];
            for (int x = 0; x < w; x++)
            {
                double at = x / layout.Sx; gx[x] = Math.Min(layout.Nx - 2, (int)at); fx[x] = at - gx[x];
            }
            int halo = (int)Math.Ceiling(fields.Max(field => field.ReachY)) + 2;
            for (int top = 0; top < h; top += 80)
            {
                cancel.ThrowIfCancellationRequested();
                int first = Math.Max(0, top - halo), count = Math.Min(80, h - top), end = Math.Min(h, top + count + halo);
                var input = new ushort[checked((end - first) * w * 4)];
                source.CopyPixels(new Int32Rect(0, first, w, end - first), input, stride, 0);
                var rows = new ushort[checked(count * w * 4)];
                Array.Copy(input, (top - first) * w * 4, rows, 0, rows.Length);
                // Every subject samples the same original tile. Disjoint edits never cause a
                // second interpolation, and overlap is protected even if both people are edited.
                foreach (Field field in fields)
                {
                    BodyGeometry pose = field.Pose;
                    int left = Math.Max(0, (int)(pose.Left * w)), right = Math.Min(w, (int)Math.Ceiling(pose.Right * w));
                    int y0 = Math.Max(top, (int)(pose.Top * h)), y1 = Math.Min(top + count, (int)Math.Ceiling(pose.Bottom * h));
                    for (int y = y0; y < y1; y++)
                    {
                        if ((y & 7) == 0) cancel.ThrowIfCancellationRequested();
                        double at = y / field.Sy; int gy = Math.Min(field.Ny - 2, (int)at);
                        double fy = at - gy;
                        for (int x = left; x < right; x++)
                        {
                            int grid = gy * field.Nx + gx[x];
                            V upper = field.Deltas[grid] * (1 - fx[x]) + field.Deltas[grid + 1] * fx[x];
                            V lower = field.Deltas[grid + field.Nx] * (1 - fx[x]) + field.Deltas[grid + field.Nx + 1] * fx[x];
                            V shift = upper * (1 - fy) + lower * fy;
                            if (Math.Abs(shift.X) + Math.Abs(shift.Y) < .012) continue;
                            double probability = BodyAssets.Probability(pose.Mask, (x + .5) / w, (y + .5) / h);
                            if (probability <= .042 || OccupiedByOther(pose, (x + .5) / w, (y + .5) / h)) continue;
                            double weight = guard!=null?1-guard.At(x,y):protect==null?1:1-Math.Max(0,Math.Min(1,protect(x,y)));
                            if (probability < .65 && x > 0 && x + 1 < w && y > 0 && y + 1 < h)
                            {
                                int i = ((y - first) * w + x) * 4;
                                double edge = Math.Max(Math.Abs(Luma(input, i + 4) - Luma(input, i - 4)),
                                    Math.Abs(Luma(input, i + w * 4) - Luma(input, i - w * 4)));
                                weight *= 1 - Smooth((edge - .095) / .20) * Smooth((.65 - probability) / .40);
                            }
                            if (weight <= 0) continue;
                            double sx = Math.Max(0, Math.Min(w - 1, x + shift.X * weight));
                            double sy = Math.Max(first, Math.Min(end - 1, y + shift.Y * weight));
                            if (OtherSample(pose, sx, sy, w, h)||guard!=null&&guard.SamplesProtected(sx,sy)) continue;
                            Bilinear(input, w, sx, sy - first, end - first, rows, ((y - top) * w + x) * 4);
                        }
                    }
                }
                output.WritePixels(new Int32Rect(0, top, w, count), rows, stride, 0);
            }
            output.Freeze();
            return output;
        }

        private static void Bilinear(ushort[] input, int w, double x, double y, int rowCount, ushort[] target, int dest)
        {
            int x0 = (int)x, y0 = (int)y, x1 = Math.Min(x0 + 1, w - 1), y1 = Math.Min(y0 + 1, rowCount - 1);
            double dx = x - x0, dy = y - y0, a = (1 - dx) * (1 - dy), b = dx * (1 - dy), c = (1 - dx) * dy, d = dx * dy;
            int i = (y0 * w + x0) * 4, j = (y0 * w + x1) * 4, k = (y1 * w + x0) * 4, l = (y1 * w + x1) * 4;
            double alpha = input[i + 3] * a + input[j + 3] * b + input[k + 3] * c + input[l + 3] * d;
            target[dest + 3] = (ushort)Math.Max(0, Math.Min(65535, alpha + .5));
            if (input[i + 3] != input[j + 3] || input[i + 3] != input[k + 3] || input[i + 3] != input[l + 3])
            {
                if (alpha <= .000001) return;
                a *= input[i + 3] / alpha; b *= input[j + 3] / alpha; c *= input[k + 3] / alpha; d *= input[l + 3] / alpha;
            }
            for (int channel = 0; channel < 3; channel++)
                target[dest + channel] = (ushort)Math.Max(0, Math.Min(65535, input[i + channel] * a +
                    input[j + channel] * b + input[k + channel] * c + input[l + channel] * d + .5));
        }
    }
}
