import React,{createContext,useContext,useMemo,useState} from 'react';
import {BodyPerson,Snapshot,invoke} from './model';

export type PeopleView='face'|'body'|'ai'|'mask';
type Box=BodyPerson['box'];
type Person={index:number;body?:BodyPerson;face?:BodyPerson};
type Evidence={index:number;box:Box};
type Identity={body?:Evidence;face?:Evidence};
type Preference={active?:Identity|null;hidden:Identity[]};

export function photoPeople(snapshot:Snapshot|null):Person[]{
  const bodies=snapshot?.bodyPeople??[],faces=snapshot?.portraitPeople??[];
  const links=snapshot?.people??[
    ...bodies.map(p=>({index:p.index,bodyIndex:p.index,faceIndex:null})),
    ...faces.map((p,i)=>({index:bodies.length+i,bodyIndex:null,faceIndex:p.index}))];
  return links.map(link=>({index:link.index,body:bodies.find(p=>p.index===link.bodyIndex),face:faces.find(p=>p.index===link.faceIndex)}));
}

function identity(person:Person):Identity{
  const evidence=(p?:BodyPerson)=>p?{index:p.index,box:p.box}:undefined;
  return {body:evidence(person.body),face:evidence(person.face)};
}
function sameEvidence(old:Evidence,current:BodyPerson){
  if(old.index!==current.index||!old.box)return false;
  const a=old.box,b=current.box;
  const w=Math.max(0,Math.min(a.x+a.w,b.x+b.w)-Math.max(a.x,b.x));
  const h=Math.max(0,Math.min(a.y+a.h,b.y+b.h)-Math.max(a.y,b.y));
  return w*h>.55*(a.w*a.h+b.w*b.h-w*h);
}
function matches(id:Identity,p:Person){
  const comparable=(['face','body'] as const).filter(k=>id?.[k]&&p[k]);
  return comparable.length>0&&comparable.every(k=>sameEvidence(id[k]!,p[k]!));
}
function readPreference(key:string,stored?:string|null):Preference{
  let legacy:string|null=null;
  try{legacy=localStorage.getItem(key);}catch{/* The library preference is also available offline. */}
  for(const raw of [stored,legacy]){
    if(!raw)continue;
    try{
      const value=JSON.parse(raw);
      if(value&&Array.isArray(value.hidden))return {active:value.active,hidden:value.hidden.filter((a:Identity)=>a?.face||a?.body).slice(0,48)};
    }catch{/* Try the other available preference. */}
  }
  return {hidden:[]};
}
export function usePhotoPeople(snapshot:Snapshot|null,invalidate:()=>void){
  const key='aiphoto/people/1/'+JSON.stringify([snapshot?.dataRoot,snapshot?.selectedId]);
  const opened=useMemo(()=>readPreference(key,snapshot?.peoplePreference),[key,snapshot?.peoplePreference]);
  const [current,setCurrent]=useState<{key:string;value:Preference}|null>(null);
  const prefs=current?.key===key?current.value:opened;
  const all=useMemo(()=>photoPeople(snapshot),[snapshot]);
  const hidden=all.filter(p=>prefs.hidden.some(id=>matches(id,p)));
  const visible=all.filter(p=>!hidden.includes(p));
  const active=prefs.active===undefined?(visible.length===1?visible[0]:undefined):
    prefs.active===null?undefined:visible.find(p=>matches(prefs.active!,p));
  function save(value:Preference){
    invalidate();setCurrent({key,value});
    try{localStorage.setItem(key,JSON.stringify(value));}catch{/* In-memory selection remains available. */}
    if(snapshot?.libraryTools&&snapshot.selectedId)void invoke('libraryPeople',{id:snapshot.selectedId,preference:JSON.stringify(value)}).catch(()=>{});
  }
  function select(p:Person|undefined){
    save({...prefs,active:p?identity(p):null,hidden:p?prefs.hidden.filter(id=>!matches(id,p)):prefs.hidden});
  }
  return {all,visible,hidden,active,
    choose:(index:number)=>{if(active?.index!==index)select(visible.find(p=>p.index===index));},
    focus:(next:Snapshot,index:number)=>{const p=photoPeople(next).find(p=>p.index===index);if(p)select(p);},
    ignore:()=>{if(active)save({active:null,hidden:[...prefs.hidden,identity(active)].slice(-48)});},
    restore:(index:number)=>{const p=hidden.find(p=>p.index===index);if(p)select(p);}
  };
}
type Selection=ReturnType<typeof usePhotoPeople>;
export const PeopleContext=createContext<Selection|null>(null);
export const usePeople=()=>useContext(PeopleContext)!;

function nameOf(p:Person,view:PeopleView){
  return (view==='face'&&p.face?'人脸 ':'人物 ')+(p.index+1);
}
export function PeopleChoices({view,disabled,manage=false}:{view:PeopleView;disabled:boolean;manage?:boolean}){
  const s=usePeople();
  if(!s.all.length)return null;
  return <div className="people-selection">
    {s.visible.length>0&&<div className="person-choices" role="group" aria-label="选择当前人物">
      {s.visible.map(p=><button type="button" key={p.index} disabled={disabled} aria-pressed={s.active?.index===p.index}
        onClick={()=>s.choose(p.index)}>{nameOf(p,view)}</button>)}</div>}
    {manage&&<div className="people-manage">
      {s.active&&<button className="text-button" type="button" disabled={disabled} onClick={s.ignore}>忽略当前人物</button>}
      {s.hidden.length>0&&<details><summary>已忽略 {s.hidden.length} 人</summary>
        {s.hidden.map(p=><button key={p.index} type="button" className="text-button" disabled={disabled}
          onClick={()=>s.restore(p.index)}>恢复人物 {p.index+1}</button>)}</details>}
    </div>}
    {s.active&&((view==='face'&&!s.active.face)||(view==='body'&&!s.active.body))&&
      <p className="body-scope">人物 {s.active.index+1} 尚未可靠定位{view==='face'?'人脸':'身体'}。可框选这位人物补检。</p>}
  </div>;
}
export function PeopleBoxes({view,disabled,passive=false}:{view:PeopleView;disabled:boolean;passive?:boolean}){
  const s=usePeople();
  const rows=(passive?(s.active?[s.active]:[]):s.visible).map(p=>({p,box:(view==='face'?(p.face||p.body):(p.body||p.face))!.box}));
  rows.sort((a,b)=>b.box.w*b.box.h-a.box.w*a.box.h);
  return <div className={'person-boxes'+(passive?' passive':'')}>{rows.map(({p,box})=>{
    const style={left:box.x*100+'%',top:box.y*100+'%',width:box.w*100+'%',height:box.h*100+'%'};
    const name=nameOf(p,view);
    if(passive)return <div key={p.index} className="person-box picked-person" style={style}><span>{name}</span></div>;
    return <button type="button" key={p.index} className="person-box" aria-pressed={s.active?.index===p.index}
      disabled={disabled} aria-label={'选择画面中的'+name} style={style} onPointerDown={e=>e.stopPropagation()}
      onDoubleClick={e=>e.stopPropagation()} onClick={e=>{e.stopPropagation();s.choose(p.index);}}><span>{name}</span></button>;
  })}</div>;
}
