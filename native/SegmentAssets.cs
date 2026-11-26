using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Windows.Media.Imaging;

namespace AIPhotoStudio
{
    public static class SegmentAssets
    {
        private const int ByteCount = 128 * 128;
        private static string directory;

        public static readonly ControlSpec[] Controls = CreateControls();

        private static ControlSpec[] CreateControls()
        {
            var result = new List<ControlSpec>();
            for (int i = 0; i < 5; i++)
                result.Add(new ControlSpec { key = "segment_ref" + i, label = "分区资源 " + (i + 1),
                    group = "分区资源", min = 0, max = i == 4 ? 281474976710655d : 4503599627370495d, step = 1, initial = 0 });
            result.Add(new ControlSpec { key = "segment_foreground", label = "前景曝光", group = "前景与背景", min = -3, max = 3, step = .05, initial = 0 });
            result.Add(new ControlSpec { key = "segment_background", label = "背景曝光", group = "前景与背景", min = -3, max = 3, step = .05, initial = 0 });
            return result.ToArray();
        }

        public static void Configure(string path) { directory = Path.GetFullPath(path); }

        private static double Value(IDictionary<string, double> values, string key)
        {
            double result;
            return values != null && values.TryGetValue(key, out result) ? result : 0;
        }

        public static string Hash(IDictionary<string, double> values)
        {
            var pieces = new string[5];
            bool any = false;
            for (int i = 0; i < pieces.Length; i++)
            {
                double value = Value(values, "segment_ref" + i);
                if (!(value >= 0 && value <= Controls[i].max && value == Math.Truncate(value)))
                    throw new InvalidDataException("前背景分区编号无效。");
                any |= value != 0;
                pieces[i] = ((long)value).ToString(i == 4 ? "x12" : "x13", CultureInfo.InvariantCulture);
            }
            return any ? String.Concat(pieces) : null;
        }

        private static string Digest(byte[] bytes)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }

        private static void Validate(byte[] alpha)
        {
            if (alpha == null || alpha.Length != ByteCount || !alpha.Any(p => p < 64) || !alpha.Any(p => p > 191))
                throw new InvalidDataException("分区必须同时包含可辨认的前景和背景。");
        }

        private static string FilePath(string hash)
        {
            if (directory == null) throw new InvalidOperationException("尚未设置分区目录。");
            return Path.Combine(directory, hash + ".alpha");
        }

        public static Dictionary<string,double> Save(byte[] alpha)
        {
            Validate(alpha);
            string hash = Digest(alpha), dest = FilePath(hash);
            Directory.CreateDirectory(directory);
            if (!File.Exists(dest))
            {
                string temp = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
                bool created = false;
                try
                {
                    using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        created = true;
                        stream.Write(alpha, 0, alpha.Length);
                    }
                    try { File.Move(temp, dest); }
                    catch (IOException) { if (!File.Exists(dest)) throw; }
                }
                finally { if (created && File.Exists(temp)) File.Delete(temp); }
            }
            var patch = new Dictionary<string,double>();
            for (int i = 0; i < 5; i++) patch.Add("segment_ref" + i, Convert.ToInt64(hash.Substring(i * 13, i == 4 ? 12 : 13), 16));
            Read(patch);
            return patch;
        }

        public static byte[] Read(IDictionary<string,double> values)
        {
            string hash = Hash(values);
            if (hash == null) return null;
            string file = FilePath(hash);
            if (new FileInfo(file).Length != ByteCount) throw new InvalidDataException("前背景分区文件尺寸异常。");
            byte[] alpha = File.ReadAllBytes(file);
            Validate(alpha);
            if (Digest(alpha) != hash) throw new InvalidDataException("前背景分区校验失败。");
            return alpha;
        }

        public static bool Supported(IDictionary<string,double> values) { return Read(values) != null; }

        public static BitmapSource Apply(BitmapSource image, IDictionary<string,double> values, CancellationToken token)
        {
            double fg = Value(values, "segment_foreground"), bg = Value(values, "segment_background");
            if (fg == 0 && bg == 0) return image;
            var alpha = Read(values);
            if (alpha == null) throw new InvalidOperationException("请先在照片上点选前景和背景并确认分区。");
            return SubjectSelection.Apply(image, alpha, fg, bg, token);
        }
    }
}
