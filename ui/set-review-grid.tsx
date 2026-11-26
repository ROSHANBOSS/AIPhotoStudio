import React,{useEffect,useMemo,useState} from 'react';
import {Photo,invoke,messageOf} from './model';
import {GridExposure,ReviewRow,exposureEvent} from './set-review-data';

export type ExposureScope='all'|'over'|'under';
export function useGridExposure(photos:Photo[],enabled:boolean){
  const [scope,setScope]=useState<ExposureScope>('all'),[epoch,setEpoch]=useState(0);
  const [found,setFound]=useState<ReadonlySet<string>>(new Set()),[loading,setLoading]=useState(false),[error,setError]=useState('');
  useEffect(()=>{
    const changed=()=>setEpoch(v=>v+1);
    window.addEventListener(exposureEvent,changed);
    return ()=>window.removeEventListener(exposureEvent,changed);
  },[]);
  useEffect(()=>{
    let live=true;
    setError('');
    if(!enabled||scope==='all'){setLoading(false);return;}
    setLoading(true);setFound(new Set());
    void (async()=>{
      const selected=new Set<string>();
      for(let start=0;start<photos.length;start+=400){
        if(!live)return;
        const members=photos.slice(start,start+400);
        const rows=await invoke<ReviewRow[]>('setReviewRows',{ids:members.map(p=>p.id),marksOnly:true,exposure:true});
        const frames=new Map(members.map(p=>[p.id,Number(p.selectedRawFrame||0)]));
        for(const row of rows)if(row.exposure?.[scope]&&frames.get(row.id)===row.exposure.frame)selected.add(row.id);
      }
      if(live){setFound(selected);setLoading(false);}
    })().catch(e=>{if(live){setError(messageOf(e));setLoading(false);}});
    return ()=>{live=false;};
  },[photos,scope,epoch,enabled]);
  const items=useMemo(()=>!enabled||scope==='all'?photos:photos.filter(p=>found.has(p.id)),[enabled,scope,photos,found]);
  return {items,scope,setScope,loading,error,epoch,canAnalyze:photos.length>0};
}

export function GridExposureSelect({scope,choose}:{scope:ExposureScope;choose:(v:ExposureScope)=>void}){
  return <select aria-label="图库曝光筛选" style={{background:'var(--surface)',color:'var(--text)',border:'1px solid var(--line)',borderRadius:4,padding:'5px'}}
    value={scope} onChange={e=>choose(e.target.value as ExposureScope)}><option value="all">全部曝光</option>
    <option value="over">可能过曝</option><option value="under">可能欠曝</option></select>;
}
export function GridExposureLabel({value,photo}:{value?:GridExposure;photo:Photo}){
  if(!value||value.frame!==Number(photo.selectedRawFrame||0)||(!value.over&&!value.under))return null;
  return <small className="grid-exposure" style={{position:'absolute',pointerEvents:'none',right:5,top:28,background:'#181a1de5',color:'#f0c497',fontFamily:'inherit',padding:'2px 5px',borderRadius:3}}>
    {[value.over?'可能过曝':'',value.under?'可能欠曝':''].filter(Boolean).join(' · ')}
  </small>;
}
