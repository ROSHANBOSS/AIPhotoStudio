using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AIPhotoStudio
{
    /// <summary>Content-addressed poses and per-person probability planes bound to one original image.</summary>
    public static partial class BodyAssets
    {
        public const int MaskSide = 128;
        public const string Model = "mediapipe-pose-landmarker-lite-fp16-1";
        private const int SubjectBytes = 33 * 12 + MaskSide * MaskSide;
        private const int LegacyBytes = 36 + SubjectBytes;
        public const int MaxBytes = 40 + BodyEdits.MaxPeople * SubjectBytes;
        private static readonly byte[] Header = Encoding.ASCII.GetBytes("BPL2");
        private static readonly byte[] LegacyHeader = Encoding.ASCII.GetBytes("BPL1");
        private static readonly object Gate = new object();
        private static string folder;
        private const int CachedScenes = 8;
        private static readonly List<KeyValuePair<string, BodyScene>> Scenes = new List<KeyValuePair<string, BodyScene>>(CachedScenes);
        private static readonly Dictionary<string, SourceIdentity> Sources =
            new Dictionary<string, SourceIdentity>(StringComparer.OrdinalIgnoreCase);

        [StructLayout(LayoutKind.Sequential)]
        private struct FileTimes { internal long Creation, Access, Write, Change; internal uint Attributes; }
        [StructLayout(LayoutKind.Sequential)]
        private struct FileIdentity { internal ulong Volume, Low, High; }
        [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetTimes(SafeFileHandle handle, int kind, out FileTimes value, uint size);
        [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetIdentity(SafeFileHandle handle, int kind, out FileIdentity value, uint size);

        private sealed class SourceIdentity
        {
            internal long Length;
            internal FileTimes Times;
            internal FileIdentity Id;
            internal string Hash;
            internal bool Matches(SourceIdentity other)
            {
                return other != null && Length == other.Length && Times.Creation == other.Times.Creation &&
                    Times.Write == other.Times.Write && Times.Change == other.Times.Change &&
                    Id.Volume == other.Id.Volume && Id.Low == other.Id.Low && Id.High == other.Id.High;
            }
        }

        public static void Configure(string directory)
        {
            if (String.IsNullOrWhiteSpace(directory)) throw new ArgumentException("缺少人体资源目录。");
            lock (Gate)
            {
                folder = Path.GetFullPath(directory);
                Directory.CreateDirectory(folder);
                Scenes.Clear();
                Sources.Clear();
            }
        }

        private static string Hex(byte[] bytes)
        {
            return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
        }

        private static bool IsHash(string value)
        {
            return value != null && value.Length == 64 && value.All(c => (c >= '0' && c <= '9') ||
                (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'));
        }

        private static byte[] HashBytes(string value)
        {
            if (!IsHash(value))
            {
                if (value != null && value.Length == 44)
                {
                    byte[] decoded = Convert.FromBase64String(value);
                    if (decoded.Length == 32) return decoded;
                }
                throw new ArgumentException("缺少有效的原片内容校验值。");
            }
            byte[] bytes = new byte[32];
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = Byte.Parse(value.Substring(2 * i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return bytes;
        }

        internal static Dictionary<string, double> Patch(string hash, string prefix = "body_ref")
        {
            var result = new Dictionary<string, double>(StringComparer.Ordinal);
            for (int i = 0; i < 5; i++)
                result.Add(prefix + i, Convert.ToInt64(hash.Substring(13 * i, i == 4 ? 12 : 13), 16));
            return result;
        }

        public static string ResourceHash(IDictionary<string, double> values)
        {
            return ResourceHash(values, "body_ref");
        }

        internal static string ResourceHash(IDictionary<string, double> values, string prefix)
        {
            if (values == null) return null;
            var result = new StringBuilder(64);
            bool any = false;
            for (int i = 0; i < 5; i++)
            {
                double part;
                if (!values.TryGetValue(prefix + i, out part)) return null;
                long maximum = i == 4 ? 281474976710655L : 4503599627370495L;
                if (Double.IsNaN(part) || Double.IsInfinity(part) || part < 0 || part > maximum || Math.Truncate(part) != part)
                    throw new InvalidDataException("识别资源指纹无效。");
                any |= part != 0;
                result.Append(((long)part).ToString(i == 4 ? "x12" : "x13", CultureInfo.InvariantCulture));
            }
            return any ? result.ToString() : null;
        }

        private static string AssetPath(string hash)
        {
            if (folder == null) throw new InvalidOperationException("尚未选定人体资源目录。");
            return Path.Combine(folder, hash + ".body");
        }

        public static bool Supported(IDictionary<string, double> values)
        {
            return TryResolveScene(values) != null;
        }

        public static bool MatchesSource(IDictionary<string, double> values, string sourceHash)
        {
            BodyScene scene = ResolveScene(values);
            return scene != null && String.Equals(scene.SourceHash, Hex(HashBytes(sourceHash)), StringComparison.Ordinal);
        }

        public static bool IsLegacy(IDictionary<string, double> values)
        {
            BodyGeometry first = ResolvePerson(values, 0);
            return first != null && first.Legacy;
        }

        // The change time also changes when an editor rewrites bytes in place and restores the visible modification time.
        // If a filesystem cannot report a stable identity, hash the open file for each non-neutral render.
        public static void ValidateFile(string path, IDictionary<string, double> values)
        {
            if (!BodyEdits.Active(values)) return;
            BodyScene scene = ResolveScene(values);
            if (scene == null) throw new InvalidOperationException("请先识别这张照片中的人物。");
            RequireAvailable(scene, values);
            if (!String.Equals(scene.SourceHash, SourceDigest(path), StringComparison.Ordinal))
                throw new InvalidOperationException("原片内容已变化，请重新识别当前照片的人体。");
        }

        internal static string SourceDigest(string path)
        {
            string full = WorkspacePaths.Full(path);
            string digest = null;
            using (var stream = WorkspacePaths.OpenRead(full, FileShare.Read))
            {
                var file = stream as FileStream;
                var now = new SourceIdentity { Length = stream.Length };
                bool stable = file != null && GetTimes(file.SafeFileHandle, 0, out now.Times, (uint)Marshal.SizeOf(typeof(FileTimes))) &&
                    GetIdentity(file.SafeFileHandle, 18, out now.Id, (uint)Marshal.SizeOf(typeof(FileIdentity))) &&
                    now.Times.Change > 0 && (now.Id.Low != 0 || now.Id.High != 0);
                if (stable)
                {
                    lock (Gate)
                    {
                        SourceIdentity previous;
                        if (Sources.TryGetValue(full, out previous) && now.Matches(previous)) digest = previous.Hash;
                    }
                }
                if (digest == null)
                {
                    using (var sha = SHA256.Create()) digest = Hex(sha.ComputeHash(stream));
                    if (stable)
                    {
                        now.Hash = digest;
                        lock (Gate)
                        {
                            if (Sources.Count >= 32 && !Sources.ContainsKey(full)) Sources.Clear();
                            Sources[full] = now;
                        }
                    }
                }
            }
            return digest;
        }

        public static BodySubjectInfo[] Describe(IDictionary<string, double> values)
        {
            BodyScene scene = TryResolveScene(values);
            if (scene == null) return new BodySubjectInfo[0];
            return scene.Subjects.Select((subject, index) => new BodySubjectInfo { index = index, box = subject.Box,
                available = BodyEdits.Keys.Where((key, region) => subject.Available[region])
                    .Select(key => BodyEdits.KeyFor(index, key)).ToArray() }).ToArray();
        }

        private static bool Available(BodyScene scene, int index, int region)
        {
            return index >= 0 && region >= 0 && scene != null && index < scene.Subjects.Length && scene.Subjects[index].Available[region];
        }

        public static bool CanAdjust(IDictionary<string, double> values, string actualKey)
        {
            int region = BodyEdits.RegionIndex(actualKey);
            return region >= 0 && Available(TryResolveScene(values), BodyEdits.PersonIndex(actualKey), region);
        }

        internal static void RequireAvailable(BodyScene scene, IDictionary<string, double> values)
        {
            foreach (var pair in values)
            {
                if (pair.Value == 0) continue;
                int region = BodyEdits.RegionIndex(pair.Key);
                if (region >= 0 && !Available(scene, BodyEdits.PersonIndex(pair.Key), region))
                    throw new InvalidOperationException("配方包含未识别的人物或不可见的美型部位。");
            }
        }

        public static bool CanReplace(IDictionary<string, double> oldValues, IDictionary<string, double> newValues)
        {
            return CanReplace(oldValues, newValues, null);
        }

        public static bool CanReplace(IDictionary<string, double> oldValues, IDictionary<string, double> newValues, IEnumerable<string> locks)
        {
            return CanReplace(oldValues,newValues,locks,null);
        }

        internal static bool CanReplace(IDictionary<string,double> oldValues,IDictionary<string,double> newValues,IEnumerable<string> locks,PersonDetectionDraft draft)
        {
            var protectedKeys = new HashSet<string>(locks ?? new string[0], StringComparer.Ordinal);
            if (oldValues != null)
                foreach (var pair in oldValues)
                    if (pair.Value != 0 && BodyEdits.RegionIndex(pair.Key) >= 0) protectedKeys.Add(pair.Key);
            protectedKeys.RemoveWhere(key => BodyEdits.RegionIndex(key) < 0);
            if (protectedKeys.Count == 0) return true;
            BodyScene before = ResolveScene(oldValues), after = ResolveScene(newValues,draft);
            if (before == null) return !BodyEdits.Active(oldValues);
            if (after == null || before.SourceHash != after.SourceHash) return false;
            if (ResourceHash(oldValues) == ResourceHash(newValues)) return true;
            foreach (var group in protectedKeys.GroupBy(BodyEdits.PersonIndex))
            {
                int index = group.Key;
                if (index >= before.Subjects.Length || index >= after.Subjects.Length) return false;
                var oldPerson = before.Subjects[index];
                var newPerson = after.Subjects[index];
                if (group.Any(key => !newPerson.Available[BodyEdits.RegionIndex(key)])) return false;
                if (!oldPerson.SamePerson(newPerson)) return false;
                if (after.Subjects.Any(person => person != newPerson && oldPerson.SamePerson(person)) ||
                    before.Subjects.Any(person => person != oldPerson && newPerson.SamePerson(person))) return false;
            }
            return true;
        }

        private static object Item(IDictionary<string, object> map, string key)
        {
            object value;
            if (map == null || !map.TryGetValue(key, out value) || value == null)
                throw new InvalidDataException("人体检测结果缺少 " + key + "。");
            return value;
        }

        private static double Number(object value)
        {
            if (!(value is double || value is float || value is int || value is decimal || value is long || value is byte))
                throw new InvalidDataException("人体检测结果包含无效数值。");
            double number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            if (Double.IsNaN(number) || Double.IsInfinity(number)) throw new InvalidDataException("人体检测结果超出范围。");
            return number;
        }

        internal static double Probability(byte[] mask, double u, double v)
        {
            double x = Math.Max(0, Math.Min(MaskSide - 1, u * MaskSide - .5));
            double y = Math.Max(0, Math.Min(MaskSide - 1, v * MaskSide - .5));
            int x0 = (int)x, y0 = (int)y, x1 = Math.Min(x0 + 1, MaskSide - 1), y1 = Math.Min(y0 + 1, MaskSide - 1);
            double fx = x - x0, fy = y - y0;
            return ((1 - fy) * ((1 - fx) * mask[y0 * MaskSide + x0] + fx * mask[y0 * MaskSide + x1]) +
                fy * ((1 - fx) * mask[y1 * MaskSide + x0] + fx * mask[y1 * MaskSide + x1])) / 255.0;
        }

        private static void Validate(BodyGeometry pose)
        {
            BodyPoint[] p = pose.Points;
            if (p == null || p.Length != 33 || pose.Mask == null || pose.Mask.Length != MaskSide * MaskSide)
                throw new InvalidDataException("需要完整的 33 个人体关键点和人物分割。");
            double min = pose.Legacy ? -2 : -16, max = pose.Legacy ? 3 : 17;
            foreach (BodyPoint point in p)
                if (point == null || Double.IsNaN(point.X) || Double.IsInfinity(point.X) || Double.IsNaN(point.Y) ||
                    Double.IsInfinity(point.Y) || Double.IsNaN(point.Visibility) || point.Visibility < 0 || point.Visibility > 1 ||
                    point.X < min || point.X > max || point.Y < min || point.Y > max)
                    throw new InvalidDataException("人体坐标或置信度越界。");
            if (!pose.Legacy) return;
            int[] anchors = {11, 12, 13, 14, 23, 24, 25, 26, 27, 28};
            int supported = 0;
            foreach (int index in anchors)
            {
                BodyPoint point = p[index];
                if (point.Visibility < (index == 13 || index == 14 ? .44 : .53) ||
                    point.X <= .005 || point.X >= .995 || point.Y <= .005 || point.Y >= .995)
                    throw new InvalidDataException("肩、肘、髋、膝或踝未完整显示。请使用清晰的单人全身照。");
                if (Probability(pose.Mask, point.X, point.Y) >= .32) supported++;
            }
            if (supported < 8 || BodyGeometry.Distance(p[11], p[23]) + BodyGeometry.Distance(p[12], p[24]) < .16 ||
                BodyGeometry.Distance(p[23], p[25]) + BodyGeometry.Distance(p[25], p[27]) < .105 ||
                BodyGeometry.Distance(p[24], p[26]) + BodyGeometry.Distance(p[26], p[28]) < .105 ||
                BodyGeometry.Distance(p[11], p[12]) < .021 || BodyGeometry.Distance(p[23], p[24]) < .012)
                throw new InvalidDataException("可见人体与分割不吻合，或人物过小、侧身遮挡。");
        }

        private static BodyGeometry ParseSubject(IDictionary<string, object> model)
        {
            var raw = Item(model, "points") as IEnumerable;
            if (raw == null || raw is string) throw new InvalidDataException("人体关键点无效。");
            var points = new List<BodyPoint>();
            foreach (object entry in raw)
            {
                var row = entry as IDictionary<string, object>;
                if (points.Count >= 33) throw new InvalidDataException("人体关键点数量异常。");
                points.Add(new BodyPoint { X = Number(Item(row, "x")), Y = Number(Item(row, "y")),
                    Visibility = Number(Item(row, "visibility")) });
            }
            string encoded = Item(model, "mask") as string;
            if (encoded == null || encoded.Length != 4 * ((MaskSide * MaskSide + 2) / 3))
                throw new InvalidDataException("人物分割尺寸无效。");
            byte[] mask;
            try { mask = Convert.FromBase64String(encoded); }
            catch (FormatException ex) { throw new InvalidDataException("人物分割编码无效。", ex); }
            var pose = new BodyGeometry { Points = points.ToArray(), Mask = mask };
            Validate(pose);
            return pose;
        }

        public static Dictionary<string, double> Save(string sourceHash, object results)
        {
            return Save(sourceHash,results,null);
        }

        internal static Dictionary<string,double> Save(string sourceHash,object results,PersonDetectionDraft draft)
        {
            byte[] source = HashBytes(sourceHash);
            var model = results as IDictionary<string, object>;
            double people = Number(Item(model, "people"));
            if (people == 0) throw new InvalidDataException("未检测到可编辑的人物。");
            if (people < 1 || people > BodyEdits.MaxPeople || people != Math.Truncate(people))
                throw new InvalidDataException("每张照片支持 1 至 "+BodyEdits.MaxPeople+" 位人物。");
            var subjects = new List<BodyGeometry>();
            object entries;
            if (model.TryGetValue("subjects", out entries))
            {
                var raw = entries as IEnumerable;
                if (raw == null || raw is string) throw new InvalidDataException("人物列表无效。");
                foreach (object entry in raw)
                {
                    if (subjects.Count >= people) throw new InvalidDataException("人物列表与检测数量不符。");
                    subjects.Add(ParseSubject(entry as IDictionary<string, object>));
                }
            }
            else if (people == 1) subjects.Add(ParseSubject(model));
            if (subjects.Count != (int)people) throw new InvalidDataException("缺少对应人物的关键点或分割。");
            byte[] bytes;
            using (var buffer = new MemoryStream(40 + subjects.Count * SubjectBytes))
            {
                using (var writer = new BinaryWriter(buffer, Encoding.UTF8, true))
                {
                    writer.Write(Header);
                    writer.Write(source);
                    writer.Write(subjects.Count);
                    foreach (BodyGeometry pose in subjects)
                    {
                        foreach (BodyPoint point in pose.Points)
                        {
                            writer.Write((float)point.X);
                            writer.Write((float)point.Y);
                            writer.Write((float)point.Visibility);
                        }
                        writer.Write(pose.Mask);
                    }
                }
                bytes = buffer.ToArray();
            }
            string hash;
            using (var sha = SHA256.Create()) hash = Hex(sha.ComputeHash(bytes));
            return Store(bytes, hash,draft);
        }

        private static Dictionary<string, double> Store(byte[] bytes, string hash,PersonDetectionDraft draft=null)
        {
            // Validate the exact on-disk precision and format before publishing an immutable file.
            BodyScene verified = Decode(bytes, hash);
            if(draft!=null){draft.Bodies[hash]=new PersonDetectionDraft.Asset<BodyScene>{Bytes=bytes,Scene=verified};return Patch(hash);}
            lock (Gate)
            {
                string file = AssetPath(hash);
                if (!File.Exists(file))
                {
                    string temp = Path.Combine(folder, ".b-" + Guid.NewGuid().ToString("N") + ".tmp");
                    try
                    {
                        File.WriteAllBytes(temp, bytes);
                        try { File.Move(temp, file); }
                        catch (IOException) { if (!File.Exists(file)) throw; verified = Decode(ReadBytes(file), hash); }
                    }
                    finally { if (File.Exists(temp)) File.Delete(temp); }
                }
                else verified = Decode(ReadBytes(file), hash);
                Remember(hash, verified);
            }
            return Patch(hash);
        }

        public static Dictionary<string, double> Import(byte[] bytes, string expectedHash)
        {
            string hash = Hex(HashBytes(expectedHash));
            return Store(bytes, hash);
        }

        public static byte[] Read(IDictionary<string, double> values)
        {
            return Read(values,null);
        }

        internal static byte[] Read(IDictionary<string,double> values,PersonDetectionDraft draft)
        {
            string hash = ResourceHash(values);
            if (hash == null) throw new InvalidDataException("配方缺少人体资源指纹。");
            if(draft!=null&&draft.Bodies.ContainsKey(hash))return draft.Bodies[hash].Bytes;
            lock (Gate)
            {
                byte[] data = ReadBytes(AssetPath(hash));
                Decode(data, hash);
                return data;
            }
        }

        private static void Remember(string hash, BodyScene scene)
        {
            Scenes.RemoveAll(entry => entry.Key == hash);
            if (Scenes.Count == CachedScenes) Scenes.RemoveAt(Scenes.Count - 1);
            Scenes.Insert(0, new KeyValuePair<string, BodyScene>(hash, scene));
        }

        private static BodyScene ReadAsset(string hash)
        {
            string path = AssetPath(hash);
            if (!File.Exists(path)) throw new FileNotFoundException("缺少这张照片的已识别人体资源。", path);
            for (int i = 0; i < Scenes.Count; i++)
            {
                var entry = Scenes[i];
                if (entry.Key != hash) continue;
                if (i > 0) { Scenes.RemoveAt(i); Scenes.Insert(0, entry); }
                return entry.Value;
            }
            BodyScene scene = Decode(ReadBytes(path), hash);
            Remember(hash, scene);
            return scene;
        }

        private static bool ValidLength(long length)
        {
            return length == LegacyBytes || (length >= 40 + SubjectBytes && length <= MaxBytes && (length - 40) % SubjectBytes == 0);
        }

        private static byte[] ReadBytes(string path)
        {
            using (var stream = File.OpenRead(path))
            {
                if (!ValidLength(stream.Length)) throw new InvalidDataException("人体资源长度异常。");
                using (var reader = new BinaryReader(stream)) return reader.ReadBytes((int)stream.Length);
            }
        }

        private static BodyScene Decode(byte[] bytes, string hash)
        {
            if (bytes == null || !ValidLength(bytes.Length)) throw new InvalidDataException("人体资源长度异常。");
            using (var sha = SHA256.Create())
                if (Hex(sha.ComputeHash(bytes)) != hash) throw new InvalidDataException("人体资源内容校验失败。");
            using (var reader = new BinaryReader(new MemoryStream(bytes, false)))
            {
                byte[] header = reader.ReadBytes(4);
                bool legacy = header.SequenceEqual(LegacyHeader);
                if (!legacy && !header.SequenceEqual(Header)) throw new InvalidDataException("无法读取此人体资源版本。");
                string source = Hex(reader.ReadBytes(32));
                int count = legacy ? 1 : reader.ReadInt32();
                if (count < 1 || count > BodyEdits.MaxPeople || bytes.Length != (legacy ? LegacyBytes : 40 + count * SubjectBytes))
                    throw new InvalidDataException("人体资源的人员数量与文件长度不符。");
                var scene = new BodyScene { SourceHash = source, Subjects = new BodyGeometry[count] };
                for (int person = 0; person < count; person++)
                {
                    var pose = new BodyGeometry { Legacy = legacy, Points = new BodyPoint[33] };
                    for (int i = 0; i < pose.Points.Length; i++)
                        pose.Points[i] = new BodyPoint { X = reader.ReadSingle(), Y = reader.ReadSingle(), Visibility = reader.ReadSingle() };
                    pose.Mask = reader.ReadBytes(MaskSide * MaskSide);
                    Validate(pose);
                    scene.Subjects[person] = pose;
                }
                scene.Prepare();
                return scene;
            }
        }

        internal static BodyScene ResolveScene(IDictionary<string, double> values,PersonDetectionDraft draft=null)
        {
            string hash = ResourceHash(values);
            if (hash == null) return null;
            if(draft!=null&&draft.Bodies.ContainsKey(hash))return draft.Bodies[hash].Scene;
            lock (Gate) return ReadAsset(hash);
        }

        private static BodyScene TryResolveScene(IDictionary<string, double> values)
        {
            try { return ResolveScene(values); }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (InvalidDataException) { return null; }
        }

        internal static BodyGeometry ResolvePerson(IDictionary<string, double> values, int index)
        {
            BodyScene scene = ResolveScene(values);
            return scene != null && index >= 0 && index < scene.Subjects.Length ? scene.Subjects[index] : null;
        }

        internal static BodyGeometry Resolve(IDictionary<string, double> values) { return ResolvePerson(values, 0); }
    }
}
