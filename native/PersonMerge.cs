using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace AIPhotoStudio
{
    internal sealed class PersonDetectionDraft
    {
        internal sealed class Asset<T>{internal byte[] Bytes;internal T Scene;}
        internal readonly Dictionary<string,Asset<BodyScene>> Bodies=new Dictionary<string,Asset<BodyScene>>();
        internal readonly Dictionary<string,Asset<PortraitScene>> Faces=new Dictionary<string,Asset<PortraitScene>>();

        // Intermediate and cancelled detections never create photo resources on disk.
        internal void StoreSelected(IDictionary<string,double> patch)
        {
            string body=BodyAssets.ResourceHash(patch),face=PortraitAssets.Hash(patch);
            if(body!=null&&Bodies.ContainsKey(body))BodyAssets.Import(Bodies[body].Bytes,body);
            if(face!=null&&Faces.ContainsKey(face))PortraitAssets.Store(Faces[face].Bytes,face);
        }
    }

    public static partial class BodyAssets
    {
        private static bool SameHeadAndShoulders(BodyGeometry a,BodyGeometry b)
        {
            if(!a.Visible[0]||!b.Visible[0]||!a.Visible[11]||!b.Visible[11]||!a.Visible[12]||!b.Visible[12])return false;
            double breadth=Math.Min(BodyGeometry.Distance(a.Points[11],a.Points[12]),BodyGeometry.Distance(b.Points[11],b.Points[12]));
            return breadth>.012 && BodyGeometry.Distance(a.Points[0],b.Points[0])<.30*breadth &&
                BodyGeometry.Distance(a.Points[11],b.Points[11])<.45*breadth && BodyGeometry.Distance(a.Points[12],b.Points[12])<.45*breadth;
        }

        internal static Dictionary<string,double> Merge(IDictionary<string,double> before,Dictionary<string,double> found,IEnumerable<string> locks,out int[] slots,PersonDetectionDraft draft=null)
        {
            var incoming=ResolveScene(found,draft);
            slots=Enumerable.Range(0,incoming.Subjects.Length).ToArray();
            var old=ResolveScene(before);
            if(old==null||old.SourceHash!=incoming.SourceHash)return found;
            if(ResourceHash(before)==ResourceHash(found))return found;
            var protectedSlots=new HashSet<int>(before.Where(p=>p.Value!=0).Select(p=>p.Key).Concat(locks).Select(BodyEdits.PersonIndex).Where(i=>i>=0));
            if(old.Subjects[0].Legacy)
            {
                if(protectedSlots.Count>0)
                {
                    if(incoming.Subjects.Length==1&&old.Subjects[0].SamePerson(incoming.Subjects[0]))return Patch(ResourceHash(before));
                    throw new InvalidOperationException("旧版人体配方已有调整或锁定，无法安全合并新增人物。可改用手绘蒙版。");
                }
                return found;
            }
            var previousBytes=Read(before);
            var newBytes=Read(found,draft);
            var rows=Enumerable.Range(0,old.Subjects.Length).Select(i=>new ArraySegment<byte>(previousBytes,40+i*SubjectBytes,SubjectBytes)).ToList();
            var occupied=new HashSet<int>();
            bool changed=false;
            for(int n=0;n<incoming.Subjects.Length;n++)
            {
                var candidate=incoming.Subjects[n];
                var matches=Enumerable.Range(0,old.Subjects.Length).Where(i=>old.Subjects[i].SamePerson(candidate) ||
                    SameHeadAndShoulders(old.Subjects[i],candidate)).ToArray();
                var bytes=new ArraySegment<byte>(newBytes,40+n*SubjectBytes,SubjectBytes);
                if(matches.Length>1 || (matches.Length==1&&!occupied.Add(matches[0])))
                    throw new InvalidOperationException("重叠人物的肩部或头部难以区分，请缩小补检范围。");
                if(matches.Length==0){
                    bool uncertain=old.Subjects.Any(other=>other.Visible[0]&&candidate.Visible[0]&&
                        BodyGeometry.Distance(other.Points[0],candidate.Points[0])<.36*Math.Min(
                            BodyGeometry.Distance(other.Points[11],other.Points[12]),BodyGeometry.Distance(candidate.Points[11],candidate.Points[12])));
                    if(uncertain)throw new InvalidOperationException("检测到的头部与已有人物接近，但肩部定位不同。请缩小补检范围。");
                    slots[n]=rows.Count;rows.Add(bytes);changed=true;continue;
                }
                int slot=matches[0];slots[n]=slot;
                var previous=old.Subjects[slot];
                bool upgrade=Enumerable.Range(0,previous.Available.Length).All(i=>!previous.Available[i]||candidate.Available[i]) &&
                    candidate.Available.Count(v=>v)>previous.Available.Count(v=>v);
                if(upgrade&&!protectedSlots.Contains(slot)&&previous.SamePerson(candidate)){rows[slot]=bytes;changed=true;}
            }
            if(!changed)return Patch(ResourceHash(before));
            if(rows.Count>BodyEdits.MaxPeople)throw new InvalidOperationException("已达到单张照片的人物数量上限。");
            using(var buffer=new MemoryStream())
            using(var writer=new BinaryWriter(buffer)){
                writer.Write(previousBytes,0,36);writer.Write(rows.Count);
                foreach(var row in rows)writer.Write(row.Array,row.Offset,row.Count);
                var bytes=buffer.ToArray();
                using(var sha=SHA256.Create())return Store(bytes,Hex(sha.ComputeHash(bytes)),draft);
            }
        }
    }

    internal static partial class PortraitAssets
    {
        private static bool RetainsSpotGeometry(PortraitScene before,PortraitScene after)
        {
            if(after==null||before.SourceHash!=after.SourceHash)return false;
            return before.Spots.All(s=>s.face<after.Faces.Length &&
                before.Faces[s.face].Points.SequenceEqual(after.Faces[s.face].Points) &&
                after.Spots.Any(t=>t.face==s.face&&t.x==s.x&&t.y==s.y&&t.radius==s.radius));
        }
        private static bool RetainsKeepGeometry(PortraitScene before,PortraitScene after)
        {
            if(after==null||before.SourceHash!=after.SourceHash)return false;
            return before.KeepMarks.All(s=>s.face<after.Faces.Length&&
                before.Faces[s.face].Points.SequenceEqual(after.Faces[s.face].Points)&&
                after.KeepMarks.Any(t=>t.face==s.face&&t.x==s.x&&t.y==s.y&&t.radius==s.radius));
        }
        internal static void ConfirmKeptFaces(PortraitScene before,PortraitScene detected)
        {
            if(before==null||before.KeepMarks.Length==0)return;
            foreach(int index in before.KeepMarks.Select(p=>p.face).Distinct()){
                var face=before.Faces[index];
                var matches=detected.Faces.Where(f=>face.SamePosition(f)&&f.SamePosition(face)).ToArray();
                if(matches.Length!=1||before.Faces.Count(f=>f.SamePosition(matches[0]))!=1)
                    throw new InvalidOperationException("请核对人脸 "+(index+1)+" 的保留位置。本次检测未能唯一匹配，已保留原有位置和调整。");
            }
        }

        internal static Dictionary<string,double> Merge(IDictionary<string,double> before,Dictionary<string,double> found,out int[] slots,PersonDetectionDraft draft=null)
        {
            var incoming=Resolve(found,draft);
            slots=Enumerable.Range(0,incoming.Faces.Length).ToArray();
            var old=Resolve(before);
            if(old==null || old.SourceHash!=incoming.SourceHash)return found;
            var indices=new List<int>();
            var occupied=new HashSet<int>();
            for(int n=0;n<incoming.Faces.Length;n++)
            {
                var candidate=incoming.Faces[n];
                var matches=Enumerable.Range(0,old.Faces.Length).Where(i=>old.Faces[i].SamePosition(candidate)).ToArray();
                if(matches.Length>1 || (matches.Length==1&&!occupied.Add(matches[0])))
                    throw new InvalidOperationException("多张脸的关键点过于接近，请缩小补检范围。");
                if(matches.Length==1){slots[n]=matches[0];continue;}
                if(old.Faces.Any(f=>Math.Abs(f.Points[1].X-candidate.Points[1].X)<.36*Math.Min(f.Box.w,candidate.Box.w) &&
                    Math.Abs(f.Points[1].Y-candidate.Points[1].Y)<.30*Math.Min(f.Box.h,candidate.Box.h)))
                    throw new InvalidOperationException("检测到的脸与已有人脸位置接近，但五官无法可靠对应。请缩小框选范围。");
                slots[n]=old.Faces.Length+indices.Count;indices.Add(n);
            }
            if(indices.Count==0)return BodyAssets.Patch(Hash(before),"portrait_ref");
            if(old.Faces.Length+indices.Count>PortraitControls.MaxFaces)throw new InvalidOperationException("已达到单张照片的人脸数量上限。");
            byte[] previous=Read(before),more=Read(found,draft);
            using(var buffer=new MemoryStream())
            using(var writer=new BinaryWriter(buffer)){
                writer.Write(0x314c5350);writer.Write(previous,4,HeaderBytes-6);writer.Write((ushort)(old.Faces.Length+indices.Count));
                writer.Write(previous,HeaderBytes,old.Faces.Length*MeshBytes);
                foreach(int n in indices)writer.Write(more,HeaderBytes+n*MeshBytes,MeshBytes);
                var bytes=buffer.ToArray();var merged=Store(bytes,Digest(bytes),draft);
                return old.Spots.Length==0&&old.KeepMarks.Length==0?merged:WriteEdits(merged,old.Spots,old.KeepMarks,draft);
            }
        }
    }
}
