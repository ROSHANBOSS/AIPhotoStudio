using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AIPhotoStudio
{
    // The cropped map travels with a photo-bound, process-local signature. The saved
    // edit is an ordinary, full-resolution-independent semantic mask in the shared editor.
    internal sealed class SetMatchHair
    {
        private const int Side=LocalEdits.PredictionSide;
        private static readonly byte[] signingKey=NewKey();
        public int left {get;set;} public int top {get;set;}
        public int width {get;set;} public int height {get;set;}
        public string bytes {get;set;} public string stamp {get;set;}
        private byte[] expanded;
        private bool[] protectedArea;
        internal byte[] Alpha {get{
            if(expanded==null){
                byte[] crop=Convert.FromBase64String(bytes),full=new byte[Side*Side];
                for(int j=0;j<height;j++)Buffer.BlockCopy(crop,j*width,full,(top+j)*Side+left,width);
                expanded=full;
            }
            return expanded;
        }}
        private static byte[] NewKey(){var key=new byte[32];using(var rng=RandomNumberGenerator.Create())rng.GetBytes(key);return key;}
        private string Sign(string binding){
            using(var hmac=new HMACSHA256(signingKey))
                return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(binding+"/"+left+"/"+top+"/"+width+"/"+height+"/"+bytes)));
        }
        internal void Verify(string binding){
            if(stamp!=Sign(binding))throw new InvalidOperationException("此发区与照片或编辑版本不符，请重新点选人物。");
        }
        internal static SetMatchHair Read(object raw){
            var obj=raw as IDictionary<string,object>;
            var fields=new[]{"left","top","width","height","bytes","stamp"};
            if(obj==null||obj.Count!=fields.Length||fields.Any(k=>!obj.ContainsKey(k)))throw new ArgumentException("头发作用范围不完整。");
            var map=StudioBridge.Json().ConvertToType<SetMatchHair>(obj);
            if(map.left<0||map.top<0||map.width<1||map.height<1||map.width>Side||map.height>Side||
                map.left>Side-map.width||map.top>Side-map.height||map.bytes==null||map.bytes.Length!=4*((map.width*map.height+2)/3)||
                map.stamp==null||map.stamp.Length!=44||Convert.FromBase64String(map.bytes).Length!=map.width*map.height)
                throw new ArgumentException("头发作用范围尺寸无效。");
            return map;
        }
        internal SetMatchRegions.Area Area(){
            return new SetMatchRegions.Area{Role="hair",Hair=this,X=(left+width*.5)/Side,Y=(top+height*.5)/Side,Rx=width*.5/Side,Ry=height*.5/Side};
        }
        internal object View(){
            var area=Area();return new {x=area.X,y=area.Y,rx=area.Rx,ry=area.Ry,hair=this};
        }
        private static SetMatchHair FromPixels(byte[] field,IList<int> active)
        {
            int l=active.Min(i=>i%Side),r=active.Max(i=>i%Side),t=active.Min(i=>i/Side),b=active.Max(i=>i/Side);
            var map=new SetMatchHair{left=l,top=t,width=r-l+1,height=b-t+1};
            var cropped=new byte[map.width*map.height];
            foreach(int i in active)cropped[(i/Side-t)*map.width+i%Side-l]=field[i];
            map.bytes=Convert.ToBase64String(cropped);return map;
        }
        internal bool SameHead(byte[] other)
        {
            int shared=0,total=0;var selected=Alpha;
            for(int i=0;i<selected.Length;i++){
                bool a=selected[i]>=96,b=other[i]>=96;
                if(a||b)total++;if(a&&b)shared++;
            }
            return total>0&&shared>.48*total;
        }
        // A small change in the rendered hair color can shift a few classifier cells. Reuse the
        // durable mask only if the new per-person evidence still supports its entire old outline.
        internal SetMatchHair Reuse(LocalEdits.MaskSet set,string digest,string binding)
        {
            foreach(var layer in set.layers.Where(m=>m.kind=="hair"&&m.alpha!=null)){
                var previous=Convert.FromBase64String(layer.alpha);
                if(!SameHead(previous))continue;
                var active=Enumerable.Range(0,previous.Length).Where(i=>previous[i]>0).ToArray();
                var old=FromPixels(previous,active);
                if(old.Area().Identity(digest)!=layer.id)continue;
                var current=Alpha;
                if(active.Any(i=>protectedArea[i]))return this;
                foreach(int i in active)if(current[i]==0){
                    int x=i%Side,y=i/Side;
                    bool near=false;
                    for(int j=Math.Max(0,y-1);j<=Math.Min(Side-1,y+1)&&!near;j++)
                        for(int k=Math.Max(0,x-1);k<=Math.Min(Side-1,x+1);k++)if(current[j*Side+k]!=0){near=true;break;}
                    if(!near)return this;
                }
                old.stamp=old.Sign(binding);return old;
            }
            return this;
        }
        internal static bool Owned(LocalEdits.MaskLayer layer,byte[] alpha,string digest){
            var active=Enumerable.Range(0,alpha.Length).Where(i=>alpha[i]>0).ToArray();
            return active.Length>0&&FromPixels(alpha,active).Area().Identity(digest)==layer.id;
        }
        private static bool InHead(BodyBox b,double u,double v){
            double x=(u-b.x)/b.w,y=(v-b.y)/b.h;
            return x>=-.47&&x<=1.47&&y>=-.68&&y<=1.04&&Math.Pow((x-.5)/1.02,2)+Math.Pow((y-.19)/.95,2)<1;
        }
        internal static SetMatchHair Locate(FaceMesh face,FaceMesh[] faces,byte[] category,string binding,int width,int height)
        {
            double step=Math.Max(.5,Math.Max(Math.Min(width,height)/220d,Math.Max(width,height)/1000d));
            int padX=Math.Max(2,(int)Math.Ceiling(.5+Side*step/width)),padY=Math.Max(2,(int)Math.Ceiling(.5+Side*step/height));
            if(category==null||face.Box.w*Side<2*padX+5||face.Box.h*Side<2*padY+5)return null;
            var box=face.Box;var protectedFace=new bool[Side*Side];var field=new byte[Side*Side];
            int x0=Math.Max(0,(int)Math.Floor((box.x-.5*box.w)*Side)),x1=Math.Min(Side-1,(int)Math.Ceiling((box.x+1.5*box.w)*Side));
            int y0=Math.Max(0,(int)Math.Floor((box.y-.7*box.h)*Side)),y1=Math.Min(Side-1,(int)Math.Ceiling((box.y+1.1*box.h)*Side));
            for(int y=y0;y<=y1;y++)for(int x=x0;x<=x1;x++){
                double u=(x+.5)/Side,v=(y+.5)/Side;
                if(!SetMatchSuggestions.Contains(face,FaceMesh.Oval,u,v))continue;
                // Leave a tiny gap beyond the landmark oval for the renderer's interpolated grid.
                for(int j=Math.Max(0,y-padY);j<=Math.Min(Side-1,y+padY);j++)
                    for(int i=Math.Max(0,x-padX);i<=Math.Min(Side-1,x+padX);i++)protectedFace[j*Side+i]=true;
            }
            for(int y=y0;y<=y1;y++)for(int x=x0;x<=x1;x++){
                double u=(x+.5)/Side,v=(y+.5)/Side;int index=y*Side+x;
                if(faces.Any(other=>other!=face&&InHead(other.Box,u,v)))protectedFace[index]=true;
                if(protectedFace[index]||category[index]<116||!InHead(box,u,v))continue;
                field[index]=(byte)Math.Min(255,(category[index]-115)*2);
            }
            // Keep the connected component that meets the crown or temple of this head.
            var visited=new bool[field.Length];List<int> best=null;int bestAnchors=0;
            for(int y=y0;y<=y1;y++)for(int x=x0;x<=x1;x++){
                int seed=y*Side+x;
                if(visited[seed]||field[seed]==0)continue;
                var component=new List<int>{seed};visited[seed]=true;int anchors=0;
                for(int n=0;n<component.Count;n++){
                    int index=component[n],i=index%Side,j=index/Side;
                    double dx=((i+.5)/Side-box.x)/box.w,dy=((j+.5)/Side-box.y)/box.h;
                    if(field[index]>=140&&dx>=-.23&&dx<=1.23&&dy>=-.45&&dy<=.40)anchors++;
                    if(i>0)Visit(index-1,field,visited,component);if(i<Side-1)Visit(index+1,field,visited,component);
                    if(j>0)Visit(index-Side,field,visited,component);if(j<Side-1)Visit(index+Side,field,visited,component);
                }
                if(anchors>bestAnchors){bestAnchors=anchors;best=component;}
            }
            if(best==null||bestAnchors<10||best.Count(i=>field[i]>=160)<24)return null;
            var map=FromPixels(field,best);map.protectedArea=protectedFace;map.stamp=map.Sign(binding);return map;
        }
        private static void Visit(int i,byte[] field,bool[] visited,List<int> component){
            if(!visited[i]&&field[i]>0){visited[i]=true;component.Add(i);}
        }
        internal BitmapSource MarkedDetail(BitmapSource frame,bool active)
        {
            var box=SetMatchRegions.DetailBox(frame,Area());int stride=box.Width*4;
            var rgb=new FormatConvertedBitmap(frame,PixelFormats.Bgra32,null,0);
            var data=new byte[stride*box.Height];rgb.CopyPixels(box,data,stride,0);
            var coverage=active?LocalEdits.MatchPrediction(Alpha,frame.PixelWidth,frame.PixelHeight):null;
            for(int y=0;y<box.Height;y++)for(int x=0;x<box.Width;x++){
                double a=coverage==null?0:.65*coverage(box.X+x,box.Y+y);int p=y*stride+4*x;
                data[p]=(byte)Math.Round(data[p]*(1-a)+108*a);data[p+1]=(byte)Math.Round(data[p+1]*(1-a)+176*a);
                data[p+2]=(byte)Math.Round(data[p+2]*(1-a)+255*a);
            }
            var result=BitmapSource.Create(box.Width,box.Height,96,96,PixelFormats.Bgra32,null,data,stride);result.Freeze();return result;
        }
    }
    public static partial class LocalEdits
    {
        internal static Func<double,double,double> MatchPrediction(byte[] alpha,int width,int height){
            var grid=new Grid(width,height,new Stroke[0],CancellationToken.None,alpha);
            return (x,y)=>grid.At(grid.Mask,x,y);
        }
    }
}
