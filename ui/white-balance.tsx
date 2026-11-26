import React,{useRef,useState} from 'react';
import {invoke,messageOf,requestId} from './model';
import type {NormalizedPoint,PhotoDocument,Values} from './model';

type Slot='A'|'B';
type Sample={values:Values;loupe:string;sampleBox:{x:number;y:number;w:number;h:number}};
type Candidate=NormalizedPoint&{token:string;sample?:Sample;error?:string};
type Session={key:string;next:Slot;active:Slot|null;slots:Partial<Record<Slot,Candidate>>};

export function useWhiteBalance(doc:PhotoDocument|null) {
  const key=doc?doc.photoId+'@'+doc.revision:'';
  const [state,setState]=useState<Session|null>(null);
  const live=useRef<Session|null>(null);
  const update=(next:Session|null)=>{live.current=next;setState(next);};
  const session=state?.key===key?state:null;
  function begin(){if(key)update({key,next:'A',active:null,slots:{}});}
  function select(slot:Slot|null){
    const s=live.current;
    if(s?.key===key)update({...s,active:slot,next:slot??s.next});
  }
  async function pick(x:number,y:number,frameView:string,maxEdge:number){
    const s=live.current;
    if(!s||s.key!==key||!doc)return;
    const slot=s.next,candidate={x,y,token:requestId()};
    update({...s,active:slot,next:slot==='A'&&!s.slots.B?'B':slot,slots:{...s.slots,[slot]:candidate}});
    function finish(result:{sample?:Sample;error?:string}){
      const latest=live.current;
      if(latest?.key===key&&latest.slots[slot]?.token===candidate.token)
        update({...latest,slots:{...latest.slots,[slot]:{...candidate,...result}}});
    }
    try{finish({sample:await invoke<Sample>('sampleWhiteCandidate',{id:doc.photoId,revision:doc.revision,x,y,frameView,maxEdge})});}
    catch(e){finish({error:messageOf(e)});}
  }
  const active=session?.active?session.slots[session.active]:undefined;
  const values=active?.sample?.values;
  return {session,active,values:values&&Object.keys(values).length?values:undefined,begin,select,pick,cancel:()=>update(null)};
}

type WhiteTool=ReturnType<typeof useWhiteBalance>;

export function WhitePoints({session}:{session:Session}){
  return <>{(['A','B'] as const).map(slot=>{
    const p=session.slots[slot];
    return p&&<span key={slot} className={'sample-point'+(session.active===slot?' selected':'')}
      style={{left:p.x*100+'%',top:p.y*100+'%'}}><b>{slot}</b></span>;
  })}</>;
}

export function WhiteBalancePanel({tool,doc,ready,busy,apply}:{tool:WhiteTool;doc:PhotoDocument;ready:boolean;busy:boolean;apply:()=>void}){
  const session=tool.session;
  if(!session)return null;
  const locked=['temperature','tint'].filter(key=>doc.locks.includes(key)).map(key=>key==='temperature'?'色温':'色调');
  return <section className="white-candidates" aria-label="白平衡候选">
    <div className="sample-instruction">{tool.active?.error?<span role="alert">{tool.active.error}</span>:
      <span>点照片为 {session.next} 取样。A、B 都从已保存的画面取色，方框标出取样像素。</span>}
      {!!locked.length&&<span>{locked.join('、')}已锁定</span>}</div>
    <div className="white-choices">{(['A','B'] as const).map(slot=>{
      const candidate=session.slots[slot],sample=candidate?.sample,box=sample?.sampleBox;
      return <button type="button" key={slot} className="white-choice" aria-label={'预览候选 '+slot}
        aria-pressed={session.active===slot} disabled={busy} onClick={()=>tool.select(slot)}>
        <span className="white-loupe">{sample&&box?<><img src={sample.loupe} alt={'候选 '+slot+' 取样放大'} draggable={false}/>
          <i style={{left:box.x*100+'%',top:box.y*100+'%',width:box.w*100+'%',height:box.h*100+'%'}}/></>:slot}</span>
        <span><b>候选 {slot}</b><small>{sample?
          '色温 '+(sample.values.temperature??doc.parameters.temperature)+' · 色调 '+(sample.values.tint??doc.parameters.tint):
          candidate?.error?'重新取样':candidate?'读取中…':'待取样'}</small></span>
      </button>;
    })}</div>
    <div className="sample-actions"><button className="secondary small" disabled={busy} aria-pressed={!session.active} onClick={()=>tool.select(null)}>已保存效果</button>
      <button className="secondary small" disabled={busy} onClick={tool.cancel}>取消白平衡</button>
      <button className="primary small" title={tool.active?.sample&&!tool.values?'取样与当前可调参数相同':undefined} disabled={busy||!ready||!tool.values} onClick={apply}>应用白平衡</button></div>
  </section>;
}
