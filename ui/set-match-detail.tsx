import React,{useEffect,useRef,useState} from 'react';
import {MatchArea,MatchPerson,MatchRole} from './set-match-suggestions';

export type SamplePoint={x:number;y:number};
const clamp=(v:number,lo:number,hi:number)=>Math.min(hi,Math.max(lo,v));

export function FaceThumbnail({photo,box}:{photo:HTMLImageElement|null;box:MatchPerson['box']}){
  const canvas=useRef<HTMLCanvasElement>(null);
  useEffect(()=>{
    if(!photo)return;
    const draw=()=>{
      const w=photo.naturalWidth,h=photo.naturalHeight,ctx=canvas.current?.getContext('2d');
      if(!w||!h||!ctx)return;
      const side=Math.min(w,h,Math.max(box.w*w,box.h*h)*1.55);
      const x=clamp((box.x+box.w*.5)*w-side*.5,0,w-side),y=clamp((box.y+box.h*.44)*h-side*.5,0,h-side);
      ctx.drawImage(photo,x,y,side,side,0,0,64,64);
    };
    draw();photo.addEventListener('load',draw);
    return ()=>photo.removeEventListener('load',draw);
  },[photo,box]);
  return <canvas width={64} height={64} ref={canvas} aria-hidden="true" style={{display:'block',width:35,height:35}}/>;
}

export function SampleDetail({photo,center,box,area,label,role,disabled,choose}:{
  photo:HTMLImageElement;center:SamplePoint;box?:MatchPerson['box'];area?:MatchArea;label:string;role:MatchRole;disabled:boolean;choose:(a:MatchArea)=>void;
}){
  const canvas=useRef<HTMLCanvasElement>(null),anchor=useRef<{id:number;point:SamplePoint}|null>(null);
  const [draft,setDraft]=useState<MatchArea|null>(null),[cursor,setCursor]=useState<SamplePoint|null>(null);
  const w=photo.naturalWidth,h=photo.naturalHeight,short=Math.min(w,h);
  const side=Math.min(short,box?clamp(1.85*Math.max(box.w*w,box.h*h),52,.36*short):.26*short);
  const x=clamp(center.x*w-side/2,0,w-side),y=clamp(center.y*h-side/2,0,h-side);
  useEffect(()=>{
    const ctx=canvas.current?.getContext('2d');
    if(ctx&&side>0)ctx.drawImage(photo,x,y,side,side,0,0,256,256);
    anchor.current=null;setDraft(null);setCursor(null);
  },[photo,x,y,side]);
  const point=(e:React.PointerEvent<HTMLButtonElement>)=>{
    const rect=canvas.current!.getBoundingClientRect();
    return {x:(x+clamp((e.clientX-rect.left)/rect.width,0,1)*side)/w,
      y:(y+clamp((e.clientY-rect.top)/rect.height,0,1)*side)/h};
  };
  function ellipse(p:SamplePoint,q:SamplePoint){
    const tap=Math.hypot((q.x-p.x)*w,(q.y-p.y)*h)<side*.022;
    return {x:(p.x+q.x)/2,y:(p.y+q.y)/2,rx:Math.max(.004,tap?side*.05/w:Math.abs(p.x-q.x)/2),
      ry:Math.max(.004,tap?side*.05/h:Math.abs(p.y-q.y)/2)};
  }
  const show=draft||area;
  return <button type="button" disabled={disabled} aria-label={label+'放大取样图'} title="点或拖圈，方向键移动，回车确认。"
    style={{position:'relative',display:'block',width:'min(100%, 210px)',padding:0,border:0,margin:'6px auto',overflow:'hidden',touchAction:'none',cursor:'crosshair'}}
    onPointerDown={e=>{if(e.button!==0)return;const p=point(e);e.currentTarget.setPointerCapture(e.pointerId);
      anchor.current={id:e.pointerId,point:p};setDraft(ellipse(p,p));setCursor(null);}}
    onPointerMove={e=>{if(anchor.current?.id===e.pointerId)setDraft(ellipse(anchor.current.point,point(e)));}}
    onPointerUp={e=>{if(anchor.current?.id!==e.pointerId)return;const a=ellipse(anchor.current.point,point(e));anchor.current=null;setDraft(null);choose(a);}}
    onPointerCancel={()=>{anchor.current=null;setDraft(null);}}
    onKeyDown={e=>{
      const p=cursor||{x:(x+side/2)/w,y:(y+side/2)/h},step=side*(e.shiftKey?.1:.02);
      const move:Record<string,number[]>={ArrowLeft:[-1,0],ArrowRight:[1,0],ArrowUp:[0,-1],ArrowDown:[0,1]};
      if(move[e.key]){e.preventDefault();setCursor({x:clamp(p.x*w+step*move[e.key][0],x,x+side)/w,y:clamp(p.y*h+step*move[e.key][1],y,y+side)/h});}
      if(e.key==='Enter'||e.key===' '){e.preventDefault();choose(ellipse(p,p));}
    }}>
    <canvas ref={canvas} width={256} height={256} aria-hidden="true" style={{display:'block',width:'100%',height:'auto'}}/>
    {show&&<span className={'set-area-ring '+role} style={{left:((show.x-show.rx)*w-x)/side*100+'%',top:((show.y-show.ry)*h-y)/side*100+'%',
      width:show.rx*w*200/side+'%',height:show.ry*h*200/side+'%'}}/>}
    {cursor&&<span className="set-area-cursor" style={{left:(cursor.x*w-x)/side*100+'%',top:(cursor.y*h-y)/side*100+'%'}}>+</span>}
  </button>;
}
