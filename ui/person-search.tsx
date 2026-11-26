import React,{createContext,useContext,useEffect,useRef,useState} from 'react';
import {BodyPerson,NormalizedPoint,Snapshot,clamp,invoke,messageOf} from './model';
import {decodeBodyInput,detectBody} from './body-detector';
import {detectPortraits} from './portrait-detector';

type Box=BodyPerson['box'];
type Candidate={person:number;face:boolean;body:boolean;changed:boolean;box:Box};
type Search={key:string;phase:'draw'|'busy'|'preview'|'empty'|'saving';box?:Box;candidate?:Candidate;token?:string;message?:string};
export function usePersonSearch(id:string|undefined,revision:number,enabled:boolean,onSaved:(state:Snapshot,index:number)=>void,manual:()=>void){
  const key=id+'/'+revision;
  const [job,setJob]=useState<Search|null>(null);
  const order=useRef(0);
  const ticket=useRef<{id:string;token:string}|null>(null);
  const current=useRef({key,enabled});current.current={key,enabled};
  const active=job?.key===key&&enabled?job:null;
  function release(){
    if(ticket.current)void invoke('personRegionCancel',ticket.current).catch(()=>{});
    ticket.current=null;
  }
  useEffect(()=>{order.current++;setJob(null);return release;},[key,enabled]);
  const cancel=()=>{order.current++;release();setJob(null);};
  const begin=()=>{order.current++;release();setJob({key,phase:'draw'});};
  async function search(box:Box){
    if(!id||!enabled)return;
    const seq=++order.current;
    const live=()=>seq===order.current&&current.current.key===key&&current.current.enabled;
    setJob({key,phase:'busy',box});
    try{
      const base={id,revision};
      const input=await invoke<{image:string;token:string}>('personRegionInput',{...base,box});
      if(!live()){void invoke('personRegionCancel',{id,token:input.token}).catch(()=>{});return;}
      ticket.current={id,token:input.token};
      const image=await decodeBodyInput(input.image);
      if(!live())return;
      // Run the two local graphs separately. A tight face-only or partly occluded crop
      // can legitimately yield one set of features without a reliable articulated body.
      const faces=await detectPortraits(image);
      if(!live())return;
      if(faces.meshes.length>1)throw Error('框内发现多张人脸，请缩小范围，只保留一位人物。');
      const pose=await detectBody(image);
      if(!live())return;
      if(pose.people>1)throw Error('框内发现多个人体，请缩小范围，只保留一位人物。');
      if(!faces.meshes.length&&!pose.people)throw Error('框内未检测到可靠的人脸或人体。');
      const candidate=await invoke<Candidate>('personRegionPreview',{...base,token:input.token,mesh:faces.meshes[0],pose:pose.subjects[0]});
      if(live())setJob({key,phase:'preview',box,candidate,token:input.token});
    }catch(e){if(live())setJob({key,phase:'empty',box,message:messageOf(e)});}
  }
  async function apply(){
    if(!id||active?.phase!=='preview')return;
    const seq=++order.current;
    setJob({...active,phase:'saving'});
    try{
      ticket.current=null;
      const result=await invoke<Snapshot&{targetPerson:number}>('personRegionSave',{id,revision,token:active.token});
      // The host may finish the confirmed write after the inspector has changed.
      // Adopt it while this photo and base revision are current, even if the overlay closed.
      if(current.current.key!==key)return;
      setJob(null);onSaved(result,result.targetPerson);
    }catch(e){if(seq===order.current)setJob({...active,phase:'empty',message:messageOf(e)});}
  }
  return {active,begin,cancel,search,apply,manual:()=>{cancel();manual();}};
}
type Controller=ReturnType<typeof usePersonSearch>;
export const PersonSearchContext=createContext<Controller|null>(null);
export function PersonSearchControls({disabled}:{disabled:boolean}){
  const search=useContext(PersonSearchContext)!;
  const job=search.active,candidate=job?.candidate;
  if(!job)return <button type="button" className="secondary small" disabled={disabled} onClick={search.begin}>框选补检人物</button>;
  return <div className="person-search-controls">
    <p role="status">{job.phase==='draw'?'在原片上拖出矩形，尽量框全目标人物的脸和双肩。':
      job.phase==='busy'?'正在本机检测框内的人脸和人体…':job.phase==='saving'?'正在保存人物…':
      job.phase==='empty'?job.message:
      '人物 '+(candidate!.person+1)+' · '+(candidate!.face&&candidate!.body?'已定位人脸和人体':candidate!.face?'仅定位到人脸':'仅定位到人体')+(candidate!.changed?'':'，照片里已有此人')+'。'}</p>
    <div className="segment-actions">
      {job.phase==='preview'&&<button className="primary small" disabled={disabled} onClick={()=>void search.apply()}>{candidate!.changed?'采用补检结果':'选中已有的人物'}</button>}
      {['empty','preview'].includes(job.phase)&&<button className="secondary small" disabled={disabled} onClick={search.begin}>重新框选</button>}
      {job.phase==='empty'&&<button className="secondary small" disabled={disabled} onClick={search.manual}>改用手绘蒙版</button>}
      <button className="secondary small" disabled={disabled||job.phase==='saving'} onClick={search.cancel}>取消补检</button>
    </div>
  </div>;
}
function bounds(a:NormalizedPoint,b:NormalizedPoint):Box{
  return {x:Math.min(a.x,b.x),y:Math.min(a.y,b.y),w:Math.abs(a.x-b.x),h:Math.abs(a.y-b.y)};
}
export function PersonSearchOverlay(){
  const search=useContext(PersonSearchContext)!;
  const start=useRef<NormalizedPoint|null>(null);
  const [pending,setPending]=useState<Box|null>(null);
  const job=search.active;
  if(!job)return null;
  const position=(e:React.PointerEvent<HTMLDivElement>)=>{
    const rect=e.currentTarget.getBoundingClientRect();
    return {x:clamp((e.clientX-rect.left)/rect.width,0,1),y:clamp((e.clientY-rect.top)/rect.height,0,1)};
  };
  const box=job.phase==='draw'?pending:job.candidate?.box||job.box;
  return <div className="person-search-overlay" aria-label="框选补检区域" onPointerDown={e=>{
    e.stopPropagation();if(job.phase!=='draw'||e.button!==0)return;
    start.current=position(e);setPending(null);e.currentTarget.setPointerCapture(e.pointerId);
  }} onPointerMove={e=>{e.stopPropagation();if(start.current)setPending(bounds(start.current,position(e)));}}
    onPointerUp={e=>{
      e.stopPropagation();if(!start.current)return;
      const rect=bounds(start.current,position(e));start.current=null;setPending(null);
      void search.search(rect);
    }} onPointerCancel={()=>{start.current=null;setPending(null);}} onDoubleClick={e=>e.stopPropagation()}>
    {box&&<div className="person-search-rect" style={{left:100*box.x+'%',top:100*box.y+'%',width:100*box.w+'%',height:100*box.h+'%'}}/>}
  </div>;
}
