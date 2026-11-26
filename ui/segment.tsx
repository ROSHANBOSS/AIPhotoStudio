import React, {useEffect, useRef, useState} from 'react';
import {NormalizedPoint, clamp, invoke, messageOf} from './model';
import {Icon} from './icons';

type Phase = 'idle' | 'foreground' | 'background' | 'loading' | 'ready';
type Result = {image:string;segmentToken:string;revision:number;id?:string};
type Selection = {
  id:string;revision:number;phase:Phase;foreground?:NormalizedPoint;background?:NormalizedPoint;
  result?:Result;error?:string;
};
const idle=(id:string,revision:number):Selection=>({id,revision,phase:'idle'});

export function useSegmentation(id:string|null,revision:number,maxEdge:number) {
  const photoId=id||'';
  const [state,setState]=useState<Selection>(()=>idle(photoId,revision));
  const [linked,setLinked]=useState(false);
  const job=useRef(0);
  const currentKey=useRef('');currentKey.current=photoId+'@'+revision;
  const stateRef=useRef(state);stateRef.current=state;
  const update=(selection:Selection)=>{stateRef.current=selection;setState(selection);};
  useEffect(()=>{
    job.current++;update(idle(photoId,revision));
  },[photoId,revision]);
  useEffect(()=>{setLinked(false);},[photoId]);
  const current=state.id===photoId&&state.revision===revision?state:idle(photoId,revision);
  const start=()=>{
    if(!photoId)return;
    job.current++;update({id:photoId,revision,phase:'foreground'});
  };
  const cancel=()=>{job.current++;update(idle(photoId,revision));};
  const pick=async(point:NormalizedPoint)=>{
    const s=stateRef.current;
    if(!photoId||s.id!==photoId||s.revision!==revision)return;
    if(s.phase==='foreground'){
      update({...s,phase:'background',foreground:point,error:undefined});return;
    }
    if(s.phase!=='background'||!s.foreground)return;
    if(s.foreground.x===point.x&&s.foreground.y===point.y){
      update({...s,error:'请在属于背景的另一个位置点选。'});return;
    }
    const epoch=++job.current, key=currentKey.current;
    const input={...s,background:point,error:undefined};
    update({...input,phase:'loading'});
    try{
      const result=await invoke<Result>('segmentPreview',{id:photoId,revision,foreground:s.foreground,background:point,maxEdge});
      if(job.current!==epoch||currentKey.current!==key)return;
      if(!result||result.revision!==revision||(result.id&&result.id!==photoId)||typeof result.segmentToken!=='string'||!result.segmentToken||
        typeof result.image!=='string'||!/^data:image\/png;base64,/i.test(result.image))throw new Error('这次分割预览未能对应当前照片，请重新点选。');
      update({...input,phase:'ready',result});
    }catch(error){
      if(job.current===epoch&&currentKey.current===key)update({...input,phase:'background',background:undefined,error:messageOf(error)});
    }
  };
  const save=async(writer:(id:string,revision:number,token:string)=>Promise<boolean>)=>{
    const s=stateRef.current;
    if(s.phase!=='ready'||!s.result||s.id!==photoId||s.revision!==revision||s.result.revision!==revision)return;
    if(await writer(s.id,s.revision,s.result.segmentToken))cancel();
  };
  return {state:current,start,cancel,pick,save,linked,setLinked,active:current.phase!=='idle'};
}
export type SegmentEditor=ReturnType<typeof useSegmentation>;

export function SegmentOverlay({editor,busy}:{editor:SegmentEditor;busy:boolean}){
  const {state}=editor;
  const [cursor,setCursor]=useState<NormalizedPoint>({x:.5,y:.5});
  const selecting=!busy&&(state.phase==='foreground'||state.phase==='background');
  const choose=(x:number,y:number)=>void editor.pick({x:Number(clamp(x,0,1).toFixed(5)),y:Number(clamp(y,0,1).toFixed(5))});
  if(state.phase==='idle')return null;
  const point=(p:NormalizedPoint|undefined,label:string,kind:string)=>p&&<span className={'seed '+kind} style={{left:p.x*100+'%',top:p.y*100+'%'}}><i/><b>{label}</b></span>;
  return <div className={'segment-overlay '+(selecting?'interactive':'')} tabIndex={selecting?0:-1}
    role={selecting?'button':'img'} aria-label={selecting?(state.phase==='foreground'?'在原片上点选前景':'在原片上点选背景'):'前景分割预览'}
    onPointerDown={e=>{if(selecting)e.stopPropagation();}} onPointerUp={e=>{if(selecting)e.stopPropagation();}}
    onDoubleClick={e=>e.stopPropagation()}
    onClick={e=>{
      if(!selecting)return;
      e.stopPropagation();
      const r=e.currentTarget.getBoundingClientRect();
      const x=(e.clientX-r.left)/r.width,y=(e.clientY-r.top)/r.height;
      setCursor({x:clamp(x,0,1),y:clamp(y,0,1)});choose(x,y);
    }}
    onKeyDown={e=>{
      if(!selecting)return;
      const move=.01, step=e.shiftKey?move/5:move;
      if(e.key==='Enter'||e.key===' '){e.preventDefault();e.stopPropagation();choose(cursor.x,cursor.y);return;}
      if(['ArrowLeft','ArrowRight','ArrowUp','ArrowDown'].includes(e.key)){
        e.preventDefault();e.stopPropagation();
        setCursor(p=>({x:clamp(p.x+(e.key==='ArrowLeft'?-step:e.key==='ArrowRight'?step:0),0,1),y:clamp(p.y+(e.key==='ArrowUp'?-step:e.key==='ArrowDown'?step:0),0,1)}));
      }
    }}>
    {state.result&&<img className="segment-result" src={state.result.image} alt="原片上的前景分割覆盖图" draggable={false}/>}
    {point(state.foreground,'前景','fg')}{point(state.background,'背景','bg')}
    {selecting&&<i className="seed-cursor" style={{left:cursor.x*100+'%',top:cursor.y*100+'%'}}/>}
  </div>;
}

export function SegmentPanel({editor,enabled,stored,linkAvailable,onStart,onSave,onBackdrop}:{
  editor:SegmentEditor;enabled:boolean;stored:boolean;linkAvailable:boolean;onStart:()=>void;onSave:()=>void;onBackdrop:(cutout:boolean)=>void;
}){
  const s=editor.state;
  const description=s.phase==='foreground'?'在原片中点击属于前景的代表位置。':
    s.phase==='background'?'前景已标记，请再点击一个属于背景的位置。':
    s.phase==='loading'?'正在根据取样点计算前景和背景…':
    s.phase==='ready'?'覆盖颜色表示分出的前景。确认边界后应用，即可分别调整曝光。':
    stored?'当前照片已有前景与背景分割，可继续调整曝光。':'在原片中依次标记前景与背景各一点。确认分割后，可分别控制两边的曝光。';
  return <section className="local-section segment-panel"><b>前景与背景</b>
    <p role="status">{description}</p>
    {s.error&&<p role="alert" className="field-error">{s.error}</p>}
    {s.phase==='idle'?<button className="secondary small" disabled={!enabled} onClick={onStart}><Icon name="target" size={14}/>{stored?'重新点选分割':'点选前景与背景'}</button>:
      <div className="segment-actions"><button className="secondary small" disabled={!enabled} onClick={onStart}>重选</button><button className="secondary small" disabled={!enabled} onClick={editor.cancel}>取消</button>
        {s.phase==='ready'&&<button className="primary small" disabled={!enabled} onClick={onSave}><Icon name="check" size={12}/>应用分割</button>}</div>}
    {stored&&s.phase==='idle'&&<label className="segment-link"><input type="checkbox" disabled={!enabled||!linkAvailable} checked={editor.linked&&linkAvailable} onChange={e=>editor.setLinked(e.target.checked)}/>联动前景与背景曝光</label>}
    {stored&&s.phase==='idle'&&<div className="segment-actions"><button className="secondary small" disabled={!enabled} onClick={()=>onBackdrop(false)}>用此分区虚化背景</button>
      <button className="secondary small" disabled={!enabled} onClick={()=>onBackdrop(true)}>用此分区抠图</button></div>}
  </section>;
}
