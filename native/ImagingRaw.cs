using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace AIPhotoStudio
{
    public sealed class RawCameraMode
    {
        public string Make { get; private set; }
        public string Model { get; private set; }
        public string Extension { get; private set; }
        public string CompressionOrMode { get; private set; }
        public int? SensorBits { get; private set; }
        public string CropMode { get; private set; }
        public int SampleId { get; private set; }
        public string ValidationResult { get; private set; }
        internal RawCameraMode(CameraRawModes.Profile profile)
        {
            Make=profile.Model=="OM-1"?"OM System":profile.Make;Model=profile.Model;Extension=profile.Ext;
            CompressionOrMode=profile.Description;SensorBits=profile.Bits;SampleId=profile.Id;
            CropMode=profile.Width+"×"+profile.Height+" 有效解码区";
            if(profile.Ext=="DNG")CropMode+="，建议裁切 "+profile.CropW+"×"+profile.CropH;
            ValidationResult="已核对 RAW.PIXLS "+profile.Id+" 的完整感光平面和 16 位成像";
        }
    }

    public static partial class Imaging
    {
        public static readonly System.Collections.ObjectModel.ReadOnlyCollection<RawCameraMode> RawCameraModes =
            Array.AsReadOnly(Array.ConvertAll(CameraRawModes.All,p=>new RawCameraMode(p)));

        public static Dictionary<string, object> InspectRawHeader(string path)
        {
            string full = ImagingInput.FullPath(path);
            using (var stream = ImagingInput.OpenRead(full))
            {
                try
                {
                    if(VendorRaw.UsesWorker(full,Path.GetExtension(full).TrimStart('.'),stream))
                        return VendorRaw.Read(full,"inspect",0,CancellationToken.None).Metadata;
                }
                catch(NotSupportedException){} // Header-only inspection can still report a recognized, unsupported camera container.
                return RawInspection.Read(stream, full).Properties;
            }
        }

        // A conformance probe for physical photos and test fixtures. Reads sensor strips without invoking a JPEG renderer.
        public static Dictionary<string, object> MeasureRawSensor(string path, CancellationToken cancel)
        {
            return MeasureRawSensor(path,cancel,0);
        }
        public static Dictionary<string, object> MeasureRawSensor(string path, CancellationToken cancel,int selected)
        {
            string full = ImagingInput.FullPath(path);
            using (var stream = ImagingInput.OpenRead(full))
            {
                if(VendorRaw.UsesWorker(full,Path.GetExtension(full).TrimStart('.'),stream))return VendorRaw.Read(full,"measure",0,cancel,selected).Metadata;
                if(selected!=0)throw new NotSupportedException("这份原片只有一个可选感光平面。");
                var info = RawInspection.Read(stream, full);
                info.Measure(stream, cancel);
                return info.Properties;
            }
        }
    }

    internal sealed partial class RawInspection
    {
        internal readonly Dictionary<string, object> Properties = new Dictionary<string, object>();
        private sealed class Field
        {
            internal int Type;
            internal uint Count;
            internal byte[] Slot;
        }
        private bool little;
        private Dictionary<int, Field> sensor;
        private Dictionary<int, Field> firstDirectory;
        private int sensorDirectories;

        private uint UInt(byte[] data, int start, int bytes)
        {
            uint result = 0;
            for (int i = 0; i < bytes; i++)
                result = (result << 8) | data[start + (little ? bytes - 1 - i : i)];
            return result;
        }
        private static byte[] At(Stream stream, long offset, int length)
        {
            if (offset < 0 || length < 0 || offset + length > stream.Length) throw new InvalidDataException("RAW 目录或数据超出文件。");
            var bytes = new byte[length];
            stream.Position = offset;
            int done = 0;
            while (done < length)
            {
                int n = stream.Read(bytes, done, length - done);
                if (n == 0) throw new EndOfStreamException("RAW 数据不完整。");
                done += n;
            }
            return bytes;
        }
        private byte[] Payload(Stream stream, Field field, int unit)
        {
            int length = checked(unit * (int)field.Count);
            return length <= 4 ? field.Slot : At(stream, UInt(field.Slot, 0, 4), length);
        }
        private double[] Numbers(Stream stream, Dictionary<int, Field> dir, int tag, int limit)
        {
            Field field;
            if (!dir.TryGetValue(tag, out field) || field.Count > limit) return new double[0];
            int unit;
            switch (field.Type)
            {
                case 1: case 7: unit = 1; break;
                case 3: unit = 2; break;
                case 4: case 9: case 13: unit = 4; break;
                case 5: case 10: unit = 8; break;
                default: return new double[0];
            }
            var bytes = Payload(stream, field, unit);
            var result = new double[field.Count];
            for (int i = 0; i < result.Length; i++)
            {
                int at = i * unit;
                uint v = UInt(bytes, at, Math.Min(unit, 4));
                if (unit != 8) result[i] = field.Type == 9 ? (double)(int)v : v;
                else
                {
                    uint denominator = UInt(bytes, at + 4, 4);
                    if (denominator == 0) throw new InvalidDataException("RAW 元数据有无效分母。");
                    result[i] = field.Type == 10 ? (double)(int)v / (int)denominator : (double)v / denominator;
                }
            }
            return result;
        }
        private long First(Stream stream, Dictionary<int, Field> dir, int tag, long missing)
        {
            double[] found = Numbers(stream, dir, tag, 8);
            return found.Length == 0 ? missing : (long)found[0];
        }
        private string TextField(Stream stream, Dictionary<int, Field> dir, int tag)
        {
            Field field;
            if (!dir.TryGetValue(tag, out field) || field.Type != 2 || field.Count < 1 || field.Count > 1024) return "";
            return Encoding.ASCII.GetString(Payload(stream, field, 1), 0, (int)field.Count).TrimEnd('\0', ' ');
        }
        private string ListField(Stream stream, Dictionary<int, Field> dir, int tag)
        {
            return String.Join(",", Array.ConvertAll(Numbers(stream, dir, tag, 16), v => v.ToString("G9", CultureInfo.InvariantCulture)));
        }
        private void ReadTiff(Stream stream, byte[] header)
        {
            little = header[0] == 73;
            var queue = new Queue<uint>();
            var seen = new HashSet<uint>();
            queue.Enqueue(UInt(header, 4, 4));
            long bestPixels = 0;
            while (queue.Count > 0)
            {
                uint offset = queue.Dequeue();
                if (offset == 0 || !seen.Add(offset)) continue;
                if (seen.Count > 128) throw new InvalidDataException("RAW 目录数量超出上限。");
                int count = (int)UInt(At(stream, offset, 2), 0, 2);
                if (count > 8192) throw new InvalidDataException("RAW 目录项数超出上限。");
                var bytes = At(stream, offset + 2L, count * 12 + 4);
                var dir = new Dictionary<int, Field>();
                for (int p = 0; p < count * 12; p += 12)
                {
                    var field = new Field { Type = (int)UInt(bytes, p + 2, 2), Count = UInt(bytes, p + 4, 4), Slot = new byte[4] };
                    Buffer.BlockCopy(bytes, p + 8, field.Slot, 0, 4);
                    dir[(int)UInt(bytes, p, 2)] = field;
                }
                if (firstDirectory == null) firstDirectory = dir;
                foreach (double sub in Numbers(stream, dir, 330, 128)) queue.Enqueue((uint)sub);
                long exifIfd = First(stream,dir,34665,0);
                if (exifIfd > 0) queue.Enqueue((uint)exifIfd);
                queue.Enqueue(UInt(bytes, count * 12, 4));
                string make = TextField(stream, dir, 271), model = TextField(stream, dir, 272);
                if (make.Length > 0 && !Properties.ContainsKey("cameraManufacturer")) Properties["cameraManufacturer"] = make;
                if (model.Length > 0 && !Properties.ContainsKey("cameraModel")) Properties["cameraModel"] = model;
                string unique = TextField(stream,dir,50708);
                if (unique.Length > 0)
                {
                    Properties["uniqueCameraModel"]=unique;
                    if (!Properties.ContainsKey("cameraModel")) Properties["cameraModel"]=unique;
                }
                string date = TextField(stream,dir,36867);
                if (date.Length > 0) Properties["dateTaken"] = date;
                if (dir.ContainsKey(50706)) Properties["dngVersion"] = ListField(stream, dir, 50706);
                long photo = First(stream, dir, 262, -1);
                long w = First(stream, dir, 256, 0), h = First(stream, dir, 257, 0);
                if ((First(stream, dir, 254, 0) & 1) != 0 || (photo != 32803 && photo != 34892) ||
                    w < 1 || h < 1 || w > 100000 || h > 100000) continue;
                Properties["sensorDirectoryCount"] = ++sensorDirectories;
                if (w*h <= bestPixels) continue;
                bestPixels = w * h;
                sensor = dir;
                Properties["sensorWidth"] = w; Properties["sensorHeight"] = h; Properties["photometric"] = photo;
                Properties["bitsPerSample"] = First(stream, dir, 258, 0);
                Properties["compressionCode"] = First(stream, dir, 259, 1);
                Properties["orientation"] = First(stream, dir, 274, 1);
                Properties["cfaRepeat"] = ListField(stream, dir, 33421); Properties["cfaPattern"] = ListField(stream, dir, 33422);
                Properties["blackLevel"] = ListField(stream, dir, 50714); Properties["whiteLevel"] = ListField(stream, dir, 50717);
                Properties["activeArea"] = ListField(stream, dir, 50829); Properties["defaultCropOrigin"] = ListField(stream, dir, 50719);
                Properties["defaultCropSize"] = ListField(stream, dir, 50720);
            }
        }

        internal static RawInspection Read(Stream stream, string path)
        {
            var result = new RawInspection();
            result.Properties["extension"] = Path.GetExtension(path).ToUpperInvariant();
            result.Properties["fileBytes"] = stream.Length; result.Properties["cameraModeVerified"] = false;
            byte[] head = At(stream, 0, (int)Math.Min(80, stream.Length));
            if (head.Length < 16) throw new InvalidDataException("RAW 文件头不完整。");
            if ((head[0] == 73 && head[1] == 73 && head[2] == 42 && head[3] == 0) ||
                (head[0] == 77 && head[1] == 77 && head[2] == 0 && head[3] == 42))
            {
                result.Properties["container"] = head[8] == 67 && head[9] == 82 && head[10] == 2 ? "Canon CR2 (TIFF)" : "TIFF";
                result.ReadTiff(stream, head);
            }
            else if (Encoding.ASCII.GetString(head, 0, 16) == "FUJIFILMCCD-RAW ")
            {
                result.Properties["container"] = "Fujifilm RAF";
                if (head.Length >= 60) result.Properties["cameraModel"] = Encoding.ASCII.GetString(head, 28, 32).TrimEnd('\0',' ');
                result.Properties["cameraManufacturer"] = "Fujifilm";
            }
            else if(Encoding.ASCII.GetString(head,0,4)=="IIRO"||Encoding.ASCII.GetString(head,0,4)=="MMOR"||
                (head[0]==73&&head[1]==73&&head[2]==0x55&&head[3]==0))
            {
                result.Properties["container"]=head[2]==0x55?"Panasonic RW2":"Olympus ORF";
                result.ReadTiff(stream,head);
            }
            else if (Encoding.ASCII.GetString(head, 4, 4) == "ftyp") result.Properties["container"] = "ISO BMFF: " + Encoding.ASCII.GetString(head, 8, 4);
            else if (Encoding.ASCII.GetString(head, 6, 8) == "HEAPCCDR") result.Properties["container"] = "Canon CIFF";
            else result.Properties["container"] = "未识别文件头";
            return result;
        }

        private void ReadSensorRows(Stream stream, CancellationToken cancel, Action<int, ushort[]> consume)
        {
            cancel.ThrowIfCancellationRequested();
            if (sensor == null || (long)Properties["photometric"] != 32803 || (long)Properties["compressionCode"] != 1 ||
                First(stream, sensor, 277, 1) != 1 || First(stream, sensor, 266, 1) != 1 || First(stream, sensor, 339, 1) != 1 ||
                First(stream, sensor, 284, 1) != 1 || First(stream,sensor,317,1) != 1 || sensor.ContainsKey(324))
                throw new NotSupportedException("传感器测量目前支持单平面、条带存储、无压缩的整数 CFA。");
            int w = (int)(long)Properties["sensorWidth"], h = (int)(long)Properties["sensorHeight"], bits = (int)(long)Properties["bitsPerSample"];
            if ((bits != 12 && bits != 14 && bits != 16) || (long)w * h > 100000000)
                throw new NotSupportedException("传感器测量支持最多 1 亿像素的 12、14、16 位 CFA。");
            long rowsPerStrip = First(stream, sensor, 278, h);
            if (rowsPerStrip <= 0) throw new InvalidDataException("传感器每条带行数无效。");
            int strips = checked((int)((h + rowsPerStrip - 1) / rowsPerStrip));
            var offsets = Numbers(stream, sensor, 273, 100000);
            var sizes = Numbers(stream, sensor, 279, 100000);
            if (offsets.Length != strips || sizes.Length != strips) throw new InvalidDataException("传感器条带清单与尺寸不一致。");
            int rowBytes = checked((w * bits + 7) / 8);
            int y = 0;
            var rawRow = new byte[rowBytes];
            var codes = new ushort[w];
            {
                for (int strip = 0; strip < strips; strip++)
                {
                    int rows = (int)Math.Min(rowsPerStrip, h - y);
                    long offset = (long)offsets[strip], size = (long)sizes[strip];
                    if (size < (long)rows * rowBytes || offset + size > stream.Length) throw new InvalidDataException("传感器条带内容不完整。");
                    stream.Position = offset;
                    for (int row = 0; row < rows; row++, y++)
                    {
                        cancel.ThrowIfCancellationRequested();
                        int read = 0;
                        while (read < rowBytes)
                        {
                            int next = stream.Read(rawRow, read, rowBytes - read);
                            if (next == 0) throw new EndOfStreamException("传感器条带被截断。");
                            read += next;
                        }
                        int cursor = 0, remaining = 0, reservoir = 0;
                        for (int x = 0; x < w; x++)
                        {
                            int code;
                            if (bits == 16) code = (int)UInt(rawRow, x * 2, 2);
                            else
                            {
                                while (remaining < bits) { reservoir = (reservoir << 8) | rawRow[cursor++]; remaining += 8; }
                                remaining -= bits; code = (reservoir >> remaining) & ((1 << bits) - 1);
                            }
                            codes[x] = (ushort)code;
                        }
                        consume(y, codes);
                    }
                }
            }
        }

        internal void Measure(Stream stream, CancellationToken cancel)
        {
            if (sensor == null) throw new NotSupportedException("文件未包含受支持的 CFA 传感器目录。");
            int w = (int)(long)Properties["sensorWidth"], h = (int)(long)Properties["sensorHeight"];
            int min = Int32.MaxValue, max = 0, unique = 0;
            var levels = new bool[65536]; var canonical = new byte[w * 2];
            using (var sha = SHA256.Create())
            {
                ReadSensorRows(stream, cancel, delegate(int y, ushort[] codes)
                {
                    for (int x = 0; x < w; x++)
                    {
                        int code = codes[x];
                        if (!levels[code]) { levels[code] = true; unique++; }
                        min = Math.Min(min, code); max = Math.Max(max, code);
                        canonical[x * 2] = (byte)code; canonical[x * 2 + 1] = (byte)(code >> 8);
                    }
                    sha.TransformBlock(canonical, 0, canonical.Length, canonical, 0);
                });
                sha.TransformFinalBlock(new byte[0], 0, 0);
                Properties["sensorSamples"] = (long)w * h; Properties["minSensorCode"] = min; Properties["maxSensorCode"] = max;
                Properties["distinctSensorCodes"] = unique;
                Properties["sensorSha256"] = BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
            }
        }
    }
}
