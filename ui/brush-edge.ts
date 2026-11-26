import {useEffect,useState} from 'react';
import {decodeBodyInput} from './body-detector';
import {BrushStroke,NormalizedPoint,clamp,invoke,messageOf} from './model';

type Guide={width:number;height:number;pixels:Uint8ClampedArray;digest:string};
export type EdgeDomain={width:number;height:number;inside:Uint8Array;digest:string;tested:number;rejected:number};
export type EdgeTest=(x:number,y:number)=>boolean;

export function useBrushGuide(id:string|undefined,revision:number,active:boolean){
  const [enabled,setEnabled]=useState(false),[hint,setHint]=useState('');
  const [loaded,setLoaded]=useState<{key:string;guide?:Guide;error?:string}|null>(null);
  const key=enabled&&active&&id?id+'@'+revision:'';
  useEffect(()=>{
    setHint('');
    if(!key||!id){setLoaded(null);return;}
    let live=true;
    void invoke<{digest:string;image:string}>('brushEdgeInput',{id,revision}).then(async reply=>{
      const image=await decodeBodyInput(reply.image);
      if(!live)return;
      const canvas=document.createElement('canvas');
      canvas.width=image.naturalWidth;canvas.height=image.naturalHeight;
      const context=canvas.getContext('2d');
      if(!context)throw new Error('无法读取原片轮廓。');
      context.drawImage(image,0,0);
      setLoaded({key,guide:{width:canvas.width,height:canvas.height,digest:reply.digest,
        pixels:context.getImageData(0,0,canvas.width,canvas.height).data}});
    }).catch(error=>{if(live)setLoaded({key,error:messageOf(error)});});
    return ()=>{live=false;};
  },[key]);
  const current=loaded?.key===key?loaded:null;
  return {enabled,setEnabled,guide:current?.guide,error:current?.error,pending:!!key&&!current,hint,setHint};
}

// Grow the connected component under the first dab. The seed threshold limits gradual
// drift across a long transition; the neighbour threshold also stops sudden local edges.
export function seedEdge(guide:Guide,point:NormalizedPoint):EdgeDomain|null{
  const {width,height,pixels,digest}=guide;
  const sx=clamp(Math.floor(point.x*width),0,width-1),sy=clamp(Math.floor(point.y*height),0,height-1);
  const seed=sy*width+sx,origin=seed*4;
  if(pixels[origin+3]<8)return null;
  const inside=new Uint8Array(width*height),queue=new Int32Array(width*height);
  let head=0,end=1;
  queue[0]=seed;inside[seed]=1;
  function differs(a:number,b:number,amount:number){
    return Math.abs(pixels[a]-pixels[b])>amount||Math.abs(pixels[a+1]-pixels[b+1])>amount||
      Math.abs(pixels[a+2]-pixels[b+2])>amount;
  }
  function visit(next:number,from:number){
    if(inside[next]||pixels[next*4+3]<8||differs(next*4,origin,62)||differs(next*4,from*4,26))return;
    inside[next]=1;queue[end++]=next;
  }
  while(head<end){
    const pos=queue[head++],x=pos%width;
    if(x>0)visit(pos-1,pos);
    if(x+1<width)visit(pos+1,pos);
    if(pos>=width)visit(pos-width,pos);
    if(pos+width<inside.length)visit(pos+width,pos);
  }
  return {width,height,inside,digest,tested:0,rejected:0};
}

export function liveEdge(domain:EdgeDomain):EdgeTest{
  return (x,y)=>{
    const inside=!!domain.inside[clamp(Math.floor(y*domain.height),0,domain.height-1)*domain.width+
      clamp(Math.floor(x*domain.width),0,domain.width-1)];
    domain.tested++;
    if(!inside)domain.rejected++;
    return inside;
  };
}

// Source SHA-256, six little-endian 16-bit geometry fields, then one bit per cell
// of the brush's bounding rectangle. The ordinary brush keeps the feather and path.
export function packEdge(domain:EdgeDomain,stroke:BrushStroke){
  const {width,height,inside}=domain,radius=stroke.radius*Math.min(width,height)+2;
  let left=width,top=height,right=0,bottom=0;
  for(const point of stroke.points){
    left=Math.min(left,Math.floor(point.x*width-radius));top=Math.min(top,Math.floor(point.y*height-radius));
    right=Math.max(right,Math.ceil(point.x*width+radius));bottom=Math.max(bottom,Math.ceil(point.y*height+radius));
  }
  left=Math.max(0,left);top=Math.max(0,top);right=Math.min(width,right);bottom=Math.min(height,bottom);
  const columns=right-left,rows=bottom-top,bytes=new Uint8Array(44+Math.ceil(columns*rows/8));
  for(let i=0;i<32;i++)bytes[i]=parseInt(domain.digest.slice(i*2,i*2+2),16);
  const header=new DataView(bytes.buffer);
  [width,height,left,top,columns,rows].forEach((v,i)=>header.setUint16(32+i*2,v,true));
  for(let y=top;y<bottom;y++)for(let x=left;x<right;x++){
    const bit=(y-top)*columns+(x-left);
    if(inside[y*width+x])bytes[44+(bit>>3)]|=1<<(bit&7);
  }
  let text='';
  for(let i=0;i<bytes.length;i+=8192)text+=String.fromCharCode(...bytes.subarray(i,i+8192));
  return {edge:btoa(text),weak:domain.rejected<Math.max(3,domain.tested*.015)};
}

const readers=new WeakMap<BrushStroke,EdgeTest>();
export function savedEdge(stroke:BrushStroke):EdgeTest|undefined{
  if(!stroke.edge)return;
  let read=readers.get(stroke);
  if(read)return read;
  const bytes=atob(stroke.edge);
  const u16=(at:number)=>bytes.charCodeAt(at)|(bytes.charCodeAt(at+1)<<8);
  const width=u16(32),height=u16(34),left=u16(36),top=u16(38),columns=u16(40),rows=u16(42);
  read=(nx,ny)=>{
    const x=clamp(Math.floor(nx*width),0,width-1)-left,y=clamp(Math.floor(ny*height),0,height-1)-top;
    if(x<0||y<0||x>=columns||y>=rows)return false;
    const bit=y*columns+x;
    return !!(bytes.charCodeAt(44+(bit>>3))&(1<<(bit&7)));
  };
  readers.set(stroke,read);return read;
}
