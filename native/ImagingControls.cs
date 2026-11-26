using System;
using System.Collections.Generic;

namespace AIPhotoStudio
{
    public sealed class ControlSpec
    {
        public string key { get; set; }
        public string label { get; set; }
        public string group { get; set; }
        public double min { get; set; }
        public double max { get; set; }
        public double step { get; set; }
        public double initial { get; set; }
    }

    public static partial class Imaging
    {
        public const string EngineVersion = "cpu-srgb16-v1";
        internal static readonly string[] Bands = { "red", "orange", "yellow", "green", "aqua", "blue", "purple", "magenta" };
        internal static readonly string[] Channels = { "rgb", "red", "green", "blue" };
        internal static readonly string[] Zones = { "shadows", "midtones", "highlights" };
        internal static readonly string[] Primaries = { "red", "green", "blue" };
        private static readonly HashSet<string> GlobalColorKeys=new HashSet<string>(StringComparer.Ordinal);
        public static readonly ControlSpec[] Controls = MakeControls();
        public static readonly Dictionary<string, Dictionary<string, double>> Presets = MakePresets();

        private static ControlSpec Spec(string key, string label, string group, double min, double max, double step)
        {
            return new ControlSpec { key = key, label = label, group = group, min = min, max = max, step = step, initial = 0 };
        }

        private static ControlSpec[] MakeControls()
        {
            var list = new List<ControlSpec>();
            list.Add(Spec(CameraRawModes.FrameKey,"RAW 感光轨","RAW 原片",0,1,1));
            list.Add(Spec("exposure", "曝光（EV）", "基础", -5, 5, 0.05));
            string[] keys = { "contrast", "highlights", "shadows", "whites", "blacks", "temperature", "tint", "vibrance", "saturation", "clarity", "dehaze", "sharpness", "noise" };
            string[] names = { "对比度", "高光", "阴影", "白色色阶", "黑色色阶", "色温", "色调", "自然饱和度", "饱和度", "清晰度", "去雾", "锐化", "降噪" };
            for (int i = 0; i < keys.Length; i++) list.Add(Spec(keys[i], names[i], "基础", i >= 11 ? 0 : -100, 100, 1));
            list.Add(Spec("noise_chroma","彩色降噪","基础",0,100,1));
            list.Add(Spec("noise_luma","明暗降噪","基础",0,100,1));

            string[] channelNames = { "RGB", "红", "绿", "蓝" };
            for (int i = 0; i < Channels.Length; i++)
                foreach (int anchor in new[] { 25, 50, 75 })
                    list.Add(Spec("curve_" + Channels[i] + "_" + anchor, channelNames[i] + " · " + anchor + "%", "RGB 曲线", -100, 100, 1));
            string[] bandNames = { "红色", "橙色", "黄色", "绿色", "青色", "蓝色", "紫色", "洋红" };
            for (int i = 0; i < Bands.Length; i++)
            {
                string prefix = "hsl_" + Bands[i];
                list.Add(Spec(prefix + "_hue", bandNames[i] + " · 色相（°）", "HSL 八色", -45, 45, 1));
                list.Add(Spec(prefix + "_sat", bandNames[i] + " · 饱和度", "HSL 八色", -100, 100, 1));
                list.Add(Spec(prefix + "_lum", bandNames[i] + " · 明度", "HSL 八色", -100, 100, 1));
            }
            string[] zoneNames = { "阴影", "中间调", "高光" };
            for (int i = 0; i < Zones.Length; i++)
            {
                string prefix = "grade_" + Zones[i];
                list.Add(Spec(prefix + "_hue", zoneNames[i] + " · 色相（°）", "色彩分级", 0, 360, 1));
                list.Add(Spec(prefix + "_sat", zoneNames[i] + " · 着色强度", "色彩分级", 0, 100, 1));
                list.Add(Spec(prefix + "_lum", zoneNames[i] + " · 明度", "色彩分级", -100, 100, 1));
            }
            for (int i = 0; i < Primaries.Length; i++)
            {
                string prefix = "cal_" + Primaries[i];
                list.Add(Spec(prefix + "_hue", channelNames[i + 1] + "原色 · 色相（°）", "色彩校准", -30, 30, 1));
                list.Add(Spec(prefix + "_sat", channelNames[i + 1] + "原色 · 饱和度", "色彩校准", -100, 100, 1));
            }
            list.Add(Spec("lut_strength", "LUT 强度", "3D LUT", 0, 100, 1));
            for (int i = 0; i < 5; i++)
                list.Add(Spec("lut_ref" + i, "LUT 内容指纹", "LUT 资源", 0, i == 4 ? 281474976710655.0 : 4503599627370495.0, 1));
            foreach(var control in list)if(control.key!=CameraRawModes.FrameKey)GlobalColorKeys.Add(control.key);
            list.AddRange(PortraitEdits.Controls);
            list.AddRange(PortraitControls.Controls);
            list.AddRange(SegmentAssets.Controls);
            list.AddRange(BodyEdits.Controls);
            list.AddRange(LocalEdits.Controls);
            for(int slot=1;slot<=LocalEdits.MaxExtraMasks;slot++)
            {
                list.Add(Spec(LocalEdits.ToneKey(slot,"finish"),"修整强度","独立蒙版",0,100,1));
                list.Add(Spec(LocalEdits.ToneKey(slot,"finefold"),"淡化细褶","独立蒙版",0,100,1));
                list.Add(Spec(LocalEdits.ToneKey(slot,"broadfold"),"淡化粗褶","独立蒙版",0,100,1));
                list.Add(Spec(LocalEdits.ToneKey(slot,"skintone"),"柔和肤色过渡","独立蒙版",0,100,1));
                list.Add(Spec(LocalEdits.ToneKey(slot,"lensglare"),"淡化镜片反光","独立蒙版",0,100,1));
                list.Add(Spec(LocalEdits.ToneKey(slot,"backgroundBlur"),"背景虚化","独立蒙版",0,100,1));
                list.Add(Spec(LocalEdits.ToneKey(slot,"fabricguard"),"保护织纹与亮片","独立蒙版",0,100,1));
            }
            list.Add(Spec("frame_turn","顺时针旋转 90°",FrameGeometry.Group,0,3,1));
            list.Add(Spec("frame_mirror","水平镜像",FrameGeometry.Group,0,1,1));
            list.Add(Spec("frame_angle","拉直角度",FrameGeometry.Group,-45,45,.1));
            list.Add(Spec("frame_ratio","裁切比例",FrameGeometry.Group,0,4,1));
            list.Add(Spec("frame_trim_x","宽度裁减",FrameGeometry.Group,0,99,.1));
            list.Add(Spec("frame_trim_y","高度裁减",FrameGeometry.Group,0,99,.1));
            list.Add(Spec("frame_pan_x","原图水平位置",FrameGeometry.Group,-100,100,.1));
            list.Add(Spec("frame_pan_y","原图垂直位置",FrameGeometry.Group,-100,100,.1));
            for(int i=0;i<FramePerspective.Keys.Length;i++)list.Add(Spec(FramePerspective.Keys[i],FramePerspective.Labels[i],FrameGeometry.Group,0,
                i==8?5:100,i==8?1:.01));
            return list.ToArray();
        }

        private static Dictionary<string, Dictionary<string, double>> MakePresets()
        {
            return new Dictionary<string, Dictionary<string, double>>(StringComparer.Ordinal)
            {
                { "韩系清透", new Dictionary<string, double> { {"exposure", .28}, {"contrast", -10}, {"highlights", -23}, {"shadows", 17}, {"tint", 5}, {"hsl_orange_lum", 12}, {"hsl_green_sat", -15} } },
                { "日系空气感", new Dictionary<string, double> { {"exposure", .32}, {"blacks", 19}, {"highlights", -26}, {"contrast", -17}, {"saturation", -11}, {"curve_blue_25", 15}, {"hsl_green_hue", 12} } },
                { "复古胶片", new Dictionary<string, double> { {"blacks", 24}, {"whites", -15}, {"contrast", 11}, {"temperature", 9}, {"curve_blue_25", 16}, {"curve_blue_75", -13}, {"hsl_green_sat", -25} } },
                { "电影氛围", new Dictionary<string, double> { {"contrast", 17}, {"highlights", -24}, {"shadows", -6}, {"grade_shadows_hue", 185}, {"grade_shadows_sat", 18}, {"grade_highlights_hue", 41}, {"grade_highlights_sat", 13} } },
                { "暖调家庭", new Dictionary<string, double> { {"temperature", 17}, {"exposure", .16}, {"shadows", 16}, {"highlights", -18}, {"vibrance", 9}, {"hsl_orange_lum", 8} } },
                { "商业影棚", new Dictionary<string, double> { {"contrast", 13}, {"whites", 9}, {"blacks", -9}, {"clarity", 15}, {"sharpness", 24}, {"hsl_orange_sat", -7} } },
                { "自然人像", new Dictionary<string, double> { {"exposure", .12}, {"highlights", -17}, {"shadows", 12}, {"hsl_orange_lum", 10}, {"hsl_red_sat", -6}, {"clarity", -5} } },
                { "冷调电影", new Dictionary<string, double> { {"temperature", -16}, {"contrast", 18}, {"blacks", 5}, {"saturation", -12}, {"grade_shadows_hue", 213}, {"grade_shadows_sat", 18}, {"grade_highlights_hue", 32}, {"grade_highlights_sat", 8} } },
                { "胶片人文", new Dictionary<string, double> { {"temperature", 6}, {"contrast", 20}, {"blacks", 14}, {"clarity", 14}, {"curve_green_75", -10}, {"hsl_blue_sat", -18}, {"vibrance", -7} } },
                { "清新户外", new Dictionary<string, double> { {"dehaze", 13}, {"vibrance", 18}, {"highlights", -20}, {"shadows", 13}, {"hsl_green_lum", 11}, {"hsl_blue_hue", -5} } },
                { "暖阳写真", new Dictionary<string, double> { {"temperature", 22}, {"exposure", .20}, {"blacks", 10}, {"hsl_orange_lum", 11}, {"grade_highlights_hue", 46}, {"grade_highlights_sat", 17}, {"hsl_yellow_sat", -9} } },
                { "黑白艺术", new Dictionary<string, double> { {"saturation", -100}, {"contrast", 22}, {"highlights", -12}, {"blacks", -10}, {"clarity", 16}, {"curve_rgb_75", 6} } }
            };
        }
    }
}
