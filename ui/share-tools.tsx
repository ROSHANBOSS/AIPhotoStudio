import React,{useEffect,useRef,useState} from 'react';
import {clamp,invoke,messageOf,NormalizedPoint,Preview,requestId} from './model';
import type {ExportSettings} from './export-settings';
import {MatteFilter} from './transparency';

// Frame is the part of the source visible in a crop, or the part of the paper occupied by the photo.
function frame(value:ExportSettings,width:number,height:number){
  const ratio=(value.outputWidth||width)/(value.outputHeight||height)/(width/height);
  let w=1,h=1;
  if(value.layout==='crop'){w=Math.min(1,ratio);h=Math.min(1,1/ratio);}
  if(value.layout==='pad'){w=Math.min(1,1/ratio);h=Math.min(1,ratio);}
  return {x:(1-w)*(value.positionX??.5),y:(1-h)*(value.positionY??.5),w,h};
}
const unit=(n:number)=>clamp(n,0,1);

export function PositionOutput({value,change,image,width,height}:{value:ExportSettings;change:(s:ExportSettings)=>void;image:string;width:number;height:number}){
  const box=frame(value,width,height),pad=value.layout==='pad';
  const drag=useRef<{u:number;v:number;x:number;y:number}|null>(null);
  const ratio=pad?(value.outputWidth||1)/(value.outputHeight||1):width/height;
  const paper=pad&&value.background&&value.background!=='transparent'?value.background:value.format==='jpeg'?(value.matte||'#ffffff'):undefined;
  const position=(e:React.PointerEvent<HTMLDivElement>)=>{
    const b=e.currentTarget.getBoundingClientRect();return {x:(e.clientX-b.left)/b.width,y:(e.clientY-b.top)/b.height};
  };
  const place=(x:number,y:number)=>change({...value,positionX:unit(x),positionY:unit(y)});
  return <div className="output-framing">{paper&&<MatteFilter id="export-layout-paper" color={paper}/>}<div className="output-stage" aria-label={pad?'调整加边位置':'移动取景框'}
    style={{aspectRatio:String(ratio),width:'min(100%, '+(190*ratio)+'px)',backgroundColor:paper}}
    onPointerDown={e=>{
      if(e.button!==0)return;
      e.currentTarget.setPointerCapture(e.pointerId);
      const p=position(e),x=box.w===1?.5:unit((p.x-box.w/2)/(1-box.w)),y=box.h===1?.5:unit((p.y-box.h/2)/(1-box.h));
      drag.current={u:p.x,v:p.y,x,y};place(x,y);
    }} onPointerMove={e=>{const d=drag.current;if(d){const p=position(e);place(box.w===1?.5:d.x+(p.x-d.u)/(1-box.w),box.h===1?.5:d.y+(p.y-d.v)/(1-box.h));}}}
    onPointerUp={()=>{drag.current=null;}} onPointerCancel={()=>{drag.current=null;}}>
    {image&&<img draggable={false} src={image} alt="导出取景预览" style={{filter:paper?'url(#export-layout-paper)':undefined,...(pad?{left:box.x*100+'%',top:box.y*100+'%',width:box.w*100+'%',height:box.h*100+'%'}:{})}}/>}
    {!pad&&<i className="output-crop" style={{left:box.x*100+'%',top:box.y*100+'%',width:box.w*100+'%',height:box.h*100+'%'}}/>}
  </div><div className="output-position">{[['水平位置','positionX',box.w],['垂直位置','positionY',box.h]].map(([label,key,span])=>Number(span)<1&&<label key={key}>{label}<input type="range" min={0} max={100} step={1} aria-label={String(label)}
    value={Math.round(100*(value[key as 'positionX'|'positionY']??.5))} onChange={e=>change({...value,[key]:e.target.valueAsNumber/100})}/></label>)}</div></div>;
}

type ProofSample={x:number;y:number;width:number;height:number;before:string;after:string};
export function QualityProof({id,revision,options,width,height,point,select}:{id:string;revision:number;options:ExportSettings;width:number;height:number;point:NormalizedPoint;select:(p:NormalizedPoint)=>void}){
  const [view,setView]=useState<{ticket:string;key:string;image:string;width:number;height:number}>(),[sample,setSample]=useState<ProofSample>(),[error,setError]=useState('');
  const key=JSON.stringify(options),box=frame(options,width,height);
  const px=unit(options.layout==='crop'?(point.x-box.x)/box.w:point.x*box.w+box.x);
  const py=unit(options.layout==='crop'?(point.y-box.y)/box.h:point.y*box.h+box.y);
  useEffect(()=>{
    const ticket=requestId();let live=true;setView(undefined);setError('');
    const timer=window.setTimeout(()=>void invoke<Preview>('prepareShareProof',{id,revision,ticket,...JSON.parse(key)}).then(next=>{if(live)setView({...next,key,ticket});})
      .catch(e=>{if(live)setError(messageOf(e));}),180);
    return ()=>{live=false;clearTimeout(timer);void invoke('releaseShareProof',{ticket}).catch(()=>{});};
  },[id,revision,key]);
  useEffect(()=>{
    setSample(undefined);
    if(!view||view.key!==key)return;
    let live=true;setError('');
    void invoke<ProofSample>('shareProofSample',{id,revision,ticket:view.ticket,x:px,y:py}).then(next=>{if(live)setSample(next);})
      .catch(e=>{if(live)setError(messageOf(e));});
    return ()=>{live=false;};
  },[id,revision,view,key,px,py]);
  const pick=(x:number,y:number)=>select(options.layout==='crop'?{x:x*box.w+box.x,y:y*box.h+box.y}:{x:(x-box.x)/box.w,y:(y-box.y)/box.h});
  return <div className="share-proof">{view?.key===key&&<button type="button" className="proof-map" aria-label="选择压缩对照位置" onClick={e=>{
    const b=e.currentTarget.getBoundingClientRect();if(e.detail)pick(unit((e.clientX-b.left)/b.width),unit((e.clientY-b.top)/b.height));
  }} onKeyDown={e=>{
    const d=({ArrowLeft:[-1,0],ArrowRight:[1,0],ArrowUp:[0,-1],ArrowDown:[0,1]} as Record<string,number[]>)[e.key];
    if(d){e.preventDefault();pick(unit(px+d[0]*.05),unit(py+d[1]*.05));}
  }}><img draggable={false} src={view.image} alt="点击成品选择人脸、标题或其他细节"/>
    {sample&&<i style={{left:sample.x/view.width*100+'%',top:sample.y/view.height*100+'%',width:sample.width/view.width*100+'%',height:sample.height/view.height*100+'%'}}/>}</button>}
    {sample&&view?.key===key?<div className="proof-pair" data-x={sample.x} data-y={sample.y}>{([[sample.before,'压缩前'],[sample.after,'实际 JPEG']] as const).map(([src,label])=><figure key={label}>
      <img src={src} alt={label+'的选中细节'} style={{width:sample.width/(window.devicePixelRatio||1)}}/><figcaption>{label} · 100%</figcaption></figure>)}</div>:<small role="status">{error||'正在读取 JPEG 原像素…'}</small>}</div>;
}

type LastExport={available:boolean;changed:boolean;path:string;width:number;height:number;bytes:number};
export function LastOutput({id,revision}:{id:string;revision:number}){
  const [last,setLast]=useState<LastExport|null>(),[preview,setPreview]=useState(''),[error,setError]=useState('');
  useEffect(()=>{
    let live=true;
    const load=()=>void invoke<LastExport|null>('lastSingleExport',{id,revision}).then(item=>{if(live)setLast(item);}).catch(e=>{if(live)setError(messageOf(e));});
    load();window.addEventListener('focus',load);return()=>{live=false;window.removeEventListener('focus',load);};
  },[id,revision]);
  if(!last)return error?<p role="alert">{error}</p>:null;
  return <section className="last-output"><strong>上次单张导出</strong><span>{last.available?(last.changed?'导出后已修改画面':'与当前编辑一致'):'原位置的成品已移动、删除或被替换'}</span>
    <small>{last.path} · {last.width} × {last.height} px · {last.bytes.toLocaleString()} 字节</small>
    <button type="button" className="secondary small" disabled={!last.available} onClick={async()=>{
      if(preview){setPreview('');return;}
      try{setError('');const result=await invoke<Preview>('openLastSingleExport',{id,revision});setPreview(result.image);}
      catch(e){setError(messageOf(e));setLast(await invoke<LastExport|null>('lastSingleExport',{id,revision}).catch(()=>null));}
    }}>{preview?'收起上次成品':'打开上次成品'}</button>
    {preview&&<img src={preview} alt="上次导出的成品"/>}{error&&<small role="alert">{error}</small>}</section>;
}
