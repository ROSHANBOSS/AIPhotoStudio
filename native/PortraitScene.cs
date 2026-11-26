using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;

namespace AIPhotoStudio
{
    internal sealed class PortraitScene
    {
        internal string SourceHash;
        internal FaceMesh[] Faces;
        internal string GeometryHash;
        internal PortraitSpot[] Spots=new PortraitSpot[0];
        internal PortraitSpot[] KeepMarks=new PortraitSpot[0];
    }

    internal sealed class FaceMesh
    {
        internal static readonly int[] Oval = {10,338,297,332,284,251,389,356,454,323,361,288,397,365,379,378,400,377,152,148,176,149,150,136,172,58,132,93,234,127,162,21,54,103,67,109};
        internal static readonly int[] LeftEye = {33,7,163,144,145,153,154,155,133,173,157,158,159,160,161,246};
        internal static readonly int[] RightEye = {263,249,390,373,374,380,381,382,362,398,384,385,386,387,388,466};
        internal static readonly int[] LeftBrow = {70,63,105,66,107,55,65,52,53,46};
        internal static readonly int[] RightBrow = {300,293,334,296,336,285,295,282,283,276};
        internal static readonly int[] Lips = {61,40,37,0,267,270,291,321,314,17,84,91};
        internal static readonly int[] Mouth = {78,191,80,81,82,13,312,311,310,415,308,324,318,402,317,14,87,178,88,95};
        internal Point[] Points;
        internal BodyBox Box;
        internal double Aspect;
        internal Lazy<PortraitRegions> Regions;
        internal Lazy<PortraitSpotArea> SpotArea;
        internal bool LeftOpen, RightOpen, MouthOpen;
        internal void Measure(int width,int height)
        {
            Aspect=(double)width/height;
            Func<int,int,double> span=(a,b)=>{
                double x=(Points[a].X-Points[b].X)*width,y=(Points[a].Y-Points[b].Y)*height;
                return Math.Sqrt(x*x+y*y);
            };
            double left=Oval.Min(i=>Points[i].X),right=Oval.Max(i=>Points[i].X);
            double top=Oval.Min(i=>Points[i].Y),bottom=Oval.Max(i=>Points[i].Y);
            Box=new BodyBox{x=left,y=top,w=right-left,h=bottom-top};
            double eyeSpan=span(33,263),faceHeight=span(10,152),faceWidth=span(234,454);
            double eye1=span(33,133),eye2=span(263,362),mouth=span(78,308);
            var horizontal=new Vector((Points[263].X-Points[33].X)*width,(Points[263].Y-Points[33].Y)*height);
            var vertical=new Vector((Points[152].X-Points[10].X)*width,(Points[152].Y-Points[10].Y)*height);
            if(eyeSpan<10 || Math.Min(eye1,eye2)<Math.Max(2.2,eyeSpan*.10) || eyeSpan<.36*faceWidth ||
                eyeSpan>1.12*faceWidth || faceHeight<.77*eyeSpan || faceHeight>3.9*eyeSpan ||
                Box.w<=0||Box.h<=0||Math.Abs(Vector.CrossProduct(horizontal,vertical))<.48*eyeSpan*faceHeight||
                Points.Any(p=>p.X<left-.15*Box.w||p.X>right+.15*Box.w||p.Y<top-.15*Box.h||p.Y>bottom+.15*Box.h)||
                mouth<.17*eyeSpan || span(1,152)<.18*faceHeight || span(168,1)<.075*faceHeight ||
                Oval.Any(i=>Points[i].X<.00001 || Points[i].X>.99999 || Points[i].Y<.00001 || Points[i].Y>.99999))
                throw new InvalidDataException("有一张脸的轮廓或眼鼻定位不可靠。");
            LeftOpen=span(159,145)>Math.Max(1.15,.105*eye1);
            RightOpen=span(386,374)>Math.Max(1.15,.105*eye2);
            MouthOpen=span(13,14)>Math.Max(1.4,.065*mouth);
            Regions=new Lazy<PortraitRegions>(()=>new PortraitRegions(this),true);
            SpotArea=new Lazy<PortraitSpotArea>(()=>new PortraitSpotArea(this),true);
        }
        internal bool SamePosition(FaceMesh other)
        {
            foreach(int i in new[]{33,263,1,152})
                if(Math.Abs(Points[i].X-other.Points[i].X)>.16*Box.w ||
                   Math.Abs(Points[i].Y-other.Points[i].Y)>.14*Box.h)return false;
            return true;
        }
        internal bool Available(string suffix)
        {
            if(suffix=="teeth")return MouthOpen;
            if(suffix=="eye_light" || suffix=="eye_detail" || suffix=="sclera")return LeftOpen||RightOpen;
            return true;
        }
    }

    internal static class PortraitControls
    {
        internal const int MaxFaces=16;
        internal const string ResourceGroup="人脸关键点";
        internal static readonly string[] Keys={"light","smooth","redness","even","under_eye","eye_light","eye_detail","sclera","teeth",
            "slim","jaw","chin_length","chin_width","forehead","eye_size","eye_spacing","nose_bridge","nose_width","lips"};
        internal static readonly string[] Labels={"皮肤提亮","保留纹理磨皮","减淡泛红","均匀肤色","淡化黑眼圈","眼睛提亮","眼睛清晰度","眼白调整","牙齿美白",
            "瘦脸","收下颌线","下巴长度","下巴宽度","额头比例","眼睛大小","眼距","鼻梁宽度","鼻翼宽度","嘴唇厚度"};
        private static readonly Dictionary<string,int> Indices=new Dictionary<string,int>(StringComparer.Ordinal);
        internal static readonly ControlSpec[] Controls=MakeControls();
        internal static string Key(int face,string suffix){return "portrait_p"+(face+1)+"_"+suffix;}
        internal static int Index(string key)
        {
            int index;
            return key!=null && Indices.TryGetValue(key,out index)?index:-1;
        }
        internal static string Suffix(string key){return Index(key)>=0?key.Substring(key.IndexOf('_',10)+1):null;}
        internal static bool Bound(string key){return Index(key)>=0 || key.StartsWith("portrait_ref",StringComparison.Ordinal);}
        internal static bool Active(IDictionary<string,double> values)
        {
            return values!=null && values.Any(p=>p.Value!=0&&Index(p.Key)>=0);
        }
        private static ControlSpec[] MakeControls()
        {
            var list=new List<ControlSpec>();
            for(int i=0;i<5;i++)list.Add(new ControlSpec{key="portrait_ref"+i,group=ResourceGroup,label="人脸关键点指纹",
                min=0,max=i==4?281474976710655d:4503599627370495d,step=1,initial=0});
            for(int i=0;i<MaxFaces;i++)for(int k=0;k<Keys.Length;k++)
            {
                string key=Key(i,Keys[k]); Indices.Add(key,i);
                list.Add(new ControlSpec{key=key,group="人像",label="人脸 "+(i+1)+" · "+Labels[k],
                    min=k<9?0:-100,max=100,step=1,initial=0});
            }
            return list.ToArray();
        }
    }
}
