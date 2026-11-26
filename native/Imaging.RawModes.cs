using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace AIPhotoStudio
{
    internal static class CameraRawModes
    {
        internal const string FrameKey="raw_frame";
        internal static int Selected(IDictionary<string,double> values)
        {
            double frame;
            if(values==null||!values.TryGetValue(FrameKey,out frame)||frame==0)return 0;
            if(frame==1)return 1;
            throw new ArgumentOutOfRangeException(FrameKey,"感光轨编号无效。");
        }
        internal static Dictionary<string,double> Neutral(IDictionary<string,double> values)
        {
            var neutral=new Dictionary<string,double>();
            int selected=Selected(values);
            if(selected!=0)neutral.Add(FrameKey,selected);
            return neutral;
        }
        internal sealed class Profile
        {
            internal readonly string Ext,Make,Model,Decoder,Cfa,Description;
            internal readonly int Id,Bits,SensorW,SensorH,Width,Height,Left,Top,CropX,CropY,CropW,CropH,Frames,Mode,Selector;
            internal Profile(string ext,string make,string model,string decoder,string cfa,string description,int id,int bits,
                int sw,int sh,int w,int h,int left,int top,int cx,int cy,int cw,int ch,int frames,int mode,int selector)
            {
                Ext=ext;Make=make;Model=model;Decoder=decoder;Cfa=cfa;Description=description;Id=id;Bits=bits;
                SensorW=sw;SensorH=sh;Width=w;Height=h;Left=left;Top=top;CropX=cx;CropY=cy;CropW=cw;CropH=ch;
                Frames=frames;Mode=mode;Selector=selector;
            }
        }
        internal static readonly Profile[] All={
            new Profile("NEF","Nikon","Z 6","nikon_load_raw()","b4b4b4b4","Nikon 压缩，NEFCompression 4",3581,14,6064,4040,6064,4040,0,0,8,8,6048,4024,1,4,0),
            new Profile("NRW","Nikon","COOLPIX P1000","packed_load_raw()","b4b4b4b4","12 位打包，无压缩",3381,12,4624,3470,4624,3470,0,0,65535,65535,0,0,1,0,0),
            new Profile("CR2","Canon","EOS 5D Mark IV","lossless_jpeg_load_raw()","b4b4b4b4","标准无损 JPEG RAW，单感光帧",983,14,6880,4544,6744,4502,136,42,148,54,6720,4480,1,4,0),
            new Profile("CR3","Canon","EOS R","crxLoadRaw()","b4b4b4b4","CRX，两个全尺寸感光轨",4611,14,6888,4546,6742,4498,146,48,156,58,6720,4480,2,3,0),
            new Profile("CRW","Canon","EOS D60","canon_load_raw()","b4b4b4b4","CIFF 原生 Canon 压缩",2010,12,3152,2068,3088,2056,64,12,72,16,3072,2048,1,4,0),
            new Profile("ARW","Sony","ILCE-7M3","sony_arw2_load_raw()","b4b4b4b4","Sony ARW2 有损压缩",2414,14,6048,4024,6024,4024,0,0,12,12,6000,4000,1,2,3330),
            new Profile("SR2","Sony","DSC-R1","unpacked_load_raw()","e1e1e1e1","无压缩 14 位",3221,14,3984,2608,3925,2608,0,0,65535,65535,0,0,1,0,1000),
            new Profile("SRF","Sony","DSC-F828","sony_load_raw()","63636363","Sony SRF，四通道 RGBE",1351,14,3360,2460,3287,2460,6,0,65535,65535,0,0,1,65535,0),
            new Profile("RAF","Fujifilm","X-T4","fuji_compressed_load_raw()","00000009","Fuji 压缩 X-Trans",3918,14,6384,4182,6246,4170,0,6,6,18,6240,4155,1,0,0),
            new Profile("RAF","Fujifilm","X-A3","fuji_14bit_load_raw()","b4b4b4b4","Fujifilm 14 位无压缩 Bayer",1371,14,6016,4014,6016,4014,0,0,8,8,6000,3999,1,0,0),
            new Profile("RW2","Panasonic","DC-G9","panasonic_load_raw()","b4b4b4b4","Panasonic RAW 1，4:3 单张",3219,12,5264,3904,5208,3904,0,0,12,8,5184,3888,1,34316,0),
            new Profile("RW2","Panasonic","DMC-LX100","panasonic_load_raw()","1e1e1e1e","Panasonic RAW 1，16:9 感光区",1137,12,4592,2536,4496,2536,0,0,8,8,4480,2520,1,34316,0),
            new Profile("RW2","Panasonic","DMC-LX100","panasonic_load_raw()","1e1e1e1e","Panasonic RAW 1，1:1 感光区",1139,12,3248,3104,3104,3104,0,0,8,8,3088,3088,1,34316,0),
            new Profile("ORF","Olympus","OM-1","olympus_load_raw()","b4b4b4b4","Olympus 压缩，普通 20 MP 单张",5283,12,5220,3912,5220,3912,0,0,12,12,5184,3888,1,0,0),
            new Profile("ORF","Olympus","OM-1","olympus_load_raw()","b4b4b4b4","Olympus 压缩，手持 50 MP 机内合成",5284,12,8180,6132,8170,6132,0,0,6,6,8160,6120,1,11,12),
            new Profile("ORF","Olympus","E-M5","olympus_load_raw()","b4b4b4b4","Olympus 压缩，普通单张",1084,12,4640,3472,4640,3472,0,0,8,8,4608,3456,1,-1,0),
            new Profile("DNG","Ricoh","GR III","lossless_dng_load_raw()","b4b4b4b4","原生 DNG 1.2，无损 JPEG CFA",3115,14,6112,4064,6020,4024,58,28,68,40,6000,4000,1,0x01020000,0)
        };
        internal static Profile Find(string kind)
        {
            foreach(var p in All)if(String.Equals(p.Ext,kind,StringComparison.OrdinalIgnoreCase))return p;
            return null;
        }
        private static int N(IDictionary<string,object> m,string key){return Convert.ToInt32(m[key],CultureInfo.InvariantCulture);}
        private static bool StandardSingleCfa(Dictionary<string,object> m,Profile family)
        {
            if(family==null||family.Make!=(string)m["cameraManufacturer"]||N(m,"frameCount")!=1||N(m,"rawColors")!=3||
                N(m,"rawHeaderBits")<10||N(m,"rawHeaderBits")>16||N(m,"whiteLevel")<255||!(bool)m["hasCameraMatrix"])return false;
            int w=N(m,"storedWidth"),h=N(m,"storedHeight"),sw=N(m,"sensorWidth"),sh=N(m,"sensorHeight");
            if(w<16||h<16||sw<16||sh<16||(long)sw*sh>80000000||w+N(m,"leftMargin")>sw||h+N(m,"topMargin")>sh)return false;
            string model=(string)m["cameraModel"],cfa=(string)m["cfaPattern"],decoder=(string)m["unpackFunction"];
            if(String.IsNullOrWhiteSpace(model)||!(cfa.Length==4||cfa.Length==36)||cfa.IndexOf('R')<0||cfa.IndexOf('G')<0||cfa.IndexOf('B')<0)return false;
            foreach(char ink in cfa)if(ink!='R'&&ink!='G'&&ink!='B')return false;
            foreach(var known in All)if(known.Make==family.Make&&known.Model==model&&known.Ext!=family.Ext)return false;
            switch(family.Ext)
            {
                case "CR2":return decoder=="lossless_jpeg_load_raw()";
                case "CR3":return decoder=="crxLoadRaw()"&&m.ContainsKey("primaryTrackEncoding");
                case "CRW":return decoder=="canon_load_raw()";
                case "RAF":return decoder=="fuji_compressed_load_raw()"||decoder=="unpacked_load_raw()"||decoder=="fuji_14bit_load_raw()";
                case "RW2":return decoder=="panasonic_load_raw()"&&N(m,"manufacturerMode")==34316&&N(m,"selector")==0;
                case "ORF":return decoder=="olympus_load_raw()"&&(N(m,"manufacturerMode")==0||N(m,"manufacturerMode")==-1)&&N(m,"selector")==0;
                case "NEF":return !model.StartsWith("COOLPIX",StringComparison.OrdinalIgnoreCase)&&
                    (decoder=="nikon_load_raw()"||decoder=="packed_load_raw()"||decoder=="unpacked_load_raw()"||decoder=="lossless_jpeg_load_raw()");
                case "NRW":return model.StartsWith("COOLPIX",StringComparison.OrdinalIgnoreCase)&&decoder=="packed_load_raw()";
                case "SR2":return N(m,"selector")==1000&&decoder=="unpacked_load_raw()";
                case "ARW":return N(m,"selector")>=2000&&N(m,"selector")<=5999&&
                    (decoder=="sony_arw2_load_raw()"||decoder=="sony_arw_load_raw()"||decoder=="unpacked_load_raw()"||
                    decoder=="lossless_jpeg_load_raw()"||decoder=="sony_ljpeg_load_raw()");
                default:return false;
            }
        }
        private static bool Matches(Profile p,Dictionary<string,object> meta)
        {
            return p.Ext==(string)meta["format"]&&p.Make==(string)meta["cameraManufacturer"]&&p.Model==(string)meta["cameraModel"]&&
                p.Decoder==(string)meta["unpackFunction"]&&p.Cfa==(string)meta["cfaFilters"]&&
                p.Bits==N(meta,"rawHeaderBits")&&p.SensorW==N(meta,"sensorWidth")&&p.SensorH==N(meta,"sensorHeight")&&
                p.Width==N(meta,"activeWidth")&&p.Height==N(meta,"activeHeight")&&p.Left==N(meta,"leftMargin")&&p.Top==N(meta,"topMargin")&&
                p.CropX==N(meta,"insetLeft")&&p.CropY==N(meta,"insetTop")&&p.CropW==N(meta,"insetWidth")&&p.CropH==N(meta,"insetHeight")&&
                p.Frames==N(meta,"frameCount")&&p.Mode==N(meta,"manufacturerMode")&&p.Selector==N(meta,"selector")&&
                (p.Ext!="CR3"||(string)meta["trackEncodings"]=="0e400380,0e400380")&&
                (p.Ext!="ORF"||N(meta,"makerValidBits")==p.Bits);
        }
        internal static void Verify(Dictionary<string,object> meta)
        {
            int frames=N(meta,"frameCount"),selected=N(meta,"selectedRawFrame");
            if(selected<0||selected>=frames)throw new NotSupportedException("所选感光轨超出该文件的范围。");
            Profile p=Array.Find(All,profile=>Matches(profile,meta));
            if(p==null)
            {
                if(!StandardSingleCfa(meta,Find((string)meta["format"])))throw new NotSupportedException("此相机的感光阵列、裁切或多帧模式尚未验证，且未通过通用单帧检查。");
                meta["cameraModeVerified"]=false;meta["encodingProfileVerified"]=false;
                meta["cameraValidation"]="此模式未经样片认证，已按通用单帧 CFA 检查。";
                meta["compressionMode"]=meta["unpackFunction"];
                return;
            }
            meta["cameraModeVerified"]=true;meta["encodingProfileVerified"]=true;meta["referenceSampleId"]=p.Id;
            meta["cameraValidation"]="已通过相同机型和编码组合的公开样片。";
            meta["compressionMode"]=p.Description;
            if(p.Make=="Olympus"&&p.Model=="OM-1")meta["displayManufacturer"]="OM System";
        }
    }
}
