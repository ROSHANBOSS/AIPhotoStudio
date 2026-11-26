using System;
using System.IO;
using System.Linq;

namespace AIPhotoStudio
{
    public sealed class BodyBox
    {
        public double x { get; internal set; }
        public double y { get; internal set; }
        public double w { get; internal set; }
        public double h { get; internal set; }
    }

    public sealed class BodySubjectInfo
    {
        public int index { get; internal set; }
        public BodyBox box { get; internal set; }
        public string[] available { get; internal set; }
    }

    internal sealed class BodyPoint
    {
        internal double X, Y, Visibility;
    }

    internal sealed class BodyScene
    {
        internal string SourceHash;
        internal BodyGeometry[] Subjects;

        internal void Prepare()
        {
            // Masks remain in the original coordinate space. A bystander's confident or soft
            // silhouette protects its pixels even when that person's own sliders are all at zero.
            if (Subjects.Length > 1)
                foreach (BodyGeometry subject in Subjects)
                {
                    subject.OtherPeople = new byte[subject.Mask.Length];
                    foreach (BodyGeometry other in Subjects)
                    {
                        if (other == subject) continue;
                        for (int i = 0; i < other.Mask.Length; i++)
                            if (other.Mask[i] > subject.OtherPeople[i]) subject.OtherPeople[i] = other.Mask[i];
                    }
                }
            foreach (BodyGeometry subject in Subjects) subject.Measure();
            if (!Subjects.Any(s => s.Available.Any(available => available)))
                throw new InvalidDataException("未找到能与人物轮廓对应的可见肩部或躯干。");
        }
    }

    internal sealed class BodyGeometry
    {
        internal BodyPoint[] Points;
        internal byte[] Mask, OtherPeople;
        internal bool Legacy, Torso;
        internal readonly bool[] Visible = new bool[33];
        internal readonly bool[] Available = new bool[10];
        internal BodyBox Box;
        internal double Left, Top, Right, Bottom;

        internal static double Distance(BodyPoint a, BodyPoint b)
        {
            double x = a.X - b.X, y = a.Y - b.Y;
            return Math.Sqrt(x * x + y * y);
        }

        private double Along(int a, int b, double t)
        {
            return BodyAssets.Probability(Mask, Points[a].X * (1 - t) + Points[b].X * t,
                Points[a].Y * (1 - t) + Points[b].Y * t);
        }

        internal bool HasBone(int a, int b)
        {
            return Visible[a] && Visible[b] && Distance(Points[a], Points[b]) > .011 && Along(a, b, .5) >= .22;
        }

        private static int TrimEdge(int[] counts, int discard, bool fromEnd)
        {
            int at = fromEnd ? counts.Length - 1 : 0, step = fromEnd ? -1 : 1;
            while (discard >= counts[at]) { discard -= counts[at]; at += step; }
            return at;
        }

        private int MeasureMask()
        {
            int side = BodyAssets.MaskSide, left = side, top = side, right = -1, bottom = -1, strong = 0, exclusive = 0;
            var columns = new int[side];
            var rows = new int[side];
            for (int y = 0; y < side; y++) for (int x = 0; x < side; x++)
            {
                byte p = Mask[y * side + x];
                if (p >= 11)
                {
                    left = Math.Min(left, x); right = Math.Max(right, x);
                    top = Math.Min(top, y); bottom = Math.Max(bottom, y);
                }
                if (p >= 153)
                {
                    columns[x]++; rows[y]++; strong++;
                    if (OtherPeople == null || OtherPeople[y * side + x] < 11) exclusive++;
                }
            }
            if (strong < (Legacy ? 120 : 52) || strong > Mask.Length * (Legacy ? .87 : .98))
                throw new InvalidDataException("人物分割的可见面积不足，或无法区分人物与背景。");
            // Include the full support of bilinear probability sampling during rendering. For
            // the selection box only, trim isolated high-probability pixels far from the person.
            Left = Math.Max(0, (left - .5) / side); Right = Math.Min(1, (right + 1.5) / side);
            Top = Math.Max(0, (top - .5) / side); Bottom = Math.Min(1, (bottom + 1.5) / side);
            int trim = strong / 320;
            left = TrimEdge(columns, trim, false); right = TrimEdge(columns, trim, true);
            top = TrimEdge(rows, trim, false); bottom = TrimEdge(rows, trim, true);
            Box = new BodyBox { x = (double)left / side, y = (double)top / side,
                w = (double)(right - left + 1) / side, h = (double)(bottom - top + 1) / side };
            return exclusive;
        }

        internal void Measure()
        {
            int exclusive = MeasureMask();
            if (Legacy)
            {
                Torso = true;
                for (int i = 0; i < Available.Length; i++) Available[i] = true;
            }
            for (int i = 0; i < Points.Length; i++)
            {
                BodyPoint p = Points[i];
                double threshold = i >= 13 && i <= 16 ? .44 : .53;
                Visible[i] = p.Visibility >= threshold && p.X > .005 && p.X < .995 && p.Y > .005 && p.Y < .995 &&
                    BodyAssets.Probability(Mask, p.X, p.Y) >= .30;
            }
            if (Legacy || exclusive < 36) return;
            bool shoulders = HasBone(11, 12) && Distance(Points[11], Points[12]) >= .017;
            if (!shoulders) return;
            Available[0] = true;
            Available[1] = HasBone(11, 13) || HasBone(12, 14) || HasBone(13, 15) || HasBone(14, 16);
            double shoulderX = (Points[11].X + Points[12].X) / 2, hipX = (Points[23].X + Points[24].X) / 2;
            double shoulderY = (Points[11].Y + Points[12].Y) / 2, hipY = (Points[23].Y + Points[24].Y) / 2;
            double spineX = hipX - shoulderX, spineY = hipY - shoulderY;
            Torso = HasBone(23, 24) && spineX * spineX + spineY * spineY >= .055 * .055 &&
                Distance(Points[11], Points[23]) + Distance(Points[12], Points[24]) >= .13 &&
                Along(11, 23, .54) >= .22 && Along(12, 24, .54) >= .22;
            if (!Torso) return;
            Func<double, bool> trunkAt = t => BodyAssets.Probability(Mask, shoulderX + (hipX - shoulderX) * t,
                shoulderY + (hipY - shoulderY) * t) >= .30;
            Available[2] = trunkAt(.54);
            Available[3] = trunkAt(.81);
            Available[4] = trunkAt(1);
            bool leftThigh = HasBone(23, 25), rightThigh = HasBone(24, 26);
            bool leftCalf = HasBone(25, 27), rightCalf = HasBone(26, 28);
            Available[5] = leftThigh || rightThigh;
            Available[6] = leftCalf || rightCalf;
            Available[7] = (leftThigh && leftCalf) || (rightThigh && rightCalf);
            Available[8] = leftThigh && leftCalf && rightThigh && rightCalf;
            Available[9] = trunkAt(.66);
        }

        internal bool SamePerson(BodyGeometry other)
        {
            // Re-detection has the same original bytes. Both the silhouette and anatomical
            // anchors must still agree before an edited person's slot can be reused.
            int common = 0, either = 0;
            for (int i = 0; i < Mask.Length; i++)
            {
                bool a = Mask[i] >= 128, b = other.Mask[i] >= 128;
                if (a || b) either++;
                if (a && b) common++;
            }
            if (either == 0 || common < either * .58) return false;
            double breadth = Math.Min(Distance(Points[11], Points[12]), Distance(other.Points[11], other.Points[12]));
            double tolerance = Math.Max(.010, Math.Min(.055, breadth * .28));
            int anchors = 0;
            foreach (int i in new[] { 11, 12, 23, 24 })
            {
                if (!Visible[i] || !other.Visible[i]) continue;
                if (Distance(Points[i], other.Points[i]) > tolerance) return false;
                anchors++;
            }
            foreach (int i in new[] { 0, 7, 8 })
                if (Visible[i] && other.Visible[i] && Distance(Points[i], other.Points[i]) > tolerance) return false;
            return anchors >= 2;
        }
    }
}
