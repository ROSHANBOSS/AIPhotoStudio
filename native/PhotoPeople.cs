using System;
using System.Collections.Generic;
using System.Linq;

namespace AIPhotoStudio
{
    internal sealed class PhotoPerson
    {
        public int index {get;set;}
        public int? bodyIndex {get;set;}
        public int? faceIndex {get;set;}
    }

    internal static class PhotoPeople
    {
        // Two detectors can return different counts and different orders. A body rectangle
        // may contain several heads in a group photo, so use the measured nose and support
        // in that person's own mask. Foreground overlap alone does not establish identity.
        internal static double FaceDistance(BodyGeometry body,FaceMesh face)
        {
            var nose=body.Points[0];
            if(!body.Visible[0] || BodyAssets.Probability(body.Mask,nose.X,nose.Y)<.45)return Double.PositiveInfinity;
            var target=face.Points[1];
            if(BodyAssets.Probability(body.Mask,target.X,target.Y)<.35)return Double.PositiveInfinity;
            double dx=(nose.X-target.X)/face.Box.w,dy=(nose.Y-target.Y)/face.Box.h;
            return Math.Sqrt(dx*dx+dy*dy);
        }

        internal static PhotoPerson[] Match(BodyGeometry[] bodies,FaceMesh[] faces)
        {
            var people=bodies.Select((b,i)=>new PhotoPerson{index=i,bodyIndex=i}).ToList();
            var distances=bodies.Select(b=>faces.Select(f=>FaceDistance(b,f)).ToArray()).ToArray();
            var paired=new bool[faces.Length];
            for(int b=0;b<bodies.Length;b++)
            {
                for(int f=0;f<faces.Length;f++)
                {
                    double score=distances[b][f];
                    if(score>.50)continue;
                    if(Enumerable.Range(0,faces.Length).Any(i=>i!=f&&distances[b][i]<score+.20) ||
                        Enumerable.Range(0,bodies.Length).Any(i=>i!=b&&distances[i][f]<score+.20))continue;
                    people[b].faceIndex=f;paired[f]=true;break;
                }
            }
            for(int f=0;f<faces.Length;f++)if(!paired[f])people.Add(new PhotoPerson{index=people.Count,faceIndex=f});
            return people.ToArray();
        }

        internal static PhotoPerson[] Describe(IDictionary<string,double> values,BodySubjectInfo[] body,BodySubjectInfo[] portrait)
        {
            return Describe(body.Length==0?null:BodyAssets.ResolveScene(values),portrait.Length==0?null:PortraitAssets.Resolve(values));
        }

        internal static PhotoPerson[] Describe(BodyScene body,PortraitScene portrait)
        {
            var poses=body==null?new BodyGeometry[0]:body.Subjects;
            var meshes=portrait==null?new FaceMesh[0]:portrait.Faces;
            if(body!=null&&portrait!=null&&body.SourceHash!=BitConverter.ToString(Convert.FromBase64String(portrait.SourceHash)).Replace("-","").ToLowerInvariant())
            {
                var unpaired=Match(poses,new FaceMesh[0]).ToList();
                for(int f=0;f<meshes.Length;f++)unpaired.Add(new PhotoPerson{index=unpaired.Count,faceIndex=f});
                return unpaired.ToArray();
            }
            return Match(poses,meshes);
        }

        internal static PhotoPerson[] Describe(IDictionary<string,double> values)
        {
            return Describe(values,BodyAssets.Describe(values),PortraitAssets.Describe(values));
        }

        internal static void SetPlanTarget(Dictionary<string,object> args,Dictionary<string,double> values,Dictionary<string,object> analysis)
        {
            object raw;
            if(!args.TryGetValue("subjectIndex",out raw))return;
            if(!(raw is int) || (int)raw < -1)throw new ArgumentException("人物选择无效。");
            int index=(int)raw;
            var people=Describe(values);
            if(index>=people.Length)throw new ArgumentException("所选人物已改变，请重新选择。");
            var person=index<0?null:people[index];
            analysis["bodyPersonIndex"]=person==null?-1:person.bodyIndex??-1;
            analysis["faceIndex"]=person==null?-1:person.faceIndex??-1;
            analysis["subjectNumber"]=index+1;
            analysis["allowLegacyFace"]=people.Length==0;
        }
    }
}
