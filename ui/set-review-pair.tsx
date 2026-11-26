import React,{useMemo,useState} from 'react';
import {Photo} from './model';
import {nearbyCaptures} from './set-review-data';

export function ComparisonPicker({anchor,photos,groups,initial,compare,close}:{
  anchor:Photo;photos:Photo[];groups:Photo[][];initial:string[];compare:(ids:string[])=>void;close:()=>void;
}){
  const [search,setSearch]=useState(''),[chosen,setChosen]=useState('');
  const [added,setAdded]=useState(()=>initial.filter(id=>id!==anchor.id));
  const selected=useMemo(()=>new Set(added),[added]);
  const byId=useMemo(()=>new Map(photos.map(p=>[p.id,p])),[photos]);
  const nearby=useMemo(()=>nearbyCaptures(anchor,photos,groups),[anchor,photos,groups]);
  const matches=useMemo(()=>{
    const needle=search.trim().toLocaleLowerCase(),near=new Set(nearby.map(item=>item.photo.id));
    const found:Photo[]=[];
    for(const photo of photos){
      if(photo.id!==anchor.id&&!selected.has(photo.id)&&!near.has(photo.id)&&photo.path.toLocaleLowerCase().includes(needle))found.push(photo);
      if(found.length>=80)break;
    }
    return found;
  },[anchor,photos,nearby,search,selected]);
  const adjacent=nearby.filter(item=>!selected.has(item.photo.id)&&item.photo.path.toLocaleLowerCase().includes(search.trim().toLocaleLowerCase()));
  const choices=[...adjacent.map(item=>item.photo),...matches];
  const other=choices.find(p=>p.id===chosen)||choices[0];
  return <div className="set-tools" aria-label="人工构图复核">
    <strong>{anchor.name}</strong>
    <label>查找对照片 <input type="search" value={search} placeholder="文件名或文件夹" onChange={e=>setSearch(e.target.value)}/></label>
    <label>对照片 <select aria-label="对照片" value={other?.id||''} onChange={e=>setChosen(e.target.value)}>
      {adjacent.length>0&&<optgroup label="同机同目录，一分钟内拍摄，未归为同组">
        {adjacent.map(({photo,seconds})=><option key={photo.id} value={photo.id}>{photo.name} · 相差 {seconds} 秒</option>)}</optgroup>}
      {matches.length>0&&<optgroup label="其他照片">{matches.map(photo=><option key={photo.id} value={photo.id} title={photo.path}>{photo.name}</option>)}</optgroup>}
    </select></label>
    <button className="secondary small" disabled={!other} onClick={()=>other&&setAdded([...added,other.id])}>加入手选组</button>
    {added.map(id=><button className="text-button" key={id} aria-label={'移出 '+byId.get(id)?.name}
      onClick={()=>setAdded(added.filter(value=>value!==id))}>{byId.get(id)?.name} ×</button>)}
    <button className="secondary small" disabled={!added.length&&!other}
      onClick={()=>compare([anchor.id,...(added.length?added:other?[other.id]:[])])}>
      {added.length?'查看手选组 · '+(added.length+1)+' 张':'并排比较这两张'}</button>
    {added.length>0&&<small>手选组保留到关闭审片窗口。</small>}
    <button className="text-button" onClick={close}>收起</button>
  </div>;
}
