using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media.Imaging;

namespace AIPhotoStudio
{
    internal sealed class ImagingInput
    {
        internal string Kind, Make, Model, Date;
        internal int Orientation, FrameCount;
        internal BitmapFrame Frame;
        internal System.Windows.Media.ColorContext Profile;
        private static readonly HashSet<string> Raw = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".nef", ".nrw", ".cr2", ".cr3", ".crw", ".arw", ".sr2", ".srf", ".raf", ".rw2", ".orf", ".dng",
            ".pef", ".srw", ".3fr", ".fff", ".rwl", ".x3f", ".raw", ".iiq", ".mos", ".mrw", ".mef", ".erf", ".dcr", ".kdc"
        };

        internal static string FullPath(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) throw new ArgumentException("请选择照片文件。");
            int colon = path.IndexOf(':');
            if (colon >= 0 && (colon != 1 || !Char.IsLetter(path[0]) || path.IndexOf(':', colon + 1) >= 0))
                throw new ArgumentException("不支持设备路径或文件备用数据流。");
            if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal))
                throw new ArgumentException("请选择普通文件路径。");
            string full = Path.GetFullPath(path);
            if (full.IndexOf(':', 2) >= 0) throw new ArgumentException("不支持设备路径或文件备用数据流。");
            string stem = Path.GetFileName(full).Split('.')[0].TrimEnd(' ').ToUpperInvariant();
            if (stem == "CON" || stem == "PRN" || stem == "AUX" || stem == "NUL" || stem == "CONIN$" || stem == "CONOUT$" ||
                ((stem.StartsWith("COM") || stem.StartsWith("LPT")) && stem.Length == 4 && Char.IsDigit(stem[3])))
                throw new ArgumentException("请选择普通文件名。");
            return full;
        }

        private static NotSupportedException RawUnsupported()
        {
            return new NotSupportedException("此 RAW 文件还没有经过相机机型、压缩方式与位深验证的完整传感器解码器。请导入相机或原厂软件导出的 TIFF、PNG 或 JPEG。");
        }

        internal static string TypeFromExtension(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".dng") return "DNG";
            string cameraKind=ext.TrimStart('.').ToUpperInvariant();
            if(VendorRaw.Supports(cameraKind))return cameraKind;
            if (Raw.Contains(ext))
            {
                string model = "";
                if (File.Exists(path))
                {
                    try
                    {
                        object value;
                        var meta = Imaging.InspectRawHeader(path);
                        if (meta.TryGetValue("cameraModel", out value)) model = "已识别机型 " + value + "。";
                    }
                    catch (InvalidDataException) { }
                    catch (IOException) { }
                }
                if (model.Length > 0) throw new NotSupportedException(model + RawUnsupported().Message);
                throw RawUnsupported();
            }
            switch (ext)
            {
                case ".jpg": case ".jpeg": case ".jpe": return "JPEG";
                case ".png": return "PNG";
                case ".tif": case ".tiff": return "TIFF";
                case ".heic": case ".heif": return "HEIC";
                default: throw new NotSupportedException("支持 JPEG、PNG、单页 TIFF 以及具备系统解码器的 HEIC。");
            }
        }

        internal static FileStream OpenRead(string path)
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
        }

        private static bool Fill(Stream stream, byte[] data, int count)
        {
            int total = 0;
            while (total < count)
            {
                int read = stream.Read(data, total, count - total);
                if (read == 0) return false;
                total += read;
            }
            return true;
        }

        private static uint Number(byte[] b, int index, int count, bool little)
        {
            uint n = 0;
            for (int i = 0; i < count; i++) n = (n << 8) | b[index + (little ? count - 1 - i : i)];
            return n;
        }

        // A DSLR TIFF-based RAW may expose a valid JPEG preview as its first IFD. Inspect the directory tree before WIC sees it.
        private static int InspectTiff(Stream stream, long origin, long length, bool rejectRaw)
        {
            if (length < 8) return 1;
            var head = new byte[8];
            stream.Position = origin;
            if (!Fill(stream, head, 8)) return 1;
            bool little = head[0] == 73 && head[1] == 73;
            if (!little && !(head[0] == 77 && head[1] == 77)) return 1;
            if (Number(head, 2, 2, little) != 42) return 1;
            var queue = new Queue<uint>();
            var seen = new HashSet<uint>();
            queue.Enqueue(Number(head, 4, 4, little));
            int orientation = 1;
            while (queue.Count > 0)
            {
                uint offset = queue.Dequeue();
                if (offset == 0 || !seen.Add(offset)) continue;
                if (seen.Count > 128 || offset + 2L > length) throw new InvalidDataException("TIFF 目录无效或过于复杂。");
                stream.Position = origin + offset;
                if (!Fill(stream, head, 2)) throw new InvalidDataException("TIFF 目录不完整。");
                int count = (int)Number(head, 0, 2, little);
                long dirSize = 12L * count + 4;
                if (count > 8192 || offset + 2L + dirSize > length) throw new InvalidDataException("TIFF 目录超出文件边界。");
                byte[] directory = new byte[dirSize];
                if (!Fill(stream, directory, directory.Length)) throw new InvalidDataException("TIFF 目录读取失败。");
                for (int i = 0; i < count; i++)
                {
                    int p = i * 12, tag = (int)Number(directory, p, 2, little), type = (int)Number(directory, p + 2, 2, little);
                    uint entries = Number(directory, p + 4, 4, little);
                    uint val = Number(directory, p + 8, type == 3 ? 2 : 4, little);
                    if (tag == 274 && type == 3 && entries == 1 && val >= 1 && val <= 8 && seen.Count == 1)
                        orientation = (int)val;
                    if (!rejectRaw) continue;
                    if (tag == 50706 || tag == 33421 || tag == 33422 || tag == 50710 ||
                        (tag == 262 && (val == 32803 || val == 34892)) || (tag == 259 && (val == 34713 || val == 32767)))
                        throw RawUnsupported();
                    if (tag != 330 || (type != 4 && type != 13) || entries == 0) continue;
                    if (entries == 1) { queue.Enqueue(val); continue; }
                    if (entries > 128 || val + 4L * entries > length) throw new InvalidDataException("TIFF 子目录超出文件边界。");
                    stream.Position = origin + val;
                    var offsets = new byte[entries * 4];
                    if (!Fill(stream, offsets, offsets.Length)) throw new InvalidDataException("TIFF 子目录不完整。");
                    for (int j = 0; j < offsets.Length; j += 4) queue.Enqueue(Number(offsets, j, 4, little));
                }
                if (!rejectRaw) break;
                queue.Enqueue(Number(directory, count * 12, 4, little));
            }
            return orientation;
        }

        private static int PngOrientation(Stream stream)
        {
            stream.Position = 8;
            byte[] header = new byte[8];
            for (int i = 0; i < 4096 && stream.Position + 12 <= stream.Length; i++)
            {
                if (!Fill(stream, header, 8)) break;
                uint size = Number(header, 0, 4, false);
                long next = stream.Position + size + 4;
                if (next > stream.Length) throw new InvalidDataException("PNG 数据块不完整。");
                string type = Encoding.ASCII.GetString(header, 4, 4);
                if (type == "eXIf") return InspectTiff(stream, stream.Position, size, false);
                if (type == "IEND") break;
                stream.Position = next;
            }
            return 1;
        }

        private static int ValidateHeader(Stream stream, string kind)
        {
            byte[] b = new byte[16];
            stream.Position = 0;
            if (!Fill(stream, b, b.Length)) throw new InvalidDataException("照片文件不完整。");
            int orientation = 1;
            bool valid = false;
            switch (kind)
            {
                case "JPEG": valid = b[0] == 255 && b[1] == 216 && b[2] == 255; break;
                case "PNG":
                    valid = b[0] == 137 && b[1] == 80 && b[2] == 78 && b[3] == 71 && b[4] == 13 && b[5] == 10 && b[6] == 26 && b[7] == 10;
                    if (valid) orientation = PngOrientation(stream);
                    break;
                case "TIFF":
                    valid = (b[0] == 73 && b[1] == 73 && b[2] == 42 && b[3] == 0) || (b[0] == 77 && b[1] == 77 && b[2] == 0 && b[3] == 42);
                    if (b[8] == 67 && b[9] == 82 && b[10] == 2 && b[11] == 0) throw RawUnsupported();
                    if (valid) orientation = InspectTiff(stream, 0, stream.Length, true);
                    break;
                case "HEIC":
                    uint size = Number(b, 0, 4, false);
                    if (Encoding.ASCII.GetString(b, 4, 4) != "ftyp" || size < 16 || size > 16384 || size > stream.Length) break;
                    var brands = new byte[size];
                    stream.Position = 0;
                    if (!Fill(stream, brands, brands.Length)) break;
                    for (int p = 8; p + 4 <= brands.Length; p += 4)
                    {
                        if (p == 12) continue;
                        string brand = Encoding.ASCII.GetString(brands, p, 4);
                        if (brand == "crx " || brand == "cr3 ") throw RawUnsupported();
                        if (brand == "avif" || brand == "avis") throw new NotSupportedException("该文件使用 AVIF 编码。");
                        if (brand == "heic" || brand == "heix" || brand == "hevc" || brand == "hevx" || brand == "mif1" || brand == "msf1") valid = true;
                    }
                    break;
            }
            stream.Position = 0;
            if (!valid) throw new InvalidDataException("文件扩展名与实际图像格式不符，或该容器变体尚未支持。");
            return orientation;
        }

        private static object Query(BitmapMetadata meta, string name)
        {
            if (meta == null) return null;
            try { return meta.GetQuery(name); }
            catch (NotSupportedException) { return null; }
            catch (ArgumentException) { return null; }
            catch (IOException) { return null; }
            catch (COMException) { return null; }
        }

        private static string Tag(BitmapMetadata meta, string name, string fallback)
        {
            object value = Query(meta, name) ?? Query(meta, fallback);
            string text = value as string;
            return text == null ? "" : text.Trim('\0', ' ');
        }

        internal static ImagingInput Read(Stream stream, string kind)
        {
            int hint = ValidateHeader(stream, kind);
            BitmapDecoder decoder;
            try
            {
                decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
            }
            catch (Exception ex)
            {
                if (kind == "HEIC" && (ex is NotSupportedException || ex is IOException || ex is COMException || ex is FileFormatException))
                    throw new NotSupportedException("当前 Windows HEIF/HEVC 解码器无法打开该照片。", ex);
                throw;
            }
            int frames = decoder.Frames.Count;
            if (frames != 1) throw new NotSupportedException("该文件有 " + frames + " 个图像帧；当前支持单张照片。");
            var frame = decoder.Frames[0];
            string nativeFormat = frame.Format.ToString();
            if (nativeFormat.IndexOf("Float", StringComparison.OrdinalIgnoreCase) >= 0 ||
                nativeFormat.IndexOf("FixedPoint", StringComparison.OrdinalIgnoreCase) >= 0 || nativeFormat.IndexOf("Half", StringComparison.OrdinalIgnoreCase) >= 0)
                throw new NotSupportedException("当前处理范围为 SDR 整数照片。浮点或扩展范围输入需要 HDR 处理流程。");
            long pixels = (long)frame.PixelWidth * frame.PixelHeight;
            if (frame.PixelWidth < 1 || frame.PixelHeight < 1 || frame.PixelWidth > 65535 || frame.PixelHeight > 65535 || pixels > 120000000L)
                throw new NotSupportedException("单张导入上限为 1.2 亿像素，单边上限为 65535 像素。");
            BitmapMetadata meta = frame.Metadata as BitmapMetadata;
            object orientation = Query(meta, "/app1/ifd/{ushort=274}") ?? Query(meta, "/ifd/{ushort=274}");
            int num;
            if (orientation != null && Int32.TryParse(orientation.ToString(), out num) && num >= 1 && num <= 8) hint = num;
            System.Windows.Media.ColorContext profile = null;
            try
            {
                var contexts = frame.ColorContexts;
                if (contexts != null && contexts.Count > 0) profile = contexts[0];
            }
            catch (Exception ex)
            {
                if (ex is ArgumentException || ex is NotSupportedException || ex is COMException)
                    throw new InvalidDataException("无法读取照片中的颜色配置文件。", ex);
                throw;
            }
            if (frame.Format == System.Windows.Media.PixelFormats.Cmyk32 && profile == null)
                throw new NotSupportedException("CMYK 照片需要内嵌 ICC 配置文件。");
            return new ImagingInput
            {
                Frame = frame, FrameCount = frames, Profile = profile, Orientation = hint, Kind = kind,
                Make = Tag(meta, "/app1/ifd/{ushort=271}", "/ifd/{ushort=271}"),
                Model = Tag(meta, "/app1/ifd/{ushort=272}", "/ifd/{ushort=272}"),
                Date = Tag(meta, "/app1/ifd/exif/{ushort=36867}", "/ifd/exif/{ushort=36867}")
            };
        }
    }
}
