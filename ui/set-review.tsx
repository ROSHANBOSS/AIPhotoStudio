import React,{useEffect,useMemo,useRef,useState} from 'react';
import {Photo,Preview,Snapshot,invoke,messageOf} from './model';
import {Modal} from './widgets';
import {detectPortraits} from './portrait-detector';
import {SetMatchPanel} from './set-match';
import {Evidence,EyeManual,GridExposure,Mark,ReviewFace,ReviewRow,ReviewRows,Verdict,burstGroups,emptyRow,evidenceFor,eyesFor,exposureEvent,qualityFor,reviewMatches,keyMarks,markEvent,markLabels,writeReview} from './set-review-data';
import {EyeReview,EyeSelection} from './set-review-eye';
import {EyeQueue,SeriesAnchor} from './set-review-queue';
import {ComparisonPicker} from './set-review-pair';

export function useGridReview(photos:Photo[],first:number,end:number,enabled:boolean,epoch:number){
  const [marks,setMarks]=useState<Record<string,Mark>>({});
  const [exposures,setExposures]=useState<Record<string,GridExposure>>({});
  const [error,setError]=useState('');
  const keys=JSON.stringify(enabled?photos.slice(first,end).map(p=>p.id):[]);
  useEffect(()=>{
    if(!enabled)return;
    let live=true;
    const later:Record<string,Mark>={};
    const changed=(event:Event)=>{
      const {id,mark}=(event as CustomEvent<{id:string;mark:Mark}>).detail;
      later[id]=mark;setMarks(prev=>({...prev,[id]:mark}));
    };
    window.addEventListener(markEvent,changed);
    void invoke<ReviewRow[]>('setReviewRows',{ids:JSON.parse(keys),marksOnly:true,exposure:true}).then(rows=>{
      if(live){setMarks(Object.assign(Object.fromEntries(rows.map(r=>[r.id,r.mark])),later));
        setExposures(Object.fromEntries(rows.flatMap(r=>r.exposure?[[r.id,r.exposure]]:[])));}
    }).catch(e=>{if(live)setError(messageOf(e));});
    return ()=>{live=false;window.removeEventListener(markEvent,changed);};
  },[keys,enabled,epoch]);
  function keyDown(event:React.KeyboardEvent,id?:string){
    const mark=keyMarks[event.key.toLowerCase()];
    if(!enabled||!id||!mark||event.repeat||event.ctrlKey||event.altKey||event.metaKey||event.nativeEvent.isComposing)return false;
    event.preventDefault();event.stopPropagation();
    setError('');
    void writeReview(id,'mark',mark).catch(e=>setError(messageOf(e)));
    return true;
  }
  return {marks,exposures,error,keyDown};
}

function ReviewPicture({photo}:{photo:Photo}){
  const [image,setImage]=useState(''),[error,setError]=useState('');
  useEffect(()=>{
    let live=true;setImage('');setError('');
    void invoke<Preview>('preview',{id:photo.id,revision:photo.revision??0,original:true,maxEdge:720}).then(frame=>{
      if(live)setImage(frame.image);
    }).catch(err=>{if(live)setError(messageOf(err));});
    return ()=>{live=false;};
  },[photo.id,photo.path,photo.revision,photo.selectedRawFrame]);
  return <div className="set-photo-image" style={{position:'relative'}}>{image?<img src={image} alt={'原片 '+photo.name}
    style={{position:'absolute',inset:0,width:'100%',height:'100%',objectFit:'contain'}}/>:<span>{error||'读取原片…'}</span>}</div>;
}
function qualityText(e:Evidence|null|undefined,field:'blur'|'eyes',level:number){
  if(!e)return '待分析';
  if(field==='blur'){
    const q=qualityFor(e,level);
    if(!q)return '请重新分析清晰度';
    if(q.blur==='face')return '人脸 '+q.softFaces.join('、')+' 疑似模糊';
    return q.blur==='image'?'画面可能偏软':q.blur==='clear'?'未见明显模糊':'纹理不足，清晰度待判';
  }
  if(!e.faces&&level!==1)return '请重新分析眼睑';
  const eyes=eyesFor(e,level);
  if(eyes.state==='suspect')return '人脸 '+eyes.suspect.join('、')+' 闭眼提示'+(eyes.unknown.length?'，另有 '+eyes.unknown.length+' 张脸待判':'');
  if(eyes.state==='clear')return e.faceCount+' 张脸未见闭眼';
  if(e.detection==='noFace')return '未检出人脸，闭眼待判';
  if(e.detection==='unavailable')return '人脸模型不可用，闭眼待判';
  if(e.detection==='pending')return '待分析眼睑';
  return eyes.unknown.length?'人脸 '+eyes.unknown.join('、')+' 眼睑待判':'眼睑证据不足';
}
function verdictText(photo:Photo,row:ReviewRow,field:'blur'|'eyes',level:number){
  if(row[field]==='auto')return qualityText(evidenceFor(photo,row),field,level);
  if(field==='blur')return row.blur==='flag'?'人工标为模糊':'人工确认清晰';
  return row.eyes==='flag'?'人工标为闭眼':'人工确认睁眼';
}
async function inspect(photo:Photo){
  const result=await invoke<{token?:string;image?:string;evidence:Evidence}>('setReviewInspect',{id:photo.id,revision:photo.revision??0});
  if(!result.token||!result.image)return result.evidence;
  let meshes:string[];
  try{
    const image=new Image();image.src=result.image;await image.decode();
    meshes=(await detectPortraits(image)).meshes;
  }catch{
    return invoke<Evidence>('setReviewFaces',{token:result.token,unavailable:true});
  }
  return invoke<Evidence>('setReviewFaces',{token:result.token,meshes});
}

export function SetReviewDialog({photos,selected,close,saved,initialScope='all'}:{photos:Photo[];selected:string|null;close:()=>void;saved:(state:Snapshot)=>void;initialScope?:string}){
  const [tab,setTab]=useState<'review'|'queue'|'match'>('review');
  const [rows,setRows]=useState<ReviewRows>({}),[ready,setReady]=useState(false);
  const [scope,setScope]=useState(initialScope),[columns,setColumns]=useState(2);
  const [level,setLevel]=useState(1),[eye,setEye]=useState<EyeSelection|null>(null);
  const [series,setSeries]=useState<SeriesAnchor|null>(null);
  const [pair,setPair]=useState<string[]>([]),[choosePair,setChoosePair]=useState(false);
  const [position,setPosition]=useState(()=>Math.max(0,photos.findIndex(p=>p.id===selected)));
  const [progress,setProgress]=useState(''),[error,setError]=useState(''),[confirming,setConfirming]=useState(false);
  const run=useRef(0),writes=useRef<Promise<void>>(Promise.resolve());
  const ids=useMemo(()=>photos.map(p=>p.id),[photos]);
  const membership=JSON.stringify(ids);
  useEffect(()=>{
    let live=true;
    setReady(false);
    void (async()=>{
      const found:ReviewRows={};
      const sensitivity=await invoke<number>('setReviewSensitivity');
      for(let i=0;i<ids.length;i+=400){
        if(!live)return;
        const batch=await invoke<ReviewRow[]>('setReviewRows',{ids:ids.slice(i,i+400)});
        for(const row of batch)found[row.id]=row;
      }
      if(live){setRows(found);setLevel(sensitivity);setReady(true);}
    })().catch(err=>{if(live)setError(messageOf(err));});
    return ()=>{live=false;run.current++;};
  },[membership]);
  const [groups,setGroups]=useState<Photo[][]>([]);
  const [oldComposition,setOldComposition]=useState(0);
  const manualGroup=useMemo(()=>{
    const byId=new Map(photos.map(photo=>[photo.id,photo]));
    return pair.flatMap(id=>byId.get(id)||[]);
  },[pair,photos]);
  const reviewGroups=useMemo(()=>{
    if(scope!=='pair'||manualGroup.length<2)return groups;
    const picked=new Set(manualGroup.map(photo=>photo.id));
    return [...groups.map(group=>group.filter(photo=>!picked.has(photo.id))).filter(group=>group.length>1),manualGroup];
  },[groups,manualGroup,scope]);
  const latestRows=useRef(rows);latestRows.current=rows;
  useEffect(()=>{
    if(!ready||progress)return;
    setGroups(burstGroups(photos,latestRows.current));
    setOldComposition(photos.filter(p=>{
      const ev=evidenceFor(p,latestRows.current[p.id]);return ev&&ev.composition==null;
    }).length);
  },[photos,ready,progress]);
  const pool=useMemo(()=>{
    if(scope.startsWith('group:'))return groups.find(group=>group[0].id===scope.slice(6))||[];
    if(scope==='pair')return manualGroup;
    if(['flagged','over','under','eyes','unknownEyes'].includes(scope))return photos.filter(p=>reviewMatches(scope,p,rows[p.id],level));
    if(scope==='all')return photos;
    return photos.filter(p=>(rows[p.id]?.mark||'none')===scope);
  },[photos,rows,groups,scope,level,manualGroup]);
  const index=Math.max(0,Math.min(position,pool.length-1));
  const start=Math.min(Math.floor(index/columns)*columns,Math.max(0,pool.length-columns));
  const active=pool[index];
  const activeRow=active?(rows[active.id]||emptyRow(active.id)):null;
  const activeEvidence=active?evidenceFor(active,activeRow||undefined):null;
  function changeSensitivity(value:number){
    setError('');
    writes.current=writes.current.then(async()=>{setLevel(await invoke<number>('setReviewSensitivity',{level:value}));setPosition(0);window.dispatchEvent(new Event(exposureEvent));})
      .catch(err=>setError(messageOf(err)));
  }
  function changeFace(photo:Photo,e:Evidence,face:ReviewFace,value:EyeManual){
    setError('');
    const write=writes.current.then(async()=>{
      await invoke('setReviewEye',{id:photo.id,revision:photo.revision??0,source:e.source,frame:e.frame,key:face.key,value});
      setRows(old=>{
        const row=old[photo.id],current=evidenceFor(photo,row);
        if(current?.source!==e.source||current.frame!==e.frame||!current.faces)return old;
        return {...old,[photo.id]:{...row,evidence:{...current,faces:current.faces.map(f=>f.key===face.key?{...f,manual:value}:f)}}};
      });
    });
    writes.current=write.catch(err=>setError(messageOf(err)));
    return write.then(()=>true,()=>false);
  }
  async function setField(id:string,field:'mark'|'blur'|'eyes',value:Mark|Verdict){
    setError('');
    writes.current=writes.current.then(async()=>{
      await writeReview(id,field,value);
      setRows(old=>({...old,[id]:{...(old[id]||emptyRow(id)),[field]:value}}));
    }).catch(err=>setError(messageOf(err)));
    await writes.current;
  }
  async function analyze(){
    if(progress||!ready)return;
    const request=++run.current;setError('');let skipped=0,firstProblem='';
    for(let i=0;i<photos.length;i++){
      if(request!==run.current)return;
      const p=photos[i];setProgress('分析 '+(i+1)+' / '+photos.length+' · '+p.name);
      try{
        const evidence=await inspect(p);
        if(request!==run.current)return;
        setRows(old=>({...old,[p.id]:{...(old[p.id]||emptyRow(p.id)),evidence}}));
      }catch(err){skipped++;if(!firstProblem)firstProblem=p.name+' · '+messageOf(err);}
    }
    if(request===run.current){setProgress('');window.dispatchEvent(new Event(exposureEvent));if(skipped)setError(skipped+' 张未完成，'+firstProblem);}
  }
  function stop(){run.current++;setProgress('');if(progress)window.dispatchEvent(new Event(exposureEvent));}
  function keyDown(e:React.KeyboardEvent){
    if(!active||e.ctrlKey||e.altKey||e.metaKey||e.nativeEvent.isComposing||['SELECT','INPUT','TEXTAREA'].includes((e.target as HTMLElement).tagName))return;
    const mark=keyMarks[e.key.toLowerCase()];
    if(mark && !e.repeat){e.preventDefault();e.stopPropagation();void setField(active.id,'mark',mark);}
    else if(e.key==='ArrowLeft'||e.key==='ArrowRight'){
      e.preventDefault();e.stopPropagation();(e.currentTarget as HTMLElement).focus({preventScroll:true});
      setPosition(Math.max(0,Math.min(pool.length-1,index+(e.key==='ArrowLeft'?-1:1))));
    }
  }
  return <Modal title="整组审片与校色" className="set-dialog" close={()=>{if(!confirming){stop();close();}}}>
    <div className="dialog-tabs"><button className={tab==='review'?'on':''} disabled={confirming} onClick={()=>setTab('review')}>并排挑片</button>
      <button className={tab==='queue'?'on':''} disabled={!ready||confirming} onClick={()=>{setSeries(null);setTab('queue');}}>疑难眼睛队列</button>
      <button className={tab==='match'?'on':''} disabled={!!progress||!ready||confirming} onClick={()=>setTab('match')}>基准校色</button></div>
    {tab==='review'?<div className="set-review-body" onKeyDown={keyDown} data-focus tabIndex={-1}>
      <div className="set-tools"><label>查看 <select aria-label="查看" value={scope} onChange={e=>{setScope(e.target.value);setPosition(0);setChoosePair(false);}}>
        <option value="all">全部 {photos.length} 张</option><option value="keep">已保留</option><option value="reject">已淘汰</option>
        <option value="none">未标记</option><option value="flagged">全部问题提示</option>
        <option value="over">过曝提示</option><option value="under">欠曝提示</option><option value="eyes">闭眼提示</option><option value="unknownEyes">有眼睑待判的人脸</option>
        {manualGroup.length>=2&&<option value="pair">{manualGroup.length===2?'人工构图对比':'本次手选组'} · {manualGroup.length} 张</option>}
        {groups.map((g,i)=><option value={'group:'+g[0].id} key={g[0].id}>相似组 {i+1} · {g.length} 张 · {g[0].name}</option>)}
      </select></label>
      <label>并排 <select aria-label="并排" value={columns} disabled={scope==='pair'&&manualGroup.length===2} onChange={e=>setColumns(Number(e.target.value))}>{[2,3,4].map(n=><option key={n} value={n}>{n} 张</option>)}</select></label>
      <label>检测灵敏度 <select aria-label="检测灵敏度" disabled={!ready} value={level} onChange={e=>changeSensitivity(Number(e.target.value))}>
        <option value="0">低</option><option value="1">标准</option><option value="2">高</option></select></label>
      <button className="secondary small" disabled={!ready} onClick={()=>{if(progress)stop();else void analyze();}}>{progress?'停止分析':'分析连拍与人脸'}</button>
      <span role="status">{progress||('相似组 '+groups.length+' 组'+(oldComposition?' · '+oldComposition+' 张可重新分析构图':''))}</span></div>
      <div className="set-compare" style={{gridTemplateColumns:'repeat('+columns+', minmax(0,1fr))'}}>
        {pool.slice(start,start+columns).map((p,i)=>{
          const row=rows[p.id]||emptyRow(p.id),e=evidenceFor(p,row),q=e?.quality?.[level],eyes=eyesFor(e,level);
          const suspectFace=eyes.suspect[0]||eyes.unknown[0]||(row.eyes==='flag'?1:0),face=e?.faces?.[suspectFace-1];
          return <article key={p.id} className={'set-tile '+(p.id===active?.id?'active':'')} title={p.path}
            onFocus={event=>{if((event.target as HTMLElement).matches(':focus-visible'))setPosition(start+i);}} onClick={()=>setPosition(start+i)}>
            <button style={{display:'block',width:'100%',textAlign:'left',padding:0,whiteSpace:'normal'}} aria-pressed={p.id===active?.id}>
            <ReviewPicture photo={p}/><span className="set-tile-name">{p.name}</span><small className={'set-mark '+row.mark}>{markLabels[row.mark]}</small>
            <small>{verdictText(p,row,'blur',level)} · {verdictText(p,row,'eyes',level)}</small>
            <small className={q?.over||q?.under?'set-warning':''}>{q?(e?.medianLuma===-1?'可见像素不足，曝光待判':
              [q.over?'可能过曝':'',q.under?'可能欠曝':''].filter(Boolean).join(' · ')||'未见明显曝光异常'):e?'重新分析可查看曝光与逐脸位置':'曝光待分析'}</small></button>
            {face&&<button className="secondary small" onClick={()=>setEye({id:p.id,key:face.key,eye:face.closerEye})}>查看人脸 {suspectFace} 眼部原像素</button>}
          </article>;
        })}
      </div>
      {!active&&<p className="dialog-note">此筛选下没有照片。</p>}
      <div className="set-tools"><button className="secondary small" disabled={index===0} onClick={()=>setPosition(index-1)}>上一张</button>
        <span>{pool.length?index+1:0} / {pool.length}</span>
        <button className="secondary small" disabled={index+1>=pool.length} onClick={()=>setPosition(index+1)}>下一张</button>
        {active&&(<>{(['keep','reject','none'] as Mark[]).map((mark,i)=><button key={mark} disabled={!ready} className={'secondary small '+(activeRow?.mark===mark?'selected':'')}
          onClick={()=>void setField(active.id,'mark',mark)}>{['保留 P','淘汰 X','清标 U'][i]}</button>)}
          {photos.length>1&&<button className="secondary small" onClick={()=>setChoosePair(v=>!v)}>比较不同构图</button>}</>)}</div>
      {active&&choosePair&&<ComparisonPicker key={active.id} anchor={active} photos={photos} groups={groups} initial={scope==='pair'?pair:[]} close={()=>setChoosePair(false)}
        compare={ids=>{setPair(ids);setColumns(Math.min(4,ids.length));setScope('pair');setPosition(0);setChoosePair(false);}}/>}
      {activeEvidence?.quality&&activeEvidence.medianLuma!==-1&&<small className="dialog-note">
        原片亮端 {((activeEvidence.brightFraction||0)*100).toFixed(1)}% · 暗端 {((activeEvidence.darkFraction||0)*100).toFixed(1)}% · 亮度中位数 {activeEvidence.medianLuma}/255
      </small>}
      {active&&activeEvidence&&<EyeReview photo={active} evidence={activeEvidence} level={level} selection={eye} onSelect={setEye} onSave={changeFace} disabled={!!progress||!ready}
        onSeries={reviewGroups.some(g=>g.some(p=>p.id===active.id))?face=>{
          setSeries({id:active.id,source:activeEvidence.source,frame:activeEvidence.frame,key:face.key});setTab('queue');
        }:undefined}/>}
      {active&&activeRow&&<div className="set-verdicts">{(['blur','eyes'] as const).map(field=><label key={field}>{field==='blur'?'清晰度复核':'闭眼复核'}
        <select aria-label={field==='blur'?'清晰度复核':'闭眼复核'} disabled={!ready} value={activeRow[field]} onChange={e=>void setField(active.id,field,e.target.value as Verdict)}>
          <option value="auto">{qualityText(evidenceFor(active,activeRow),field,level)}（{field==='eyes'&&activeEvidence?.faces?.some(f=>f.manual&&f.manual!=='auto')?'按逐脸复核':'自动'}）</option>
          <option value="flag">{field==='blur'?'人工标为模糊':'人工标为闭眼'}</option>
          <option value="clear">{field==='blur'?'人工确认清晰':'人工确认睁眼'}</option></select></label>)}</div>}
    </div>:tab==='queue'?<EyeQueue photos={photos} rows={rows} groups={reviewGroups} level={level} save={changeFace} progress={progress} series={series} setSeries={setSeries}
      busy={confirming} setBusy={setConfirming} analyze={()=>{if(progress)stop();else void analyze();}}
      openPhoto={p=>{setScope('all');setPosition(photos.findIndex(x=>x.id===p.id));setChoosePair(false);setTab('review');}}/>:
      <SetMatchPanel photos={photos} selected={selected} rows={rows} saved={saved} committing={setConfirming}/>}
    {error&&<p role="alert" className="field-error">{error}</p>}
  </Modal>;
}
