import React,{useEffect,useRef,useState} from 'react';
import {Photo,invoke,messageOf} from './model';
import {AreaSelection,Guide,HairField,MatchPerson,PreparedSuggestions,Suggestion,prepareSuggestions} from './set-match-suggestions';
import {FaceThumbnail} from './set-match-detail';

function HairOverlay({field,label}:{field:HairField;label:string}){
  const canvas=useRef<HTMLCanvasElement>(null);
  useEffect(()=>{
    const ctx=canvas.current?.getContext('2d');if(!ctx)return;
    const bytes=atob(field.bytes),image=ctx.createImageData(field.width,field.height);
    for(let i=0;i<bytes.length;i++){
      image.data[i*4]=255;image.data[i*4+1]=176;image.data[i*4+2]=108;image.data[i*4+3]=Math.round(bytes.charCodeAt(i)*.65);
    }
    ctx.putImageData(image,0,0);
  },[field]);
  return <canvas ref={canvas} width={field.width} height={field.height} role="img" aria-label={label+'头发作用范围'}
    style={{pointerEvents:'none',position:'absolute',left:field.left/256*100+'%',top:field.top/256*100+'%',
      width:field.width/256*100+'%',height:field.height/256*100+'%'}}/>;
}

export function HairPicker({photo,label,selection,enabled,disabled,onPick,onSkip}:{
  photo:Photo;label:string;selection?:AreaSelection;enabled:boolean;disabled:boolean;
  onPick:(s:Suggestion,digest:string)=>void;onSkip?:(digest:string)=>void;
}){
  const [guide,setGuide]=useState<Guide|null>(null),[people,setPeople]=useState<PreparedSuggestions|null>(null);
  const [image,setImage]=useState<HTMLImageElement|null>(null),[message,setMessage]=useState(''),[overlay,setOverlay]=useState(true);
  const job=useRef(0),revision=photo.revision??0;
  const selected=selection?.digest===guide?.digest&&selection?.revision===revision?selection:undefined;
  const field=selected?.regions.hair?.hair;
  useEffect(()=>{
    let live=true;setGuide(null);setImage(null);setPeople(null);setMessage('正在本机查找人物…');
    void invoke<Guide>('setMatchAreaInput',{id:photo.id,revision,suggest:true,purpose:'hair'}).then(async guide=>{
      if(!live)return;setGuide(guide);
      const list=await prepareSuggestions(photo,guide,true);
      if(live){setPeople(list);setMessage(list.people.length?'':'未找到可定位的人脸。');}
    }).catch(err=>{if(live)setMessage(messageOf(err));});
    return ()=>{live=false;job.current++;};
  },[photo.id,revision]);
  async function choose(person:MatchPerson){
    if(!guide||!people||disabled||!enabled||selected?.person===person.number&&field)return;
    const seq=++job.current;
    onPick({person:person.number,regions:{}},guide.digest);
    setOverlay(true);setMessage('正在核对人物 '+person.number+' 的头发边缘…');
    try{
      const result=await people.recheck(guide,person);
      if(seq!==job.current)return;
      if(!result?.hair){setMessage('此人的头发范围不够明确，可选其他人或跳过此张。');return;}
      onPick({person:person.number,regions:{hair:result.hair}},guide.digest);setMessage('');
    }catch(err){if(seq===job.current)setMessage(messageOf(err));}
  }
  return <section className="set-area-picker" aria-label={label+'发色'}>
    <b>{label} · {photo.name}</b>
    <div className="set-area-stage">{guide&&<div className="set-area-image" style={{cursor:'default'}}>
      <img src={guide.image} alt={label+'取样图'} onLoad={e=>setImage(e.currentTarget)}/>
      {field&&overlay&&<HairOverlay field={field} label={label}/>}
    </div>}</div>
    {people&&people.people.length>0&&<div className="set-area-tags" style={{flexWrap:'wrap'}}><span>选人物</span>
      {people.people.map(p=><button type="button" key={p.number} aria-label={label+'人物 '+p.number}
        aria-pressed={selected?.person===p.number} disabled={disabled||!enabled} onClick={()=>void choose(p)}>
        <FaceThumbnail photo={image} box={p.box}/>{p.number}</button>)}</div>}
    <div className="set-area-tags" style={{flexWrap:'wrap'}}>
      {field&&<button type="button" aria-pressed={overlay} onClick={()=>setOverlay(!overlay)}>{overlay?'隐藏':'显示'}头发范围</button>}
      {onSkip&&guide&&enabled&&<button type="button" disabled={disabled} onClick={()=>{job.current++;setMessage('');onSkip(guide.digest);}}>
        {selected?.omitted?'恢复此张取样':'跳过此张发色'}</button>}
      <span role="status">{message||(!enabled?'先选基准片里的人物。':field?'橙色为所选人物的头发。':selected?.omitted?'已跳过':'点头像，查看头发范围。')}</span>
    </div>
  </section>;
}
