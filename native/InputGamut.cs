using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AIPhotoStudio
{
    internal sealed partial class StudioBridge
    {
        private async Task<object> CheckInputGamut(string id)
        {
            await renderSlots.WaitAsync(closing.Token);
            try { return await Task.Run(() => InputGamut.Sample(work.PathFor(id), closing.Token)); }
            finally { renderSlots.Release(); }
        }
    }

    public static partial class Imaging
    {
        internal static string GamutOverlay(byte[] marked, int cols, int rows, int orientation)
        {
            var pixels = new byte[cols*rows*4];
            for (int i = 0; i < marked.Length; i++)
            {
                if (marked[i] == 0) continue;
                pixels[4*i] = 185;
                pixels[4*i+1] = 33;
                pixels[4*i+2] = 246;
                pixels[4*i+3] = 194;
            }
            var bitmap = BitmapSource.Create(cols,rows,96,96,PixelFormats.Bgra32,null,pixels,4*cols);
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(Oriented(bitmap,orientation)));
            using (var buffer = new MemoryStream())
            {
                png.Save(buffer);
                return "data:image/png;base64,"+Convert.ToBase64String(buffer.GetBuffer(),0,(int)buffer.Length);
            }
        }
    }

    internal static class InputGamut
    {
        private const int Grid = 96;
        private const double Tolerance = .001; // linear sRGB; allow for rounding of ICC D50 colorants

        internal static object Sample(string path, CancellationToken cancel)
        {
            string kind = ImagingInput.TypeFromExtension(path);
            if (kind == "DNG" || VendorRaw.Supports(kind)) return new { status = "camera" };
            using (var file = ImagingInput.OpenRead(path))
            {
                var input = ImagingInput.Read(file, kind);
                if (input.Profile == null) return new { status = "untagged" };
                var profile = MatrixProfile.Read(input.Profile);
                if (profile == null) return new { status = "unsupported" };
                var frame = input.Frame;
                BitmapSource rgb = frame.Format == PixelFormats.Rgba64 ? (BitmapSource)frame :
                    new FormatConvertedBitmap(frame, PixelFormats.Rgba64, null, 0);
                int w = rgb.PixelWidth, h = rgb.PixelHeight, cols = Math.Min(w, Grid), rows = Math.Min(h, Grid);
                var row = new ushort[w * 4];
                var marked = new byte[cols*rows];
                int samples = 0, outside = 0;
                for (int j = 0; j < rows; j++)
                {
                    cancel.ThrowIfCancellationRequested();
                    int y = (2*j+1)*h/(2*rows);
                    rgb.CopyPixels(new Int32Rect(0, y, w, 1), row, w*8, 0);
                    for (int i = 0; i < cols; i++)
                    {
                        int p = 4*((2*i+1)*w/(2*cols));
                        if (row[p+3] == 0) continue;
                        double r = profile.Curves[0](row[p]/65535.0), g = profile.Curves[1](row[p+1]/65535.0), b = profile.Curves[2](row[p+2]/65535.0);
                        if (Double.IsNaN(r+g+b) || Double.IsInfinity(r+g+b)) return new { status = "unsupported" };
                        samples++;
                        for (int k = 0; k < 3; k++)
                        {
                            double value = profile.ToSrgb[k,0]*r + profile.ToSrgb[k,1]*g + profile.ToSrgb[k,2]*b;
                            if (value < -Tolerance || value > 1+Tolerance)
                            {
                                marked[j*cols+i] = 1;
                                outside++;
                                break;
                            }
                        }
                    }
                }
                return new { status = samples == 0 ? "transparent" : "measured", samples = samples, outside = outside,
                    overlay = outside == 0 ? null : Imaging.GamutOverlay(marked,cols,rows,input.Orientation) };
            }
        }

        // ICC.1 matrix/TRC input or display profiles. Read encoded input before WIC maps it to the integer working surface.
        private sealed class MatrixProfile
        {
            internal readonly Func<double,double>[] Curves = new Func<double,double>[3];
            internal readonly double[,] ToSrgb = new double[3,3];
            private static readonly double[,] FromPcs = {
                {3.1338561,-1.6168667,-.4906146}, {-.9787684,1.9161415,.0334540}, {.0719453,-.2289914,1.4052427}
            };
            private static uint U32(byte[] b, int p) { return ((uint)b[p]<<24) | ((uint)b[p+1]<<16) | ((uint)b[p+2]<<8) | b[p+3]; }
            private static int U16(byte[] b, int p) { return (b[p]<<8) | b[p+1]; }
            private static double Fixed(byte[] b, int p) { return unchecked((int)U32(b,p))/65536.0; }
            private static string Id(byte[] b, int p) { return Encoding.ASCII.GetString(b,p,4); }

            internal static MatrixProfile Read(ColorContext context)
            {
                byte[] bytes;
                using (var stream = context.OpenProfileStream())
                {
                    if (stream.Length < 132 || stream.Length > 4*1024*1024) return null;
                    using (var input = new BinaryReader(stream)) bytes = input.ReadBytes((int)stream.Length);
                }
                return Parse(bytes);
            }

            private static MatrixProfile Parse(byte[] b)
            {
                if (b.Length < 132 || U32(b,0) != (uint)b.Length || (b[8] != 2 && b[8] != 4) ||
                    Id(b,36) != "acsp" || Id(b,16) != "RGB " || Id(b,20) != "XYZ ") return null;
                string device = Id(b,12);
                if (device != "mntr" && device != "scnr" && device != "spac") return null;
                uint count = U32(b,128);
                if (count > 2048 || 132+12*count > b.Length) return null;
                var tags = new Dictionary<string,ArraySegment<byte>>();
                for (int i = 0; i < count; i++)
                {
                    int p = 132+12*i;
                    string key = Id(b,p);
                    uint off = U32(b,p+4), size = U32(b,p+8);
                    if (key.StartsWith("A2B",StringComparison.Ordinal) || key.StartsWith("D2B",StringComparison.Ordinal)) return null;
                    if (off < 132+12*count || off%4 != 0 || size < 8 || (ulong)off+size > (uint)b.Length || tags.ContainsKey(key)) return null;
                    tags.Add(key,new ArraySegment<byte>(b,(int)off,(int)size));
                }
                var profile = new MatrixProfile();
                for (int i = 0; i < 3; i++)
                {
                    string channel = "rgb"[i].ToString();
                    ArraySegment<byte> primary, trc;
                    if (!tags.TryGetValue(channel+"XYZ",out primary) || !tags.TryGetValue(channel+"TRC",out trc) ||
                        primary.Count < 20 || Id(b,primary.Offset) != "XYZ ") return null;
                    profile.Curves[i] = ToneCurve(trc);
                    if (profile.Curves[i] == null) return null;
                    for (int row = 0; row < 3; row++)
                        for (int axis = 0; axis < 3; axis++)
                            profile.ToSrgb[row,i] += FromPcs[row,axis]*Fixed(b,primary.Offset+8+4*axis);
                }
                return profile;
            }

            private static Func<double,double> ToneCurve(ArraySegment<byte> tag)
            {
                byte[] b = tag.Array;
                int off = tag.Offset;
                if (tag.Count < 12) return null;
                if (Id(b,off) == "curv")
                {
                    uint n = U32(b,off+8);
                    if (n > (tag.Count-12)/2) return null;
                    if (n == 0) return x=>x;
                    if (n == 1)
                    {
                        double gamma = U16(b,off+12)/256.0;
                        if (gamma <= 0) return null;
                        return x=>Math.Pow(x,gamma);
                    }
                    return x=>{
                        double pos=x*(n-1);
                        int low=(int)pos, high=Math.Min(low+1,(int)n-1);
                        return (U16(b,off+12+2*low)*(1-(pos-low)) + U16(b,off+12+2*high)*(pos-low))/65535.0;
                    };
                }
                if (Id(b,off) != "para") return null;
                int type = U16(b,off+8);
                int[] lengths = {1,3,4,5,7};
                if (type >= lengths.Length || tag.Count < 12+4*lengths[type]) return null;
                double[] q = Enumerable.Range(0,lengths[type]).Select(i=>Fixed(b,off+12+i*4)).ToArray();
                if (q[0] <= 0 || (type != 0 && q[1] <= 0)) return null;
                switch (type)
                {
                    case 0: return x=>Math.Pow(x,q[0]);
                    case 1: return x=>x >= -q[2]/q[1] ? Math.Pow(q[1]*x+q[2],q[0]) : 0;
                    case 2: return x=>(x >= -q[2]/q[1] ? Math.Pow(q[1]*x+q[2],q[0]) : 0)+q[3];
                    case 3: return x=>x >= q[4] ? Math.Pow(q[1]*x+q[2],q[0]) : q[3]*x;
                    default: return x=>x >= q[4] ? Math.Pow(q[1]*x+q[2],q[0])+q[5] : q[3]*x+q[6];
                }
            }
        }
    }
}
