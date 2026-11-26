import React,{useEffect,useMemo,useRef,useState} from 'react';
import {Photo,Snapshot,Values,invoke,messageOf} from './model';
import {ReviewRows,packSelection} from './set-review-data';
import {MatchAreaPicker,regionNames} from './set-match-areas';
import {HairPicker} from './set-match-hair';
import {AreaSelection,MatchArea,MatchRole,Suggestion} from './set-match-suggestions';

type Fit={id:string;slot:number;before:number;after:number;values:Values;locks:string[];review:string[];needsPreview?:boolean;
  regions?:{role:MatchRole;maskSlot:number;before:number;after:number;oppositeBefore?:number|null;oppositeAfter?:number|null}[]};
type MatchRow={photo:Photo;fit?:Fit;error?:string};
type MatchPreview={before:string;after:string;detail?:{role?:MatchRole;reference?:string;before:string;after:string;coverage?:string}};
const labels:Record<string,string>={exposure:'曝光',temperature:'色温',tint:'色调',saturation:'饱和度'};
const perPage=12;
const signed=(n:number)=>(n>0?'+':'')+Number(n.toFixed(2));

export function SetMatchPanel({photos,selected,rows,saved,committing}:{
  photos:Photo[];selected:string|null;rows:ReviewRows;saved:(state:Snapshot)=>void;committing:(active:boolean)=>void;
}){
  const [reference,setReference]=useState(()=>photos.find(p=>p.id===selected)?.id||photos[0]?.id||'');
  const [query,setQuery]=useState('');
  const [kept,setKept]=useState(false),[limit,setLimit]=useState(5);
  const [mode,setMode]=useState<'global'|'regions'|'hair'>('global'),[role,setRole]=useState<MatchRole>('skin');
  const [continuous,setContinuous]=useState(false);
  const [picks,setPicks]=useState<Record<string,AreaSelection>>({}),[targetId,setTargetId]=useState(''),[targetQuery,setTargetQuery]=useState('');
  const [results,setResults]=useState<MatchRow[]>([]),[overrides,setOverrides]=useState<Record<string,boolean>>({}),[seen,setSeen]=useState<Record<string,boolean>>({});
  const [token,setToken]=useState(''),[refImage,setRefImage]=useState(''),[preview,setPreview]=useState<(MatchPreview&{id:string})|null>(null);
  const [page,setPage]=useState(0),[stage,setStage]=useState(''),[busy,setBusy]=useState(false),[applying,setApplying]=useState(false);
  const [notice,setNotice]=useState(''),[error,setError]=useState('');
  const run=useRef(0),previewRun=useRef(0),writePending=useRef(false);
  useEffect(()=>()=>{run.current++;previewRun.current++;},[]);
  const references=useMemo(()=>{
    if(photos.length<=200)return photos;
    const text=query.trim().toLocaleLowerCase(),source=photos.find(p=>p.id===reference);
    const found:Photo[]=[];
    for(const photo of photos){
      if(photo.path.toLocaleLowerCase().includes(text))found.push(photo);
      if(found.length===100)break;
    }
    return source&&!found.some(p=>p.id===reference)?[source,...found]:found;
  },[photos,query,reference]);
  const targets=photos.filter(p=>p.id!==reference&&(!kept||rows[p.id]?.mark==='keep'));
  const source=photos.find(p=>p.id===reference),target=targets.find(p=>p.id===targetId)||targets[0];
  const pickFor=(p:Photo|undefined)=>p&&picks[p.id]?.revision===(p.revision??0)?picks[p.id]:undefined;
  const sourcePick=pickFor(source),roles=Object.keys(sourcePick?.regions||{}) as MatchRole[];
  const ready=(pick?:Pick<AreaSelection,'regions'|'skipped'>)=>roles.some(k=>pick?.regions[k])&&
    roles.every(k=>pick?.regions[k]||pick?.skipped?.includes(k));
  const fitTargets=mode==='global'?targets:targets.filter(p=>ready(pickFor(p)));
  const useSuggestions=mode!=='global'&&(sourcePick?.suggested||fitTargets.some(p=>pickFor(p)?.suggested));
  const targetOptions=targets.length<=200?targets:targets.filter(p=>p.path.toLocaleLowerCase().includes(targetQuery.trim().toLocaleLowerCase())).slice(0,100);
  if(target&&!targetOptions.includes(target)&&targetOptions!==targets)targetOptions.unshift(target);
  const chosen=(r:MatchRow)=>!!r.fit&&(!r.fit.needsPreview||seen[r.photo.id])&&Object.keys(r.fit.values).length>0&&
    (overrides[r.photo.id]??(!r.fit.review?.length&&r.fit.after<=limit));
  const slots=results.filter(chosen).map(r=>r.fit!.slot);
  const high=results.filter(r=>r.fit&&r.fit.after>limit).length;
  function reset(){
    run.current++;previewRun.current++;setBusy(false);setStage('');setToken('');setResults([]);setPage(0);
    setRefImage('');setPreview(null);setError('');setNotice('');setOverrides({});setSeen({});
  }
  function savePick(photo:Photo,digest:string,edit:(old:AreaSelection)=>AreaSelection){
    reset();setPicks(prev=>{
      const revision=photo.revision??0,old=prev[photo.id];
      const base=old?.revision===revision&&old.digest===digest?old:{revision,digest,regions:{}};
      const next=edit(base);
      // A target selection only asserts correspondence with the reference the operator actually saw.
      if(photo.id===reference&&old){
        if(old.regions.hair&&old.regions.hair.hair?.stamp!==next.regions.hair?.hair?.stamp)return {[photo.id]:next};
        if(old.digest!==digest||old.revision!==revision||old.regions.skin&&
          (old.person!==next.person||JSON.stringify(old.regions.skin)!==JSON.stringify(next.regions.skin)))return {[photo.id]:next};
        if(old.regions.background&&next.regions.background&&JSON.stringify(old.regions.background)!==JSON.stringify(next.regions.background)){
          const updated={...prev};
          for(const [id,pick] of Object.entries(prev))if(id!==reference&&pick.suggested){
            const regions={...pick.regions};delete regions.background;
            updated[id]={...pick,regions,backgroundColor:undefined,skipped:pick.skipped?.filter(k=>k!=='background')};
          }
          return {...updated,[photo.id]:next};
        }
      }
      return {...prev,[photo.id]:next};
    });
  }
  function pick(photo:Photo,k:MatchRole,a:MatchArea|null,digest:string){
    savePick(photo,digest,old=>{
      const regions={...old.regions},skipped=(old.skipped||[]).filter(v=>v!==k);
      if(a)regions[k]=a;else delete regions[k];
      if(!a&&photo.id!==reference)skipped.push(k);
      return {...old,regions,skipped,omitted:false,...(k==='skin'?{side:undefined,person:undefined}:{backgroundColor:undefined})};
    });
  }
  function advance(photo:Photo){
    const index=targets.findIndex(p=>p.id===photo.id);
    const ordered=targets.slice(index+1).concat(targets.slice(0,index));
    const next=ordered.find(p=>!pickFor(p)?.omitted&&!ready(pickFor(p)));
    if(next)setTargetId(next.id);
  }
  function omit(photo:Photo,digest:string){
    const omitted=!pickFor(photo)?.omitted;
    savePick(photo,digest,old=>({revision:old.revision,digest:old.digest,regions:{},omitted}));
    if(omitted&&continuous)advance(photo);
  }
  function recommend(photo:Photo,proposal:Suggestion,digest:string,backgroundOnly:boolean){
    savePick(photo,digest,old=>{
      if(backgroundOnly)return {...old,suggested:true,omitted:false,regions:{...old.regions,background:proposal.regions.background},
        skipped:old.skipped?.filter(k=>k!=='background'),backgroundColor:proposal.backgroundColor};
      return {...old,...proposal,suggested:true,omitted:false,skipped:[]};
    });
    if(continuous&&target?.id===photo.id&&!backgroundOnly&&(proposal.regions.skin||proposal.regions.hair)&&ready(proposal))advance(photo);
  }
  async function fitAll(){
    if(!source||busy||writePending.current)return;
    reset();const job=++run.current;setBusy(true);setStage('读取已保存的基准片…');
    try{
      const ref=await invoke<{token:string;image:string}>('setMatchStart',{id:reference,revision:source.revision??0,
        ...(mode!=='global'&&sourcePick?{regions:sourcePick.regions,pickedDigest:sourcePick.digest,suggested:!!useSuggestions,skinSide:sourcePick.side}:{})});
      if(job!==run.current)return;
      setToken(ref.token);setRefImage(ref.image);
      let first:Photo|undefined;
      for(let i=0;i<fitTargets.length;i++){
        const photo=fitTargets[i];
        if(job!==run.current)return;
        setStage('拟合 '+(i+1)+' / '+fitTargets.length+' · '+photo.name);
        let row:MatchRow;
        try{
          const pick=pickFor(photo);
          const fit=await invoke<Fit>('setMatchPhoto',{token:ref.token,id:photo.id,revision:photo.revision??0,
            ...(mode!=='global'&&pick?{regions:Object.fromEntries(roles.filter(k=>pick.regions[k]).map(k=>[k,pick.regions[k]])),
              pickedDigest:pick.digest,suggested:!!pick.suggested,skinSide:pick.side,onlyPaired:!!pick.skipped?.length}:{})});
          row={photo,fit};
          if(!first&&fit.needsPreview&&Object.keys(fit.values).length)first=photo;
        }catch(err){row={photo,error:messageOf(err)};}
        if(job!==run.current)return;
        setResults(prev=>[...prev,row]);
      }
      if(first&&job===run.current)await show(first,ref.token);
    }catch(err){if(job===run.current)setError(messageOf(err));}
    finally{if(job===run.current){setStage('');setBusy(false);}}
  }
  async function show(photo:Photo,matchToken=token){
    const seq=++previewRun.current;setPreview(null);setError('');
    try{
      const pictures=await invoke<MatchPreview>('setMatchPreview',{id:photo.id,revision:photo.revision??0,token:matchToken});
      if(seq===previewRun.current){setPreview({id:photo.id,...pictures});setSeen(prev=>({...prev,[photo.id]:true}));}
    }catch(err){if(seq===previewRun.current)setError(messageOf(err));}
  }
  async function apply(){
    if(!slots.length||writePending.current)return;
    writePending.current=true;setApplying(true);committing(true);setError('');
    try{
      const state=await invoke<Snapshot>('setMatchApply',{token,selection:packSelection(slots)});
      saved(state);reset();setNotice('已应用到 '+slots.length+' 张。每张照片可从编辑历史撤销。');
    }catch(err){
      setError(messageOf(err));
      try{saved(await invoke<Snapshot>('init'));}catch{/* 保留原有界面和错误，允许重新拟合。 */}
    }finally{writePending.current=false;setApplying(false);committing(false);}
  }
  return <div className="set-match-body">
    {photos.length>200&&<label className="set-ref-find">查找基准片 <input aria-label="查找基准片" placeholder="按名称或路径缩小前 100 项" value={query} onChange={e=>setQuery(e.target.value)} disabled={busy||applying}/></label>}
    <div className="set-tools"><label>匹配范围 <select aria-label="匹配范围" value={mode} disabled={busy||applying} onChange={e=>{
      reset();if(mode==='hair'||e.target.value==='hair')setPicks({});setMode(e.target.value as typeof mode);}}>
      <option value="global">全图</option><option value="regions">按肤色或背景取样</option><option value="hair">按人物匹配发色</option></select></label>
      <label>基准片 <select aria-label="基准片" value={reference} disabled={busy||applying} onChange={e=>{reset();setPicks({});setReference(e.target.value);}}>
      {references.map(p=><option key={p.id} value={p.id}>{p.name} · {p.path}</option>)}</select></label>
      <label><input type="checkbox" checked={kept} disabled={busy||applying} onChange={e=>{reset();setKept(e.target.checked);}}/>仅拟合已保留片</label>
      {mode==='global'&&<button className="secondary small" disabled={!fitTargets.length||applying} onClick={()=>{if(busy){run.current++;setBusy(false);setStage('已停止，可复核已完成的照片。');}else void fitAll();}}>
        {busy?'停止拟合':'逐张拟合 '+fitTargets.length+' 张'}</button>}</div>
    {mode==='global'?<p className="dialog-note">以基准成片逐张匹配全图曝光、色温、色调。画面明暗分布、各处色偏或已识别人脸数差异明显时，目标默认留待复核。</p>:<>
      <div className="set-tools">{targets.length>200&&<label>查找目标 <input type="search" aria-label="查找校色目标" value={targetQuery} onChange={e=>setTargetQuery(e.target.value)}/></label>}
        <label>目标片 <select aria-label="目标片" disabled={busy||applying} value={target?.id||''} onChange={e=>setTargetId(e.target.value)}>
          {targetOptions.map(p=><option key={p.id} value={p.id}>{p.name} · {p.path}</option>)}</select></label>
        {mode==='regions'&&<label>取样对象 <select aria-label="取样对象" value={role} disabled={busy||applying} onChange={e=>setRole(e.target.value as MatchRole)}>
          <option value="skin">肤色</option><option value="background">背景</option></select></label>}
        {targets.length>1&&<label><input type="checkbox" checked={continuous} disabled={busy||applying}
          onChange={e=>setContinuous(e.target.checked)}/>选好人物后转到下一张待选片</label>}</div>
      {mode==='hair'?<>
        <p className="dialog-note">先点基准片的人，再点目标片里要调整的人。橙色标出发区，可隐藏对照。拟合后核对发际线，勾选要应用的照片。</p>
        <div className="set-area-pair">{source&&<HairPicker key={'hair-ref'+source.id} label="基准" photo={source} selection={sourcePick}
          enabled disabled={busy||applying} onPick={(v,d)=>recommend(source,v,d,false)}/>}
          {target&&<HairPicker key={'hair-target'+target.id+reference+(sourcePick?.regions.hair?.hair?.stamp??'')} label="目标" photo={target}
            selection={pickFor(target)} enabled={!!sourcePick?.regions.hair} disabled={busy||applying}
            onPick={(v,d)=>recommend(target,v,d,false)} onSkip={d=>omit(target,d)}/>}</div>
      </>:<><p className="dialog-note">点两张图里同一人的头像，每张图单独编号。橙圈取肤色，蓝圈取背景。淡橙色显示初选面部范围，拟合预览显示两侧受光核对后的范围。手动改圈只调整新圈内。</p>
      <div className="set-area-pair">{source&&<MatchAreaPicker key={'ref'+source.id} label="基准" photo={source} selection={sourcePick} role={role} disabled={busy||applying}
        change={(k,a,d)=>pick(source,k,a,d)} suggest={(v,d,b)=>recommend(source,v,d,b)}/>}
        {target&&<MatchAreaPicker key={'target'+target.id} label="目标" target photo={target} selection={pickFor(target)} reference={sourcePick} role={role} disabled={busy||applying}
          change={(k,a,d)=>pick(target,k,a,d)} suggest={(v,d,b)=>recommend(target,v,d,b)} omit={d=>omit(target,d)}/>}</div></>}
      <div className="set-tools"><button className="secondary small" disabled={!fitTargets.length||applying} onClick={()=>{if(busy){run.current++;setBusy(false);setStage('已停止，可复核已完成的照片。');}else void fitAll();}}>
        {busy?'停止拟合':(useSuggestions?'确认取样并拟合 ':'拟合选区 ')+fitTargets.length+' 张'}</button><span>{fitTargets.length} / {targets.length} 张目标已选好对应区域
          {targets.some(p=>pickFor(p)?.omitted)&&' · 已跳过 '+targets.filter(p=>pickFor(p)?.omitted).length+' 张'}</span></div>
    </>}
    {!!results.length&&<div className="set-tools"><label>自动排除偏差超过 <input aria-label="偏差阈值" type="number" min="0" max="100" step=".5" value={limit} disabled={applying} onChange={e=>{
      setLimit(Math.max(0,Math.min(100,Number(e.target.value)||0)));setOverrides({});
    }}/></label><span>超差 {high} 张 · 场景或光线待复核 {results.filter(r=>r.fit?.review?.length).length} 张 · 已选 {slots.length} 张，可逐张改选</span></div>}
    {(stage||notice)&&<p role="status" className="set-status">{stage||notice}</p>}
    {error&&<p role="alert" className="field-error">{error}</p>}
    <div className="set-fit-list">{results.slice(page*perPage,(page+1)*perPage).map(row=>{
      const {photo,fit}=row,changed=!!fit&&Object.keys(fit.values).length>0;
      return <div className="set-fit-row" key={photo.id}>
        <label title={photo.path}><input type="checkbox" disabled={!changed||applying||!!fit?.needsPreview&&!seen[photo.id]} checked={chosen(row)} onChange={e=>setOverrides(prev=>({...prev,[photo.id]:e.target.checked}))}/>
          <b>{photo.name}</b></label>
        {fit?<><span className={fit.after>limit?'set-warning':''}>偏差 {fit.before.toFixed(1)} → {fit.after.toFixed(1)}{fit.after>limit?' · 超差':''}</span>
          <small>{fit.regions?fit.regions.map(r=><span className="set-area-score" key={r.role}>{regionNames[r.role]} {r.before.toFixed(1)} → {r.after.toFixed(1)} · {Object.entries(fit.values)
              .filter(([k])=>k.startsWith('local_m'+r.maskSlot+'_')).map(([k,v])=>labels[k.split('_').pop()!]+' '+signed(v)).join('，')||'无改动'}
              {r.oppositeBefore!=null&&r.oppositeAfter!=null?' · 对侧偏差 '+r.oppositeBefore.toFixed(1)+' → '+r.oppositeAfter.toFixed(1):''}</span>):
            Object.entries(fit.values).map(([k,v])=>labels[k]+' '+signed(v)).join('，')||'参数无变化'}
            {fit.locks.length?' · 已锁 '+fit.locks.map(k=>{const r=fit.regions?.find(a=>k.startsWith('local_m'+a.maskSlot+'_'));return (r?regionNames[r.role]:'')+labels[k.split('_').pop()!];}).join('、'):''}</small>
          <button className="text-button" disabled={applying||busy} onClick={()=>void show(photo)}>看预览</button>
          {!!fit.review?.length&&<div className="set-area-warning">待复核 · {fit.review.join('。')}</div>}</>:<span className="field-error">{row.error}</span>}
      </div>;
    })}</div>
    {results.length>perPage&&<div className="set-tools"><button disabled={!page} onClick={()=>setPage(page-1)}>上一页</button>
      <span>{page+1} / {Math.ceil(results.length/perPage)}</span><button disabled={(page+1)*perPage>=results.length} onClick={()=>setPage(page+1)}>下一页</button></div>}
    {preview&&<><p className="dialog-note">当前预览 · {photos.find(p=>p.id===preview.id)?.name}</p>
      <div className="set-match-images">{[{label:'基准成片',image:refImage},{label:'目标当前',image:preview.before},{label:'拟合预览',image:preview.after}].map(p=>
        <figure key={p.label}><img src={p.image} alt={p.label}/><figcaption>{p.label}</figcaption></figure>)}</div>
      {preview.detail&&<div className="set-tools" aria-label={preview.detail.role==='hair'?'头发取样细节':'面颊取样细节'}>
        {(preview.detail.role==='hair'?[['基准头发',preview.detail.reference],['当前头发',preview.detail.before],['拟合头发',preview.detail.after],['头发作用范围',preview.detail.coverage]]:
          [['基准面颊',preview.detail.reference],['当前面颊',preview.detail.before],['拟合面颊',preview.detail.after],['面部作用范围',preview.detail.coverage]]).map(([label,image])=>
          image&&<figure key={label} style={{margin:'2px 4px',textAlign:'center'}}><img src={image} alt={label} width="96" height="96"/>
            <figcaption>{label}</figcaption></figure>)}</div>}</>}
    <div className="dialog-actions"><button className="primary" disabled={busy||applying||!slots.length} onClick={()=>void apply()}>{applying?'正在保存…':'确认应用 '+slots.length+' 张'}</button></div>
  </div>;
}
