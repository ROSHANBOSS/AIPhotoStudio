using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace AIPhotoStudio
{
    internal sealed class DeliveryVariant
    {
        public string folder, pattern, format, colorSpace, matte;
        public int longEdge, quality, maxBytes, minEdge;
        internal string Extension { get { return format=="jpeg"?".jpg":format=="png"?".png":".tif"; } }

        private static void CheckName(string name, int limit)
        {
            if(String.IsNullOrWhiteSpace(name) || name.Length>limit || name!=name.Trim() || name.EndsWith(".",StringComparison.Ordinal) ||
                name.IndexOfAny(Path.GetInvalidFileNameChars())>=0 || name.Any(Char.IsControl))
                throw new ArgumentException("名称须为单个 Windows 文件名，不能含分隔符、尾随空格或句点。");
            string first=name.Split('.')[0].TrimEnd().ToUpperInvariant();
            if(new[]{"CON","PRN","AUX","NUL","CONIN$","CONOUT$","CLOCK$"}.Contains(first) ||
                first.Length==4 && (first.StartsWith("COM") || first.StartsWith("LPT")) && "0123456789¹²³".IndexOf(first[3])>=0)
                throw new ArgumentException("名称不能使用 Windows 保留的设备名。");
        }

        internal static DeliveryVariant Read(Dictionary<string,object> fields)
        {
            var settings=StudioBridge.ExportSettings.Read(fields);
            var v=new DeliveryVariant{folder=StudioBridge.TextOf(fields,"folder"),pattern=StudioBridge.TextOf(fields,"pattern"),
                format=settings.Format,longEdge=settings.LongEdge,quality=settings.Quality,matte=settings.Matte,maxBytes=settings.MaxBytes,
                minEdge=settings.MinEdge,colorSpace=StudioBridge.TextOf(fields,"colorSpace")};
            if(v.format==null || v.colorSpace!="srgb" && v.colorSpace!="adobeRgb")throw new ArgumentException("请选择导出格式和 ICC 色彩空间。");
            CheckName(v.folder,64);
            if(v.pattern.Length>120 || v.pattern.Replace("{name}","").Replace("{index}","").IndexOfAny(new[]{'{','}'})>=0)
                throw new ArgumentException("文件名模板只支持 {name} 原名和 {index} 序号。");
            v.Stem("原片",1);
            return v;
        }

        internal string Stem(string original, int index)
        {
            string numbered=pattern.Replace("{index}",index.ToString("D4",CultureInfo.InvariantCulture));
            string[] parts=numbered.Split(new[]{"{name}"},StringSplitOptions.None);
            if(parts.Length>1)
            {
                int room=(236-parts.Sum(p=>p.Length))/(parts.Length-1);
                if(original.Length>room){if(room>0 && Char.IsHighSurrogate(original[room-1]))room--;original=original.Substring(0,room);}
            }
            string stem=String.Join(original,parts);
            CheckName(stem,236);
            return stem;
        }
    }

    internal sealed class DeliveryMark
    {
        public string text, landscape, portrait, square, kind;
        public int size, opacity;
        public DeliveryLogoRef logo;
        internal Lazy<System.Windows.Media.Imaging.BitmapSource> picture;

        internal static DeliveryMark Read(Dictionary<string,object> fields)
        {
            var m=new DeliveryMark{text=StudioBridge.TextOf(fields,"text").Trim(),landscape=StudioBridge.TextOf(fields,"landscape"),
                portrait=StudioBridge.TextOf(fields,"portrait"),square=StudioBridge.TextOf(fields,"square"),
                size=DeliveryPreset.Integer(fields,"size",2,20),opacity=DeliveryPreset.Integer(fields,"opacity",10,100)};
            m.kind=StudioBridge.TextOf(fields,"kind");
            if(m.kind=="")m.kind="text";
            if(m.kind=="png"){
                object raw;
                if(!fields.TryGetValue("logo",out raw) || !(raw is Dictionary<string,object>))throw new ArgumentException("请先选择 PNG 标志。");
                m.logo=DeliveryLogoRef.Read((Dictionary<string,object>)raw);m.text="";
            }else if(m.kind!="text")throw new ArgumentException("未知的水印类型。");
            else if(m.text.Length==0 || m.text.Length>96 || m.text.Any(Char.IsControl))throw new ArgumentException("水印文字须为 1 至 96 个单行字符。");
            var positions=new[]{"topLeft","topRight","center","bottomLeft","bottomRight"};
            if(new[]{m.landscape,m.portrait,m.square}.Any(p=>!positions.Contains(p)))throw new ArgumentException("请选择横、竖和方图的水印位置。");
            return m;
        }
    }

    internal sealed class DeliveryPreset
    {
        public string id, name;
        public int revision;
        public DeliveryVariant proof, final;
        public DeliveryMark watermark;

        internal static int Integer(Dictionary<string,object> args,string key,int min,int max)
        {
            object raw;
            if(!args.TryGetValue(key,out raw))throw new ArgumentException("缺少字段 "+key);
            double n=StudioWorkspace.Number(raw);
            if(n<min || n>max || n!=Math.Truncate(n))throw new ArgumentException("字段 "+key+" 超出范围。");
            return (int)n;
        }

        internal static string Key(string id)
        {
            Guid value;
            if(!Guid.TryParseExact(id,"N",out value))throw new ArgumentException("交付记录编号无效。");
            return value.ToString("N");
        }

        internal static DeliveryPreset Read(Dictionary<string,object> fields)
        {
            var p=new DeliveryPreset{id=StudioBridge.TextOf(fields,"id"),name=StudioBridge.TextOf(fields,"name").Trim(),
                revision=Integer(fields,"revision",0,Int32.MaxValue),proof=DeliveryVariant.Read(Child(fields,"proof")),
                final=DeliveryVariant.Read(Child(fields,"final")),watermark=DeliveryMark.Read(Child(fields,"watermark"))};
            if(p.id.Length>0)p.id=Key(p.id);
            if(p.name.Length==0 || p.name.Length>80 || p.name.Any(Char.IsControl))throw new ArgumentException("交付预设名称须为 1 至 80 个单行字符。");
            if(String.Equals(p.proof.folder,p.final.folder,StringComparison.OrdinalIgnoreCase))throw new ArgumentException("小样和成片须使用各自的子目录。");
            return p;
        }

        private static Dictionary<string,object> Child(Dictionary<string,object> fields,string key)
        {
            object raw;
            if(!fields.TryGetValue(key,out raw) || !(raw is Dictionary<string,object>))throw new ArgumentException("缺少交付设置 "+key);
            return (Dictionary<string,object>)raw;
        }
    }
}
