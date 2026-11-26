using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace AIPhotoStudio
{
    internal static class NativeMessages
    {
        private static readonly Lazy<Dictionary<string,string>> Catalog=new Lazy<Dictionary<string,string>>(delegate{
            string path=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"ui","i18n-native-messages.json");
            try{
                return new JavaScriptSerializer().Deserialize<Dictionary<string,string>>(File.ReadAllText(path,Encoding.UTF8))??new Dictionary<string,string>();
            }catch(IOException){}catch(UnauthorizedAccessException){}catch(ArgumentException){}catch(InvalidOperationException){}
            return new Dictionary<string,string>();
        });
        // Only match complete program-owned templates. Text between the fixed portions may be
        // a user's path, model or mask name; keep that value verbatim.
        private static readonly string[][] Templates={
            new[]{"已确认服务可访问，模型 "," 可见。"},
            new[]{"新照片已保存在 ","，未能完成更名。"},
            new[]{"人体检测结果缺少 ","。"},new[]{"每张照片支持 1 至 "," 位人物。"},
            new[]{"导出设置超出允许范围，字段 ","。"},new[]{"请先在蒙版中选中","选区。"},
            new[]{"暂时无法确认交付盘 "," 的位置。请重新连接原盘或恢复映射后再试。"},
            new[]{"字段 "," 超出范围。"},new[]{"字段 "," 的文字无效或过长。"},new[]{"字段 "," 必须为布尔值。"},new[]{"字段 "," 需要有效的非负整数。"},
            new[]{"该文件有 "," 个图像帧；当前支持单张照片。"},new[]{"请核对人脸 "," 的保留位置。本次检测未能唯一匹配，已保留原有位置和调整。"},
            new[]{"服务返回 HTTP ","。请核对模型 ID 与地址，或确认该服务提供模型查询接口。"},new[]{"服务返回 HTTP ","，请检查模型、授权和额度。"},
            new[]{"服务返回 HTTP ","。"},new[]{"参数 "," 已锁定或超出了所选调整范围。"},new[]{"参数 "," 已锁定，请先解锁。"},
            new[]{"请说明 "," 要增加、降低或设为什么数值。"},new[]{""," 超出允许范围，请缩小调整量。"},
            new[]{"选区与已锁定的「","」重叠。可沿用该椭圆拟合未锁参数，或另外圈选。"},
            new[]{"此配方使用图像引擎 ","。请使用兼容版本编辑或导出。"},
            new[]{"出现未定义的字段 ","。"},new[]{"缺少字段 ","。"},new[]{"不支持的调整参数 ","。"},new[]{"调整参数 "," 超出了本次选择的范围。"},
            new[]{"无法打开交付位置 ",""},new[]{"缺少字段 ",""},new[]{"缺少交付设置 ",""},new[]{"无效或超出范围的图像参数 ",""},
            new[]{"编辑历史缺少配套资源：",""},new[]{"编辑资源内容校验失败：",""},new[]{"图库包文件校验失败：",""}
        };
        private static readonly string[] NestedPrefixes={
            "请核对原有人脸和保留位置，本次检测未写入。","当前 Windows 人脸检测组件未能完成识别。","框内没有足够可靠的人脸或人体。","Windows 相机接口："
        };
        private static string Part(string text)
        {
            if(text=="。")return ".";
            string result;return Catalog.Value.TryGetValue(text,out result)?result:text;
        }
        private static string Parameter(string raw)
        {
            var spec=Imaging.Controls.FirstOrDefault(c=>String.Equals(c.label,raw,StringComparison.Ordinal));
            return spec==null?raw:spec.key;
        }
        internal static string Translate(string source,string method=null)
        {
            if(String.IsNullOrEmpty(source))return source;
            string match;
            if(Catalog.Value.TryGetValue(source,out match))return match;
            if(source=="此参数没有可撤销的调整。")return "There is no adjustment to undo for this parameter.";
            if(source=="此参数没有可重做的调整。")return "There is no adjustment to redo for this parameter.";
            const string raw="此 RAW 文件还没有经过相机机型、压缩方式与位深验证的完整传感器解码器。请导入相机或原厂软件导出的 TIFF、PNG 或 JPEG。";
            if(source.StartsWith("已识别机型 ",StringComparison.Ordinal)&&source.EndsWith("。"+raw,StringComparison.Ordinal))
                return "Detected camera: "+source.Substring(6,source.Length-7-raw.Length)+". "+Part(raw);
            const string dng="该 DNG 不符合本机已验证的传感器处理条件：";
            if(source.StartsWith(dng,StringComparison.Ordinal)&&source.EndsWith("。",StringComparison.Ordinal))
                return Part(dng)+Part(source.Substring(dng.Length,source.Length-dng.Length-1))+".";
            foreach(string role in new[]{"头发","肤色","背景"}){
                const string few="取样范围内可见像素太少，请扩大选区或避开透明处。";
                if(source==role+few)return Part(role)+": "+Part(few);
            }
            foreach(var pair in Templates){
                string first=pair[0],last=pair[1];
                if(source.Length<=first.Length+last.Length||!source.StartsWith(first,StringComparison.Ordinal)||!source.EndsWith(last,StringComparison.Ordinal))continue;
                string value=source.Substring(first.Length,source.Length-first.Length-last.Length);
                if(first=="参数 "||first=="请说明 "||first=="")value=Parameter(value);
                if(first=="请先在蒙版中选中")value=Part(value);
                return Part(first)+value+Part(last);
            }
            foreach(string prefix in NestedPrefixes){
                if(source.StartsWith(prefix,StringComparison.Ordinal))return Part(prefix)+" "+Translate(source.Substring(prefix.Length).TrimStart());
            }
            if(source.StartsWith("相机照片已保存到 ",StringComparison.Ordinal)){
                const string prefix="相机照片已保存到 ",middle="。图库导入失败：";int i=source.LastIndexOf(middle,StringComparison.Ordinal);
                if(i>prefix.Length)return Part(prefix)+source.Substring(prefix.Length,i-prefix.Length)+Part(middle)+Translate(source.Substring(i+middle.Length));
            }
            foreach(string stage in new[]{"识别相机 RAW","读取完整感光数据","去马赛克与色彩转换","生成整幅 16 位图像"}){
                if(source.StartsWith(stage+"失败：",StringComparison.Ordinal))return Part(stage)+" failed: "+source.Substring(stage.Length+3);
            }
            return source;
        }
    }
}
