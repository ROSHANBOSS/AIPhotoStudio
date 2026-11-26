import React,{useEffect,useLayoutEffect,useRef,useState} from 'react';
import {Control,PhotoDocument,Snapshot,displayValue,groupName,invoke,manualGroup,messageOf,publicControls,requestId} from './model';
import {frameKeys,frameRatios} from './framing';
import {pageRatios} from './frame-page';
import {usePeople} from './people';
import {Icon} from './icons';

export type AdjustmentTarget={
  key:string; panel:'manual'|'face'|'body'|'mask'|'warp'|'segment'|'frame'|'raw'|'details'; person?:number; slot?:number; title?:string;
};
type Mask={slot:number;name:string;kind:string};
type MaskSummary={masks:Mask[];hasPrimaryStrokes:boolean};
const frameFields=new Set<string>(frameKeys);

// A visit starts in the destination panel even when a different group or colour channel was previously open.
export function controlVisit(photoId:string,key:string,details:()=>void) {
  let focused=false;
  return {photoId,key,id:requestId(),focus(panel:HTMLElement) {
    if(focused)return;
    const row=panel.querySelector<HTMLElement>('[data-parameter="'+CSS.escape(key)+'"]');
    if(!row)return;
    const disclosure=row.closest('details');
    if(disclosure&&!disclosure.open)disclosure.open=true;
    if(!row.getClientRects().length)return;
    const input=row.querySelector<HTMLElement>('input[type="range"]:enabled, select:enabled') ||
      row.querySelector<HTMLElement>('button:enabled');
    focused=true;
    if(!input){details();return;}
    input.focus({preventScroll:true});
    row.scrollIntoView({block:'center',inline:'nearest'});
  }};
}
export type ControlVisit=ReturnType<typeof controlVisit>;
export function useVisitPanel<T extends HTMLElement>(visit?:ControlVisit) {
  const ref=useRef<T>(null);
  useLayoutEffect(()=>{if(ref.current)visit?.focus(ref.current);});
  return ref;
}

function maskSlot(key:string) {
  const named=/^local_m(\d+)_/.exec(key);
  return named?Number(named[1]):0;
}
function frameDisplay(c:Control,value:number,ratio:number) {
  if(c.key==='frame_mirror')return {label:c.label,value:value?'已翻转':'关闭'};
  if(c.key==='frame_ratio')return {label:c.label,value:frameRatios[value]};
  if(c.key==='frame_doc_ratio')return {label:c.label,value:pageRatios[value]};
  if(c.key==='frame_turn')return {label:'顺时针旋转',value:value*90+'°'};
  if(c.key==='frame_angle')return {label:c.label,value:displayValue(value,c)+'°'};
  if(c.key==='frame_trim_x'||c.key==='frame_trim_y')
    return {label:c.key==='frame_trim_y'?'保留高度':ratio?'裁切范围':'保留宽度',value:Number((100-value).toFixed(1))+'%'};
  return {label:c.label,value:displayValue(value,c)+'%'};
}

export function AdjustedPanel({doc,controls,busy,open}:{doc:PhotoDocument;controls:Snapshot['controls'];busy:boolean;open:(entry:AdjustmentTarget)=>void}) {
  const people=usePeople();
  const changed=controls.filter(c=>Number.isFinite(doc.parameters[c.key])&&doc.parameters[c.key]!==c.initial);
  const shown=[...publicControls(changed),...changed.filter(c=>frameFields.has(c.key)||c.key==='raw_frame')];
  const needMasks=shown.some(c=>c.key.startsWith('local_'));
  const [masks,setMasks]=useState<(MaskSummary&{version:string;error?:string})|null>(null);
  const [retry,setRetry]=useState(0);
  const version=doc.photoId+'@'+doc.revision;
  useEffect(()=>{
    if(!needMasks)return;
    let current=true;
    invoke<MaskSummary>('localData',{id:doc.photoId,revision:doc.revision,summary:true}).then(result=>{
      if(current)setMasks({version,...result});
    }).catch(error=>{if(current)setMasks({version,masks:[],hasPrimaryStrokes:false,error:messageOf(error)});});
    return ()=>{current=false;};
  },[needMasks,version,retry]);
  const maskInfo=masks?.version===version?masks:null;
  return <section className="inspector-scroll adjusted-panel">
    <h2>已调整 <span>{shown.length}</span></h2>
    {!shown.length&&<p className="local-hint">当前照片的参数均为默认值。</p>}
    {needMasks&&maskInfo?.error&&<p className="field-error" role="alert">{maskInfo.error} <button onClick={()=>setRetry(n=>n+1)}>重试读取蒙版名称</button></p>}
    <ul aria-label="已调整的参数">{shown.map(c=>{
      const key=c.key,n=doc.parameters[key];
      let scope=groupName(manualGroup(c)),label=c.label,value=displayValue(n,c);
      let target:AdjustmentTarget={key,panel:'manual'};
      if(frameFields.has(key)){
        target={key,panel:'frame'};scope='裁切与校正';
        ({label,value}=frameDisplay(c,n,doc.parameters.frame_ratio??0));
      }else if(key==='raw_frame'){
        target={key,panel:'raw'};scope='照片信息';value='第 '+(n+1)+' 轨';
      }else if(key.startsWith('portrait_p')||c.group==='人体美型'){
        const face=key.startsWith('portrait_p');
        const originalIndex=Number(/_p(\d+)_/.exec(key)?.[1]??1)-1;
        const part=(p:typeof people.all[number])=>face?p.face:p.body;
        const person=people.all.find(p=>part(p)?.index===originalIndex);
        label=label.replace(/^(人脸|人物) \d+ · /,'');
        const name=person?'人物 '+(person.index+1):(face?'原人脸 ':'原人体 ')+(originalIndex+1);
        scope=(face?'人像':'人体美型')+' · '+name;
        if(person&&part(person)?.available.includes(key)){
          target={key,panel:face?'face':'body',person:person.index};
          if(people.hidden.includes(person))scope+='（定位时恢复显示）';
        }else{
          scope+=' · 部位未定位';
          target={key,panel:'details',title:name+' · '+label};
        }
      }else if(key.startsWith('portrait_skin_')){
        scope='旧版人脸框';
        target={key,panel:doc.parameters.portrait_face_w>0&&doc.parameters.portrait_face_h>0?'face':'details'};
      }else if(key.startsWith('segment_')){
        const hasRegion=[0,1,2,3,4].some(i=>doc.parameters['segment_ref'+i]>0);
        target={key,panel:hasRegion?'segment':'details'};
      }else if(key==='local_warp'){
        target={key,panel:maskInfo?.hasPrimaryStrokes?'warp':'details'};scope='液化';
      }else if(key.startsWith('local_')){
        const slot=maskSlot(key),mask=maskInfo?.masks.find(m=>m.slot===slot);
        if(key.endsWith('_backgroundOutput'))value=n===1?'透明':'#'+(n-2).toString(16).padStart(6,'0').toUpperCase();
        if(key.endsWith('_strength'))value=n+'%';
        scope=mask?.name??(maskInfo?'原蒙版 '+(slot+1)+' · 无法定位':'正在读取蒙版…');
        if(key.endsWith('_finish')&&mask)label=mask.kind==='skin'?'协调脸颈与手臂肤色':mask.kind==='backdrop'?'淡化背景布折痕':'淡化衣褶';
        target={key,panel:mask&&(key!=='local_smooth'||maskInfo?.hasPrimaryStrokes)?'mask':'details',slot,title:scope+' · '+label};
      }
      return <li key={key}><button type="button" disabled={busy||key.startsWith('local_')&&!maskInfo} data-adjusted-key={key} onClick={()=>open(target)}>
        <span className="adjusted-name"><small>{scope}</small><strong>{label}</strong></span>
        {doc.locks.includes(key)&&<small className="adjusted-lock">已锁定</small>}
        <b>{value}</b><Icon name="right" size={12}/>
      </button></li>;
    })}</ul>
  </section>;
}
