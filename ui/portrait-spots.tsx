import React,{useEffect,useRef,useState} from 'react';
import {BodyPerson,Preview,Snapshot,invoke,messageOf} from './model';
import {Modal} from './widgets';

type Dot={x:number;y:number;radius:number};
type Search={token:string;image:string;points:Dot[];width:number;height:number;box:BodyPerson['box']};
export function PortraitSpotDialog({id,revision,faceIndex,close,onSaved}:{
  id:string;revision:number;faceIndex:number;close:()=>void;onSaved:(state:Snapshot)=>void;
}){
  const [search,setSearch]=useState<Search|null>(null),[selected,setSelected]=useState<number[]>([]);
  const [preview,setPreview]=useState<{before:string;after:string}|null>(null),[showBefore,setBefore]=useState(false);
  const [busy,setBusy]=useState('正在查找…'),[error,setError]=useState('');
  const alive=useRef(true);
  useEffect(()=>{
    alive.current=true;let current=true,token='';
    const discard=()=>{if(token)void invoke('portraitSpotCancel',{id,token}).catch(()=>{});};
    void invoke<Search>('portraitSpotFind',{id,revision,faceIndex}).then(result=>{
      token=result.token;if(current)setSearch(result);else discard();
    })
      .catch(e=>{if(current)setError(messageOf(e));}).finally(()=>{if(current)setBusy('');});
    return ()=>{current=false;alive.current=false;discard();};
  },[id,revision,faceIndex]);
  function toggle(i:number){
    if(busy||preview)return;
    setSelected(old=>old.includes(i)?old.filter(k=>k!==i):[...old,i].sort((a,b)=>a-b));
  }
  async function review(){
    if(!search||!selected.length||busy)return;
    setBusy('正在预览…');setError('');
    try{
      const args={id,revision,faceIndex,token:search.token,selected};
      const [after,before]=await Promise.all([invoke<Preview>('portraitSpotPreview',args),invoke<Preview>('preview',{id,revision,maxEdge:1200})]);
      if(alive.current){setPreview({after:after.image,before:before.image});setBefore(false);}
    }catch(e){if(alive.current)setError(messageOf(e));}finally{if(alive.current)setBusy('');}
  }
  async function apply(){
    if(!search||!preview||busy)return;
    setBusy('正在保存…');
    try{
      const next=await invoke<Snapshot>('portraitSpotSave',{id,revision,faceIndex,token:search.token,selected});
      if(alive.current){onSaved(next);close();}
    }catch(e){if(alive.current){setError(messageOf(e));setBusy('');}}
  }
  const b=search?.box;
  const x=b?Math.max(0,b.x-.14*b.w):0,y=b?Math.max(0,b.y-.09*b.h):0;
  const w=b?Math.min(1,b.x+1.14*b.w)-x:1,h=b?Math.min(1,b.y+1.09*b.h)-y:1;
  const aspect=search?w*search.width/(h*search.height):1;
  return <Modal title={'人脸 '+(faceIndex+1)+' · 查找小瑕疵'} close={busy==='正在保存…'?()=>{}:close} className="portrait-spot-dialog">
    {search&&<><div className="portrait-spot-image" style={{aspectRatio:String(aspect),width:'min(100%, '+(52*aspect)+'vh)'}}>
      <img draggable={false} alt={preview?(showBefore?'已保存的效果':'所选点修效果'):'原片肤质与候选点'}
        src={preview?(showBefore?preview.before:preview.after):search.image}
        style={{width:100/w+'%',height:100/h+'%',left:-100*x/w+'%',top:-100*y/h+'%'}}/>
      {!preview&&search.points.map((p,i)=><button type="button" className="portrait-spot-dot" key={i}
        style={{left:100*(p.x-x)/w+'%',top:100*(p.y-y)/h+'%'}} aria-pressed={selected.includes(i)} disabled={!!busy}
        aria-label={'候选点 '+(i+1)} onClick={()=>toggle(i)}><span>{i+1}</span></button>)}
    </div>
    <p className="dialog-note" aria-live="polite">{preview?'在相同的已保存调整下比较修复前后。':search.points.length?
      '已找到 '+search.points.length+' 处局部色差。请点选想修复的编号，保留痣、雀斑和正常纹理。已选 '+selected.length+' 处。':
      '这张脸上没有找到可靠的小瑕疵候选。'}</p></>}
    {preview&&<div className="channel-tabs"><button aria-pressed={showBefore} className={showBefore?'active':''} onClick={()=>setBefore(true)}>修复前</button>
      <button aria-pressed={!showBefore} className={showBefore?'':'active'} onClick={()=>setBefore(false)}>修复后</button></div>}
    {busy&&<p role="status">{busy}</p>}{error&&<p className="field-error" role="alert">{error}</p>}
    <div className="dialog-actions"><button type="button" className="secondary" disabled={busy==='正在保存…'} onClick={close}>取消</button>
      {preview?<><button type="button" className="secondary" disabled={!!busy} onClick={()=>setPreview(null)}>返回选点</button>
        <button type="button" className="primary" disabled={!!busy} onClick={()=>void apply()}>应用点修</button></>:
        <button type="button" className="primary" disabled={!selected.length||!!busy} onClick={()=>void review()}>预览点修</button>}
    </div>
  </Modal>;
}
