import React, {useLayoutEffect, useRef, useState} from 'react';
import {Control, Values, clamp, displayValue, quantize} from './model';

const channels = [
  {id:'rgb', name:'RGB', color:'#d7d4ce'},{id:'red',name:'红',color:'#d68c87'},
  {id:'green',name:'绿',color:'#8db58c'},{id:'blue',name:'蓝',color:'#81a4d2'},
];
const bands = [
  ['red','红色','#d57571'],['orange','橙色','#daa06f'],['yellow','黄色','#c8b764'],['green','绿色','#85ad7e'],
  ['aqua','青色','#73aaa8'],['blue','蓝色','#7f9fc7'],['purple','紫色','#a18cc1'],['magenta','洋红','#bf83a8'],
];
const zones = [['shadows','阴影'],['midtones','中间调'],['highlights','高光']];
export type AdvancedProps = {
  list: Control[]; initialKey?:string; values: Values; locks: string[]; disabled: boolean; version:string; rows:(controls:Control[])=>React.ReactNode;
  draft:(patch:Values)=>void; commit:(patch:Values,label:string)=>void; cancel:()=>void;
};

export function CurveEditor({list,initialKey,values,locks,disabled,version,rows,draft,commit,cancel}: AdvancedProps) {
  const [channel,setChannel] = useState(initialKey?.split('_')[1]??'rgb');
  const active = useRef<Control | null>(null);
  useLayoutEffect(()=>{active.current=null;},[version]);
  const controls=list.filter(c=>c.key.startsWith('curve_'+channel+'_'));
  const controlsByAnchor=[25,50,75].map(a=>controls.find(c=>c.key.endsWith('_'+a)));
  if(controlsByAnchor.some(c=>!c)) return <>{rows(list)}</>;
  const points=[0,...controlsByAnchor.map((c,i)=>(i+1)/4+(values[c!.key]??c!.initial)*.00235),1];
  const slopes=points.map((value,i)=>{
    if(i===0)return points[1]-value;
    if(i===4)return value-points[3];
    const left=value-points[i-1],right=points[i+1]-value;
    return left*right<=0?0:2*left*right/(left+right);
  });
  const line=Array.from({length:65},(_,i)=>{
    const x=i/64, segment=Math.min(3,Math.floor(x*4)),t=x*4-segment,t2=t*t,t3=t2*t;
    const y=clamp((2*t3-3*t2+1)*points[segment]+(t3-2*t2+t)*slopes[segment]+(-2*t3+3*t2)*points[segment+1]+(t3-t2)*slopes[segment+1],0,1);
    return (i?'L':'M')+(x*200).toFixed(2)+' '+(100-y*100).toFixed(2);
  }).join(' ');
  const valueAt=(e:React.PointerEvent<SVGSVGElement>,c:Control)=>{
    const rect=e.currentTarget.getBoundingClientRect();
    const y=1-(e.clientY-rect.top)/rect.height, anchor=Number(c.key.split('_').pop())/100;
    return quantize((y-anchor)/.00235,c);
  };
  return <div className="advanced-group">
    <div className="channel-tabs" aria-label="曲线通道">{channels.map(c=><button key={c.id} className={channel===c.id?'active':''} aria-pressed={channel===c.id} onClick={()=>setChannel(c.id)}>{c.name}</button>)}</div>
    <svg className="curve-editor" viewBox="0 0 200 100" preserveAspectRatio="none" role="img" aria-label="色调曲线，拖动控制点或使用下方数值调整"
      onPointerDown={e=>{
        if(disabled||e.button!==0)return;
        const rect=e.currentTarget.getBoundingClientRect(),x=clamp((e.clientX-rect.left)/rect.width,0,1);
        const idx=clamp(Math.round(x*4)-1,0,2),c=controlsByAnchor[idx]!;
        if(locks.includes(c.key))return;
        e.currentTarget.setPointerCapture(e.pointerId); active.current=c; draft({[c.key]:valueAt(e,c)});
      }} onPointerMove={e=>{if(active.current)draft({[active.current.key]:valueAt(e,active.current)});}}
      onPointerUp={e=>{const c=active.current; if(c){active.current=null;commit({[c.key]:valueAt(e,c)},'调整'+c.label+'曲线');}}}
      onPointerCancel={()=>{if(active.current){active.current=null;cancel();}}}>
      <path className="curve-grid" d="M0 25h200 M0 50h200 M0 75h200 M50 0v100 M100 0v100 M150 0v100"/>
      <path className="curve-reference" d="M0 100L200 0"/>
      <path d={line} fill="none" stroke={channels.find(c=>c.id===channel)?.color} strokeWidth="1.5" vectorEffect="non-scaling-stroke"/>
      {points.slice(1,-1).map((p,i)=><ellipse key={i} cx={(i+1)*50} cy={100-p*100} rx="3" ry="2.7" fill={locks.includes(controlsByAnchor[i]!.key)?'#848484':'#f0d0a5'} stroke="#46403a" strokeWidth=".7"/>)}
    </svg>
    {rows(controls)}
  </div>;
}

export function HslEditor({list,initialKey,rows}: AdvancedProps) {
  const [band,setBand]=useState(initialKey?.split('_')[1]??'red');
  const controls=list.filter(c=>c.key.startsWith('hsl_'+band+'_'));
  if(!controls.length)return <>{rows(list)}</>;
  return <div className="advanced-group">
    <div className="band-select" aria-label="HSL 颜色通道">{bands.map(([id,name,color])=><button className={band===id?'active':''} key={id} title={name} aria-label={'选择'+name+'通道'} aria-pressed={band===id}
      onClick={()=>setBand(id)}><i style={{backgroundColor:color}}/></button>)}</div>
    {rows(controls)}
  </div>;
}

export function GradingEditor({list,initialKey,values,locks,disabled,version,rows,draft,commit,cancel}: AdvancedProps) {
  const [zone,setZone]=useState(initialKey?.split('_')[1]??'shadows');
  const moving=useRef(false);
  useLayoutEffect(()=>{moving.current=false;},[version]);
  const controls=list.filter(c=>c.key.startsWith('grade_'+zone+'_'));
  const hue=controls.find(c=>c.key.endsWith('_hue')),sat=controls.find(c=>c.key.endsWith('_sat'));
  if(!hue||!sat)return <>{rows(list)}</>;
  const h=values[hue.key]??0, s=values[sat.key]??0, angle=h*Math.PI/180;
  const point={left:(50+Math.cos(angle)*s*.45)+'%',top:(50+Math.sin(angle)*s*.45)+'%'};
  const patch=(e:React.PointerEvent<HTMLDivElement>)=>{
    const r=e.currentTarget.getBoundingClientRect(),x=(e.clientX-r.left-r.width/2)/(r.width*.45),y=(e.clientY-r.top-r.height/2)/(r.height*.45);
    const next:Values={};
    if(!locks.includes(hue.key))next[hue.key]=quantize((Math.atan2(y,x)*180/Math.PI+360)%360,hue);
    if(!locks.includes(sat.key))next[sat.key]=quantize(clamp(Math.hypot(x,y),0,1)*100,sat);
    return next;
  };
  const title=zones.find(z=>z[0]===zone)?.[1] || '';
  return <div className="advanced-group">
    <div className="channel-tabs" aria-label="分级影调">{zones.map(([id,name])=><button key={id} className={id===zone?'active':''} aria-pressed={id===zone} onClick={()=>setZone(id)}>{name}</button>)}</div>
    <div className="wheel-tools"><div className="grading-wheel" role="img" aria-label={title+'色轮，可拖动或使用下方滑块'}
      onPointerDown={e=>{if(disabled||e.button!==0||[hue.key,sat.key].every(k=>locks.includes(k)))return; e.currentTarget.setPointerCapture(e.pointerId); moving.current=true; draft(patch(e));}}
      onPointerMove={e=>{if(moving.current)draft(patch(e));}} onPointerUp={e=>{if(moving.current){moving.current=false;commit(patch(e),'调整'+title+'色轮');}}}
      onPointerCancel={()=>{if(moving.current){moving.current=false;cancel();}}}><i style={point}/></div>
      <div className="wheel-values"><span>色相<b>{displayValue(h,hue)}°</b></span><span>着色强度<b>{displayValue(s,sat)}%</b></span></div></div>
    {rows(controls)}
  </div>;
}
