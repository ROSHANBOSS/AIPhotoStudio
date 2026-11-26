using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Windows;

namespace AIPhotoStudio
{
    // A PSL1 record holds the source SHA-256, inference image size and 478 pairs of unsigned coordinates per face.
    internal static partial class PortraitAssets
    {
        internal const string Model="mediapipe-face-landmarker-fp16-1";
        private const int MeshBytes=478*4, HeaderBytes=42;
        internal const int MaxBytes=HeaderBytes+PortraitControls.MaxFaces*MeshBytes+4+(PortraitSpots.Limit+PortraitKeep.Limit)*7;
        private static readonly object Gate=new object();
        private static string folder;
        private static readonly Dictionary<string,PortraitScene> Scenes=new Dictionary<string,PortraitScene>();
        internal static void Configure(string directory)
        {
            lock(Gate){folder=Path.GetFullPath(directory);Directory.CreateDirectory(folder);Scenes.Clear();}
        }
        internal static string Hash(IDictionary<string,double> values){return BodyAssets.ResourceHash(values,"portrait_ref");}
        private static string Digest(byte[] bytes)
        {
            using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","").ToLowerInvariant();
        }
        internal static Dictionary<string,double> Save(string sourceHash,Dictionary<string,object> result,PersonDetectionDraft draft=null)
        {
            var items=result["meshes"] as IEnumerable;
            if(items==null || items is string || !(result["width"] is int) || !(result["height"] is int))
                throw new InvalidDataException("人脸检测数据格式无效。");
            int width=(int)result["width"],height=(int)result["height"];
            if(width<1||height<1||width>1600||height>1600)throw new InvalidDataException("人脸检测尺寸无效。");
            var meshes=new List<byte[]>();
            foreach(object item in items){
                string encoded=item as string;
                if(meshes.Count>=PortraitControls.MaxFaces || encoded==null || encoded.Length!=4*((MeshBytes+2)/3))
                    throw new InvalidDataException("人脸关键点数量或编码长度无效。");
                byte[] bytes=Convert.FromBase64String(encoded);
                if(bytes.Length!=MeshBytes)throw new InvalidDataException("每张脸需要 478 个关键点。");
                meshes.Add(bytes);
            }
            byte[] source=Convert.FromBase64String(sourceHash);
            if(source.Length!=32 || meshes.Count==0)throw new InvalidDataException("没有检测到可编辑的人脸。");
            using(var buffer=new MemoryStream())
            using(var writer=new BinaryWriter(buffer)){
                writer.Write(0x314c5350);writer.Write(source);writer.Write((ushort)width);writer.Write((ushort)height);
                writer.Write((ushort)meshes.Count);foreach(var mesh in meshes)writer.Write(mesh);
                byte[] record=buffer.ToArray();return Store(record,Digest(record),draft);
            }
        }
        private static PortraitScene Decode(byte[] bytes,string hash)
        {
            if(bytes==null || bytes.Length<HeaderBytes+MeshBytes || bytes.Length>MaxBytes || Digest(bytes)!=hash)
                throw new InvalidDataException("人脸关键点文件长度或摘要无效。");
            using(var reader=new BinaryReader(new MemoryStream(bytes,false))){
                int header=reader.ReadInt32();
                if(header!=0x314c5350 && header!=0x324c5350 && header!=0x334c5350)throw new InvalidDataException("未知的人脸检测格式。");
                var scene=new PortraitScene{SourceHash=Convert.ToBase64String(reader.ReadBytes(32))};
                int width=reader.ReadUInt16(),height=reader.ReadUInt16(),count=reader.ReadUInt16();
                int geometrySize=HeaderBytes+count*MeshBytes;
                if(width<1||height<1||width>1600||height>1600||count<1||count>PortraitControls.MaxFaces||
                   bytes.Length<geometrySize || (header==0x314c5350 && bytes.Length!=geometrySize))
                    throw new InvalidDataException("人脸关键点数据不完整。");
                scene.Faces=new FaceMesh[count];
                for(int n=0;n<count;n++){
                    var mesh=new FaceMesh{Points=new Point[478]};
                    for(int i=0;i<mesh.Points.Length;i++)mesh.Points[i]=new Point(reader.ReadUInt16()/65535d,reader.ReadUInt16()/65535d);
                    mesh.Measure(width,height);
                    for(int j=0;j<n;j++)if(mesh.SamePosition(scene.Faces[j]))throw new InvalidDataException("两组关键点属于同一张脸。");
                    scene.Faces[n]=mesh;
                }
                if(header!=0x314c5350){
                    if(bytes.Length<geometrySize+2)throw new InvalidDataException("缺少点修数据。");
                    int total=reader.ReadUInt16();
                    if(total>PortraitSpots.Limit||bytes.Length<geometrySize+2+7*total||
                        header==0x324c5350&&(total==0||bytes.Length!=geometrySize+2+7*total))throw new InvalidDataException("点修数量或尺寸无效。");
                    scene.Spots=new PortraitSpot[total];
                    for(int i=0;i<total;i++){
                        var spot=ReadPoint(reader);
                        if(!PortraitSpots.Safe(scene,spot,width,height))throw new InvalidDataException("点修范围超出可用的皮肤区域。");
                        scene.Spots[i]=spot;
                    }
                    if(header==0x334c5350){
                        if(bytes.Length<reader.BaseStream.Position+2)throw new InvalidDataException("缺少人脸保留位置。");
                        int keep=reader.ReadUInt16();
                        if(keep<1||keep>PortraitKeep.Limit||bytes.Length!=reader.BaseStream.Position+7*keep)throw new InvalidDataException("人脸保留位置数量或尺寸无效。");
                        scene.KeepMarks=new PortraitSpot[keep];
                        for(int i=0;i<keep;i++){
                            var point=ReadPoint(reader);
                            if(!PortraitKeep.Safe(scene,point))throw new InvalidDataException("保留位置超出所选人脸，或与另一张脸重叠。");
                            scene.KeepMarks[i]=point;
                        }
                    }
                    byte[] baseMesh=new byte[geometrySize];Buffer.BlockCopy(bytes,0,baseMesh,0,geometrySize);baseMesh[3]=(byte)'1';
                    scene.GeometryHash=Digest(baseMesh);
                }else scene.GeometryHash=hash;
                return scene;
            }
        }
        internal static Dictionary<string,double> WithSpots(IDictionary<string,double> values,PortraitSpot[] additions,PersonDetectionDraft draft=null)
        {
            var scene=Resolve(values,draft);
            var spots=scene.Spots.Concat(additions).ToArray();
            if(spots.Length>PortraitSpots.Limit)throw new InvalidOperationException("单张照片最多保存 96 处自动查找的点修。");
            return WriteEdits(values,spots,scene.KeepMarks,draft);
        }
        internal static bool HasKeepMarks(IDictionary<string,double> values)
        {
            var scene=Resolve(values);return scene!=null&&scene.KeepMarks.Length!=0;
        }
        private static PortraitSpot ReadPoint(BinaryReader reader)
        {
            return new PortraitSpot{face=reader.ReadByte(),x=reader.ReadUInt16()/65535d,y=reader.ReadUInt16()/65535d,radius=reader.ReadUInt16()/65535d};
        }
        private static void WritePoints(BinaryWriter writer,PortraitSpot[] points)
        {
            writer.Write((ushort)points.Length);
            foreach(var spot in points){
                writer.Write((byte)spot.face);writer.Write((ushort)Math.Round(65535*spot.x));writer.Write((ushort)Math.Round(65535*spot.y));
                writer.Write((ushort)Math.Round(65535*spot.radius));
            }
        }
        internal static Dictionary<string,double> WriteEdits(IDictionary<string,double> values,PortraitSpot[] spots,PortraitSpot[] keep,PersonDetectionDraft draft=null)
        {
            var scene=Resolve(values,draft);
            if(keep.Length>PortraitKeep.Limit||keep.Any(p=>!PortraitKeep.Safe(scene,p)))throw new InvalidDataException("保留位置过多、超出所选人脸，或与另一张脸重叠。");
            var mesh=Read(values,draft);
            using(var buffer=new MemoryStream())
            using(var writer=new BinaryWriter(buffer)){
                writer.Write(keep.Length>0?0x334c5350:spots.Length>0?0x324c5350:0x314c5350);
                writer.Write(mesh,4,HeaderBytes+scene.Faces.Length*MeshBytes-4);
                if(keep.Length>0||spots.Length>0)WritePoints(writer,spots);
                if(keep.Length>0)WritePoints(writer,keep);
                byte[] bytes=buffer.ToArray();return Store(bytes,Digest(bytes),draft);
            }
        }
        internal static Dictionary<string,double> Store(byte[] bytes,string expected,PersonDetectionDraft draft=null)
        {
            var scene=Decode(bytes,expected);
            if(draft!=null){draft.Faces[expected]=new PersonDetectionDraft.Asset<PortraitScene>{Bytes=bytes,Scene=scene};return BodyAssets.Patch(expected,"portrait_ref");}
            lock(Gate){
                string file=Path.Combine(folder,expected+".mesh");
                if(!File.Exists(file)){
                    string tmp=Path.Combine(folder,Guid.NewGuid().ToString("N")+".tmp");
                    try{File.WriteAllBytes(tmp,bytes);try{File.Move(tmp,file);}catch(IOException){if(!File.Exists(file))throw;}}
                    finally{if(File.Exists(tmp))File.Delete(tmp);}
                }
                if(!File.Exists(file)||Digest(ReadFile(expected))!=expected)throw new InvalidDataException("人脸关键点文件已损坏。");
                Remember(expected,scene);
            }
            return BodyAssets.Patch(expected,"portrait_ref");
        }
        private static void Remember(string hash,PortraitScene scene)
        {
            if(Scenes.Count>=12&&!Scenes.ContainsKey(hash))Scenes.Remove(Scenes.Keys.First());
            Scenes[hash]=scene;
        }
        private static byte[] ReadFile(string hash)
        {
            string path=Path.Combine(folder,hash+".mesh");
            if(new FileInfo(path).Length>MaxBytes)throw new InvalidDataException("人脸关键点文件过大。");
            return File.ReadAllBytes(path);
        }
        internal static byte[] Read(IDictionary<string,double> values,PersonDetectionDraft draft=null)
        {
            var hash=Hash(values);
            if(draft!=null&&hash!=null&&draft.Faces.ContainsKey(hash))return draft.Faces[hash].Bytes;
            var bytes=ReadFile(hash);Decode(bytes,hash);return bytes;
        }
        internal static PortraitScene Resolve(IDictionary<string,double> values,PersonDetectionDraft draft=null)
        {
            var hash=Hash(values);if(hash==null)return null;
            if(draft!=null&&draft.Faces.ContainsKey(hash))return draft.Faces[hash].Scene;
            lock(Gate){
                PortraitScene found;
                if(!Scenes.TryGetValue(hash,out found)){found=Decode(ReadFile(hash),hash);Remember(hash,found);}
                return found;
            }
        }
        internal static BodySubjectInfo[] Describe(IDictionary<string,double> values)
        {
            PortraitScene scene;
            try{scene=Resolve(values);}catch(IOException){return new BodySubjectInfo[0];}catch(InvalidDataException){return new BodySubjectInfo[0];}
            return scene==null?new BodySubjectInfo[0]:scene.Faces.Select((face,i)=>new BodySubjectInfo{index=i,box=face.Box,
                available=PortraitControls.Keys.Where(face.Available).Select(k=>PortraitControls.Key(i,k)).ToArray()}).ToArray();
        }
        internal static bool CanAdjust(IDictionary<string,double> values,string key)
        {
            int index=PortraitControls.Index(key);var scene=Resolve(values);
            return scene!=null && index>=0 && index<scene.Faces.Length && scene.Faces[index].Available(PortraitControls.Suffix(key));
        }
        internal static bool CanReplace(IDictionary<string,double> before,IDictionary<string,double> after,IEnumerable<string> locks,PersonDetectionDraft draft=null)
        {
            var protectedKeys=before.Where(p=>p.Value!=0).Select(p=>p.Key).Concat(locks??new string[0]).Where(k=>PortraitControls.Index(k)>=0).Distinct().ToArray();
            if(Hash(before)==Hash(after))return true;
            var old=Resolve(before);var next=Resolve(after,draft);
            if(old!=null&&old.Spots.Length>0&&(next==null||old.GeometryHash!=next.GeometryHash)&&!RetainsSpotGeometry(old,next))return false;
            if(old!=null&&old.KeepMarks.Length>0&&(next==null||old.GeometryHash!=next.GeometryHash)&&!RetainsKeepGeometry(old,next))return false;
            if(protectedKeys.Length==0)return true;
            if(old==null||next==null||old.SourceHash!=next.SourceHash)return false;
            foreach(var key in protectedKeys){
                int i=PortraitControls.Index(key);
                if(i>=old.Faces.Length||i>=next.Faces.Length||!next.Faces[i].Available(PortraitControls.Suffix(key))||
                    !old.Faces[i].SamePosition(next.Faces[i])||next.Faces.Count(old.Faces[i].SamePosition)!=1)return false;
            }
            return true;
        }
        internal static void ValidateFile(string path,IDictionary<string,double> values,bool spotDraft=false)
        {
            if(Hash(values)==null && !PortraitControls.Active(values) && !spotDraft)return;
            var scene=Resolve(values);
            if(scene!=null && scene.Spots.Length==0 && scene.KeepMarks.Length==0 && !PortraitControls.Active(values) && !spotDraft)return;
            if(scene==null || scene.SourceHash!=Convert.ToBase64String(HexBytes(BodyAssets.SourceDigest(path))))
                throw new InvalidOperationException("原片与已保存的人脸关键点不符，请重新识别。");
        }
        private static byte[] HexBytes(string hex)
        {
            var bytes=new byte[hex.Length/2];
            for(int i=0;i<bytes.Length;i++)bytes[i]=Convert.ToByte(hex.Substring(2*i,2),16);
            return bytes;
        }
    }
}
