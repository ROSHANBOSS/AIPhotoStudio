using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace AIPhotoStudio
{
    internal sealed class ImagingCube
    {
        private readonly int size;
        private readonly double[] domainMin, scale;
        private readonly float[] samples;
        private ImagingCube(int size, float[] samples, double[] min, double[] max)
        {
            this.size = size; this.samples = samples; domainMin = min;
            scale = new double[3];
            for (int i = 0; i < 3; i++)
            {
                if (max[i] - min[i] < 1e-9) throw new InvalidDataException("LUT 的每个输入通道需要非空范围。");
                scale[i] = (size - 1) / (max[i] - min[i]);
            }
        }
        private static double Number(string s)
        {
            double n;
            if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out n) || double.IsNaN(n) || double.IsInfinity(n) || Math.Abs(n) > 65536)
                throw new InvalidDataException("LUT 内有无效或超出范围的数值。");
            return n;
        }
        internal static ImagingCube Parse(byte[] data)
        {
            double[] min = { 0, 0, 0 }, max = { 1, 1, 1 };
            bool hasMin = false, hasMax = false;
            int n = 0, row = 0;
            float[] values = null;
            using (var input = new StreamReader(new MemoryStream(data, false), new UTF8Encoding(false, true), true))
            {
                string line;
                while ((line = input.ReadLine()) != null)
                {
                    if (line.Length > 4096) throw new InvalidDataException("LUT 行长度超出上限。");
                    int comment = line.IndexOf('#');
                    if (comment >= 0) line = line.Substring(0, comment);
                    string[] part = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                    if (part.Length == 0) continue;
                    switch (part[0])
                    {
                        case "TITLE":
                            if (row != 0) throw new InvalidDataException("LUT 标题必须位于数据之前。");
                            continue;
                        case "LUT_3D_SIZE":
                            if (n != 0 || part.Length != 2 || !Int32.TryParse(part[1], NumberStyles.None, CultureInfo.InvariantCulture, out n) || n < 2 || n > 65)
                                throw new InvalidDataException("支持每边 2 至 65 个格点的单张 3D LUT。");
                            values = new float[n * n * n * 3];
                            continue;
                        case "DOMAIN_MIN":
                        case "DOMAIN_MAX":
                            bool isMin = part[0] == "DOMAIN_MIN";
                            if (row != 0 || part.Length != 4 || (isMin ? hasMin : hasMax)) throw new InvalidDataException("LUT 输入范围声明无效。");
                            for (int i = 0; i < 3; i++) (isMin ? min : max)[i] = Number(part[i + 1]);
                            if (isMin) hasMin = true; else hasMax = true;
                            continue;
                        case "LUT_3D_INPUT_RANGE":
                            if (row != 0 || part.Length != 3 || hasMin || hasMax) throw new InvalidDataException("LUT 输入范围声明有冲突。");
                            for (int i = 0; i < 3; i++) { min[i] = Number(part[1]); max[i] = Number(part[2]); }
                            hasMin = hasMax = true;
                            continue;
                        case "LUT_1D_SIZE": case "LUT_1D_INPUT_RANGE":
                            throw new NotSupportedException("请选择纯 3D .cube 文件。");
                    }
                    if (part.Length != 3 || values == null || row >= n * n * n) throw new InvalidDataException("LUT 格点数量或行格式不正确。");
                    for (int i = 0; i < 3; i++) values[row * 3 + i] = (float)Number(part[i]);
                    row++;
                }
            }
            if (n == 0 || row != n * n * n) throw new InvalidDataException("LUT 缺少完整的三维格点数据。");
            return new ImagingCube(n, values, min, max);
        }

        private double Component(int index, int sy, int sz, double x, double y, double z)
        {
            double p00 = samples[index] + (samples[index + 3] - samples[index]) * x;
            double p10 = samples[index + sy] + (samples[index + sy + 3] - samples[index + sy]) * x;
            double p01 = samples[index + sz] + (samples[index + sz + 3] - samples[index + sz]) * x;
            double p11 = samples[index + sz + sy] + (samples[index + sz + sy + 3] - samples[index + sz + sy]) * x;
            double first = p00 + (p10 - p00) * y, second = p01 + (p11 - p01) * y;
            return first + (second - first) * z;
        }
        internal void Apply(ref double r, ref double g, ref double b, double strength)
        {
            int last = size - 1;
            double x = Math.Max(0, Math.Min(last, (r - domainMin[0]) * scale[0]));
            double y = Math.Max(0, Math.Min(last, (g - domainMin[1]) * scale[1]));
            double z = Math.Max(0, Math.Min(last, (b - domainMin[2]) * scale[2]));
            int ix = Math.Min((int)x, last - 1), iy = Math.Min((int)y, last - 1), iz = Math.Min((int)z, last - 1);
            int index = ((iz * size + iy) * size + ix) * 3, sy = size * 3, sz = sy * size;
            x -= ix; y -= iy; z -= iz;
            r += (Component(index, sy, sz, x, y, z) - r) * strength;
            g += (Component(index + 1, sy, sz, x, y, z) - g) * strength;
            b += (Component(index + 2, sy, sz, x, y, z) - b) * strength;
        }
    }

    public static partial class Imaging
    {
        private const int MaxCubeBytes = 24 * 1024 * 1024;
        private static readonly object CubeLock = new object();
        private static string cubeLibrary, cachedCubePath;
        private static long cachedCubeTime, cachedCubeBytes;
        private static ImagingCube cachedCube;
        public static void ConfigureCubeLibrary(string directory)
        {
            if (String.IsNullOrWhiteSpace(directory)) throw new ArgumentException("需要指定当前通道的 LUT 库。");
            lock (CubeLock) cubeLibrary = Path.GetFullPath(directory);
        }

        private static byte[] ReadCube(string path)
        {
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (input.Length < 1 || input.Length > MaxCubeBytes) throw new InvalidDataException("LUT 文件必须在 24 MiB 之内。");
                using (var reader = new BinaryReader(input))
                {
                    byte[] result = reader.ReadBytes((int)input.Length);
                    if (result.Length != input.Length) throw new IOException("LUT 文件未完整读取。");
                    return result;
                }
            }
        }
        private static string ShaHex(byte[] contents)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(contents)).Replace("-", "").ToLowerInvariant();
        }
        public static string CubeHash(IDictionary<string, double> values)
        {
            bool any = false;
            var key = new StringBuilder(64);
            for (int i = 0; i < 5; i++)
            {
                double segment = 0;
                if (values != null) values.TryGetValue("lut_ref" + i, out segment);
                double max = i == 4 ? 281474976710655.0 : 4503599627370495.0;
                if (segment < 0 || segment > max || double.IsNaN(segment) || segment != Math.Truncate(segment))
                    throw new ArgumentException("LUT 指纹无效。");
                any |= segment != 0;
                key.Append(((long)segment).ToString(i == 4 ? "x12" : "x13", CultureInfo.InvariantCulture));
            }
            return any ? key.ToString() : null;
        }
        public static Dictionary<string, double> ImportCube(string path, string libraryDir)
        {
            string full = ImagingInput.FullPath(path);
            if (!String.Equals(Path.GetExtension(full), ".cube", StringComparison.OrdinalIgnoreCase)) throw new NotSupportedException("请选择 .cube 文件。");
            byte[] bytes = ReadCube(full);
            var parsed = ImagingCube.Parse(bytes);
            string hash = ShaHex(bytes), library = Path.GetFullPath(libraryDir);
            Directory.CreateDirectory(library);
            string target = Path.Combine(library, hash + ".cube");
            if (!File.Exists(target))
            {
                string tmp = Path.Combine(library, ".lut-" + Guid.NewGuid().ToString("N") + ".tmp");
                bool owned = false;
                try
                {
                    using (var output = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        owned = true; output.Write(bytes, 0, bytes.Length); output.Flush(true);
                    }
                    try { File.Move(tmp, target); owned = false; }
                    catch (IOException) { if (!File.Exists(target)) throw; }
                }
                finally { if (owned) File.Delete(tmp); }
            }
            if (ShaHex(ReadCube(target)) != hash) throw new InvalidDataException("LUT 库中同名内容的摘要不一致。");
            lock (CubeLock)
            {
                cachedCubePath = target; cachedCubeBytes = bytes.Length;
                cachedCubeTime = File.GetLastWriteTimeUtc(target).Ticks; cachedCube = parsed;
            }
            var result = new Dictionary<string, double>(StringComparer.Ordinal) { {"lut_strength", 100} };
            for (int i = 0; i < 5; i++) result.Add("lut_ref" + i, Convert.ToInt64(hash.Substring(i * 13, i == 4 ? 12 : 13), 16));
            return result;
        }
        internal static ImagingCube ResolveCube(string hash)
        {
            if (hash == null) return null;
            lock (CubeLock)
            {
                if (String.IsNullOrEmpty(cubeLibrary)) throw new InvalidOperationException("当前通道尚未设置 LUT 库。");
                string path = Path.Combine(cubeLibrary, hash + ".cube");
                var file = new FileInfo(path);
                if (!file.Exists) throw new FileNotFoundException("此通道缺少该配方引用的 LUT。请重新导入同一份 .cube 文件。", path);
                if (cachedCube != null && String.Equals(path, cachedCubePath, StringComparison.OrdinalIgnoreCase) &&
                    file.Length == cachedCubeBytes && file.LastWriteTimeUtc.Ticks == cachedCubeTime) return cachedCube;
                byte[] bytes = ReadCube(path);
                if (ShaHex(bytes) != hash) throw new InvalidDataException("LUT 资源与配方指纹不符。");
                cachedCube = ImagingCube.Parse(bytes); cachedCubePath = path;
                cachedCubeTime = file.LastWriteTimeUtc.Ticks; cachedCubeBytes = bytes.Length;
                return cachedCube;
            }
        }
    }
}
