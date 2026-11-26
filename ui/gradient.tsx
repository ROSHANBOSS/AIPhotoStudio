import React,{useRef} from 'react';
import {clamp} from './model';
import type {NormalizedPoint as Point} from './model';

export type GradientKind='linear'|'radial';
export type GradientGeometry={x:number;y:number;dx:number;dy:number;feather:number;invert:boolean};
export type GradientMask={kind:GradientKind;geometry:GradientGeometry};
export type GradientPreview=GradientMask & {creating:boolean};

export function isGradientKind(kind?:string):kind is GradientKind{return kind==='linear'||kind==='radial';}
export function validGradient({kind,geometry:g}:GradientMask){
  return kind==='radial'?g.dx>=.002&&g.dy>=.002:g.dx*g.dx+g.dy*g.dy>=.003*.003;
}

export function gradientSampler({kind,geometry:g}:GradientMask,width:number,height:number){
  const radial=kind==='radial',vx=g.dx*width,vy=g.dy*height,length=vx*vx+vy*vy;
  const kx=radial?1/vx:vx/length,ky=radial?1/vy:vy/length,ox=g.x*width-.5,oy=g.y*height-.5;
  const edge=radial?1:.5+.5*g.feather;
  return (px:number,py:number)=>{
    const u=(px-ox)*kx,v=(py-oy)*ky,distance=radial?Math.sqrt(u*u+v*v):u+v;
    const a=g.feather===0?Number(distance<=edge):clamp((edge-distance)/g.feather,0,1);
    return g.invert?1-a:a;
  };
}

type Handle='start'|'end'|'center'|'radiusX'|'radiusY';
type HandlePoint=Point & {id:Handle;name:string};
function handles(kind:GradientKind,g:GradientGeometry):HandlePoint[]{
  if(kind==='linear')return [{id:'start',name:'渐变起点',x:g.x,y:g.y},{id:'end',name:'渐变终点',x:g.x+g.dx,y:g.y+g.dy}];
  // Keep the radius handles on a visible side when the ellipse reaches an image edge.
  const x=g.x+g.dx<=1?g.x+g.dx:g.x-g.dx;
  const y=g.y+g.dy<=1?g.y+g.dy:g.y-g.dy;
  return [{id:'center',name:'椭圆中心',x:g.x,y:g.y},{id:'radiusX',name:'椭圆横半径',x:clamp(x,0,1),y:g.y},
    {id:'radiusY',name:'椭圆纵半径',x:g.x,y:clamp(y,0,1)}];
}

function moveHandle(g:GradientGeometry,id:Handle,p:Point):GradientGeometry{
  switch(id){
    case 'start':return {...g,x:p.x,y:p.y,dx:g.x+g.dx-p.x,dy:g.y+g.dy-p.y};
    case 'end':return {...g,dx:p.x-g.x,dy:p.y-g.y};
    case 'center':return {...g,x:p.x,y:p.y};
    case 'radiusX':{
      const side=g.x+g.dx<=1?1:-1,visible=clamp(g.x+side*g.dx,0,1);
      return {...g,dx:clamp(g.dx+side*(p.x-visible),.002,1)};
    }
    case 'radiusY':{
      const side=g.y+g.dy<=1?1:-1,visible=clamp(g.y+side*g.dy,0,1);
      return {...g,dy:clamp(g.dy+side*(p.y-visible),.002,1)};
    }
  }
}

export function GradientHandles({shape,disabled,onChange}:{shape:GradientMask;disabled:boolean;onChange:(g:GradientGeometry)=>void}){
  const {kind,geometry:g}=shape,ready=validGradient(shape);
  const points=ready?handles(kind,g):[];
  const drag=useRef<{pointer:number;anchor:Point;initial:GradientGeometry;handle?:Handle}|null>(null);
  const pointAt=(e:React.PointerEvent<HTMLDivElement>)=>{
    const box=e.currentTarget.getBoundingClientRect();
    return {x:clamp((e.clientX-box.left)/box.width,0,1),y:clamp((e.clientY-box.top)/box.height,0,1)};
  };
  const dragTo=(p:Point)=>{
    const job=drag.current;
    if(!job)return;
    if(job.handle){
      const initial=handles(kind,job.initial).find(h=>h.id===job.handle);
      if(!initial||p.x===job.anchor.x&&p.y===job.anchor.y)return;
      const moved=moveHandle(job.initial,job.handle,{
        x:clamp(initial.x+(p.x-job.anchor.x),0,1),y:clamp(initial.y+(p.y-job.anchor.y),0,1)});
      if(validGradient({kind,geometry:moved}))onChange(moved);
    }else{
      const {x,y}=job.anchor,dx=p.x-x,dy=p.y-y;
      onChange({...job.initial,x,y,dx:kind==='linear'?dx:Math.abs(dx),dy:kind==='linear'?dy:Math.abs(dy)});
    }
  };
  return <div className="gradient-geometry" aria-label={kind==='linear'?'线性渐变画布':'径向渐变画布'}
    style={{position:'absolute',inset:0,pointerEvents:disabled?'none':'auto',touchAction:'none',cursor:'crosshair'}}
    onPointerDown={e=>{
      if(disabled||e.button!==0||drag.current)return;
      e.stopPropagation();e.preventDefault();
      const handle=(e.target as Element).closest('[data-gradient-handle]')?.getAttribute('data-gradient-handle') as Handle|undefined;
      drag.current={pointer:e.pointerId,anchor:pointAt(e),initial:g,handle};
      e.currentTarget.setPointerCapture(e.pointerId);
    }}
    onPointerMove={e=>{if(drag.current?.pointer===e.pointerId){e.stopPropagation();dragTo(pointAt(e));}}}
    onPointerUp={e=>{
      const job=drag.current;
      if(job?.pointer!==e.pointerId)return;
      e.stopPropagation();
      const p=pointAt(e);
      const draw={kind,geometry:{...job.initial,dx:Math.abs(p.x-job.anchor.x),dy:Math.abs(p.y-job.anchor.y)}};
      if(!job.handle&&!validGradient(draw))onChange(job.initial);
      else dragTo(p);
      drag.current=null;
    }}
    onPointerCancel={()=>{if(drag.current){onChange(drag.current.initial);drag.current=null;}}}
    onLostPointerCapture={()=>{if(drag.current){onChange(drag.current.initial);drag.current=null;}}}
    onDoubleClick={e=>e.stopPropagation()}>
    {ready&&<><svg aria-hidden="true" viewBox="0 0 1 1" preserveAspectRatio="none" width="100%" height="100%" style={{position:'absolute',inset:0,pointerEvents:'none'}}>
      {['#25262b','#fff4dc'].map((color,index)=>{
        const pen={fill:'none',stroke:color,strokeWidth:index?1.3:3.5,vectorEffect:'non-scaling-stroke'};
        return kind==='linear'?<line key={color} x1={g.x} y1={g.y} x2={g.x+g.dx} y2={g.y+g.dy} {...pen}/>:
          <g key={color}><ellipse cx={g.x} cy={g.y} rx={g.dx} ry={g.dy} {...pen}/>
            {g.feather>0&&g.feather<1&&<ellipse cx={g.x} cy={g.y} rx={g.dx*(1-g.feather)} ry={g.dy*(1-g.feather)} {...pen} strokeDasharray="4 4"/>}</g>;
      })}
    </svg>
      {points.map(p=><button key={p.id} type="button" disabled={disabled} data-gradient-handle={p.id} aria-label={p.name} title={p.name+' · 方向键微调'}
        style={{position:'absolute',left:p.x*100+'%',top:p.y*100+'%',transform:'translate(-50%, -50%)',width:26,height:26,padding:5,
          border:0,borderRadius:'50%',background:'transparent',display:'grid',placeItems:'center',cursor:'grab'}}
        onKeyDown={e=>{
          const move:{[k:string]:[number,number]}={ArrowLeft:[-1,0],ArrowRight:[1,0],ArrowUp:[0,-1],ArrowDown:[0,1]};
          if(!move[e.key])return;
          e.preventDefault();e.stopPropagation();
          const [dx,dy]=move[e.key],step=e.shiftKey ? .02 : .002;
          const moved=moveHandle(g,p.id,{x:clamp(p.x+step*dx,0,1),y:clamp(p.y+step*dy,0,1)});
          if(validGradient({kind,geometry:moved}))onChange(moved);
        }}><i style={{width:11,height:11,borderRadius:'50%',background:p.id==='end'?'#282b34':'#ffe6b3',border:'2px solid #fff6e5',boxShadow:'0 0 0 1.5px #282b34'}}/></button>)}
    </>}
  </div>;
}
