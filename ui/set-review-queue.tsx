import React,{useMemo,useRef,useState} from 'react';
import {Photo} from './model';
import {Evidence,EyeManual,ReviewFace,ReviewRows,evidenceFor} from './set-review-data';
import {EyePixels,ExpressionReference} from './set-review-eye';

type Filter='pending'|'suspect'|'unknown'|'deferred'|'done';
type Item={id:string;photo:Photo;ev:Evidence;face:ReviewFace;number:number};
export type SeriesAnchor={id:string;source:string;frame:number;key:string};
type SaveFace=(photo:Photo,e:Evidence,f:ReviewFace,v:EyeManual)=>Promise<boolean>;
const categories:Record<Filter,string>={pending:'全部待复核',suspect:'疑似闭眼',unknown:'自动待判',deferred:'人工待判',done:'已确认'};
const automatic={suspect:'疑似闭眼',unknown:'眼睑证据不足',clear:'未见闭眼'};
const reviewed={flag:'人工确认闭眼',clear:'人工确认睁眼',unknown:'人工待判',auto:''};
const grid:React.CSSProperties={display:'grid',gridTemplateColumns:'repeat(auto-fit,minmax(min(100%,300px),1fr))',gap:16,alignItems:'start'};

function queueItem(photo:Photo,ev:Evidence,face:ReviewFace,index:number):Item {
  return {id:JSON.stringify([photo.id,ev.source,ev.frame,face.key]),photo,ev,face,number:index+1};
}

function queueItems(photos:Photo[],rows:ReviewRows,groups:Photo[][],level:number){
  const lists={suspect:[] as Item[],unknown:[] as Item[],deferred:[] as Item[],done:[] as Item[]};
  const unlocated:Photo[]=[],missing:Photo[]=[];
  const membership=new Map<string,Photo[]>();
  for(const group of groups)for(const p of group)membership.set(p.id,group);
  const seen=new Set<string>();
  let old=0,whole=0;
  for(const first of photos){
    if(seen.has(first.id))continue;
    for(const p of membership.get(first.id)||[first]){
      seen.add(p.id);
      const row=rows[p.id],e=evidenceFor(p,row);
      if(row?.eyes&&row.eyes!=='auto')whole++;
      if(!e||!e.faces||['pending','unavailable'].includes(e.detection)){missing.push(p);continue;}
      if(e.version!==undefined&&e.version<5)old++;
      if(!e.faces.length){if(!row||row.eyes==='auto')unlocated.push(p);continue;}
      e.faces.forEach((face,index)=>{
        const manual=face.manual||'auto',auto=face.eyes[level];
        if(manual==='auto'&&(row.eyes!=='auto'||auto==='clear'))return;
        const status=manual==='unknown'?'deferred':manual!=='auto'?'done':auto==='suspect'?'suspect':'unknown';
        lists[status].push(queueItem(p,e,face,index));
      });
    }
  }
  return {lists:{...lists,pending:[...lists.suspect,...lists.unknown]},membership,missing,unlocated,old,whole};
}

function samePosition(anchor:ReviewFace,originalFaces:ReviewFace[],candidateFaces:ReviewFace[],aspect:number){
  const direct=nearbyFace(anchor,candidateFaces,aspect);
  const reciprocal=direct&&nearbyFace(direct,originalFaces,aspect)?.key===anchor.key?direct:undefined;
  if(originalFaces.length<4||candidateFaces.length<4)return reciprocal;
  // Several other faces must independently agree on a small camera movement before compensating for it.
  const shifts:{dx:number;dy:number}[]=[];
  for(const face of originalFaces){
    if(face===anchor)continue;
    const other=nearbyFace(face,candidateFaces,aspect,1.3);
    if(!other||nearbyFace(other,originalFaces,aspect,1.3)!==face)continue;
    const dx=(other.centers[0]+other.centers[2]-face.centers[0]-face.centers[2])/2;
    const dy=(other.centers[1]+other.centers[3]-face.centers[1]-face.centers[3])/2;
    if(Math.hypot(dx*aspect,dy)<=.05)shifts.push({dx,dy});
  }
  if(shifts.length<3)return reciprocal;
  function median(values:number[]){
    values.sort((a,b)=>a-b);
    const i=Math.floor(values.length/2);
    return values.length%2?values[i]:(values[i-1]+values[i])/2;
  }
  const dx=median(shifts.map(s=>s.dx)),dy=median(shifts.map(s=>s.dy));
  const support=shifts.filter(s=>Math.hypot((s.dx-dx)*aspect,s.dy-dy)<=.015).length;
  if(support<3||support*2<shifts.length)return reciprocal;
  const aligned=nearbyFace(anchor,candidateFaces,aspect,.8,dx,dy);
  if(!aligned)return undefined;
  if(nearbyFace(aligned,originalFaces,aspect,.8,-dx,-dy)!==anchor)return undefined;
  return reciprocal&&reciprocal!==aligned?undefined:aligned;
}

function positionSeries(anchor:SeriesAnchor,group:Photo[]|undefined,rows:ReviewRows){
  const photo=group?.find(p=>p.id===anchor.id);
  if(!group||!photo)return null;
  const ev=evidenceFor(photo,rows[photo.id]),faces=ev?.faces;
  if(!ev||ev.source!==anchor.source||ev.frame!==anchor.frame||!faces)return null;
  const index=faces.findIndex(f=>f.key===anchor.key);
  if(index<0)return null;
  const list:Item[]=[],unmatched:Photo[]=[];let processed=0;
  for(const p of group){
    const row=rows[p.id],e=evidenceFor(p,row),others=e?.faces;
    if(row?.eyes&&row.eyes!=='auto'){processed++;continue;}
    if(!e||!others){unmatched.push(p);continue;}
    const face=p.id===anchor.id?faces[index]:samePosition(faces[index],faces,others,photo.width/photo.height);
    if(!face){unmatched.push(p);continue;}
    if(face.manual&&face.manual!=='auto'){processed++;continue;}
    list.push(queueItem(p,e,face,others.indexOf(face)));
  }
  return {list,processed,unmatched,photo,number:index+1};
}

function nearbyFace(ref:ReviewFace,others:ReviewFace[],aspect:number,tolerance=.6,dx=0,dy=0){
  const point=(f:ReviewFace,n:number)=>[f.centers[n*2]*aspect,f.centers[n*2+1]];
  const distance=(a:number[],b:number[])=>Math.hypot(a[0]-b[0],a[1]-b[1]);
  const refSpan=distance(point(ref,0),point(ref,1));
  const near=others.filter(f=>{
    const span=distance(point(f,0),point(f,1));
    return refSpan>0&&span/refSpan>=.75&&span/refSpan<=1.34&&[0,1].every(i=>
      Math.hypot((ref.centers[i*2]+dx-f.centers[i*2])*aspect,ref.centers[i*2+1]+dy-f.centers[i*2+1])<=tolerance*Math.min(span,refSpan));
  });
  return near.length===1?near[0]:undefined;
}

function ReviewPhotoList({photos,caption,open,disabled}:{photos:Photo[];caption:string;open:(p:Photo)=>void;disabled:boolean}){
  const [expanded,setExpanded]=useState(false),[text,setText]=useState(''),[id,setId]=useState('');
  if(!photos.length)return null;
  const needle=text.trim().toLocaleLowerCase();
  const choices:Photo[]=[];
  if(expanded)for(const p of photos){
    if(!needle||p.path.toLocaleLowerCase().includes(needle))choices.push(p);
    if(choices.length===60)break;
  }
  const current=choices.find(p=>p.id===id)||choices[0];
  return <div><button className="text-button" aria-expanded={expanded} disabled={disabled} onClick={()=>setExpanded(v=>!v)}>
    {caption} {photos.length} 张</button>
    {expanded&&<div className="set-tools">
      {photos.length>60&&<input aria-label={'查找'+caption+'的照片'} type="search" placeholder="查找文件名或文件夹" value={text} onChange={e=>setText(e.target.value)}/>}
      <select aria-label={caption+'的照片'} value={current?.id||''} onChange={e=>setId(e.target.value)}>
        {!current&&<option value="">没有匹配项</option>}
        {choices.map(p=><option key={p.id} value={p.id}>{p.name}</option>)}</select>
      <button className="secondary small" disabled={disabled||!current} onClick={()=>current&&open(current)}>查看整片</button>
    </div>}
  </div>;
}

function AdjacentFrame({item,group,rows,side}:{item:Item;group:Photo[];rows:ReviewRows;side:number}){
  const position=group.findIndex(p=>p.id===item.photo.id);
  const neighbors=group.slice(Math.max(0,position-1),position+2).filter(p=>p.id!==item.photo.id);
  const [id,setId]=useState(''),[chosen,setChosen]=useState('');
  const neighbor=neighbors.find(p=>p.id===id)||neighbors[0];
  if(!neighbor)return null;
  const ev=evidenceFor(neighbor,rows[neighbor.id]),faces=ev?.faces||[];
  const suggested=samePosition(item.face,item.ev.faces||[],faces,item.photo.width/item.photo.height);
  const f=faces.find(f=>f.key===chosen)||suggested;
  return <section aria-label="同组邻片原像素">
    <div className="set-tools"><label>同组邻片 <select aria-label="同组邻片" value={neighbor.id} onChange={e=>{setId(e.target.value);setChosen('');}}>
      {neighbors.map(p=><option key={p.id} value={p.id}>{p.name}</option>)}</select></label></div>
    {faces.length>0?<div className="set-tools"><label>对照人脸 <select aria-label="对照人脸" value={f?.key||''} onChange={e=>setChosen(e.target.value)}>
      {!f&&<option value="">选择要对照的人脸</option>}
      {faces.map((x,i)=><option key={x.key} value={x.key}>人脸 {i+1}{x===suggested?' · 位置相近':''}</option>)}</select></label>
      </div>:<p className="dialog-note">这张邻片尚无人脸位置。</p>}
    {f&&<div className="set-tools"><span>原片 100% · {side?'图中右眼':'图中左眼'}</span></div>}
    {ev&&f&&<EyePixels photo={neighbor} source={ev.source} face={f} eye={side} adjacent/>}
  </section>;
}

function CurrentFace({item,level,group,rows,save,busy,setBusy,onNext,hasNext,compare,setCompare,startSeries}:{item:Item;level:number;group?:Photo[];rows:ReviewRows;
  save:SaveFace;busy:boolean;setBusy:(v:boolean)=>void;onNext:()=>void;hasNext:boolean;compare:boolean;setCompare:(v:boolean)=>void;startSeries?:()=>void}){
  const [side,setSide]=useState(item.face.closerEye),[ready,setReady]=useState(false);
  const saving=useRef(false);
  async function decide(value:EyeManual){
    if(saving.current||!ready)return;
    saving.current=true;setBusy(true);
    try{
      if(await save(item.photo,item.ev,item.face,value))onNext();
    }finally{saving.current=false;setBusy(false);}
  }
  const manual=item.face.manual||'auto';
  return <section aria-label="当前复核人脸" style={compare&&group?grid:undefined}><div>
    <div className="set-tools"><strong style={{overflowWrap:'anywhere'}}>{item.photo.name}</strong><strong>人脸 {item.number}</strong></div>
    <div className="set-tools"><span>{'自动 · '+automatic[item.face.eyes[level]]}</span>{manual!=='auto'&&<span>{reviewed[manual]}</span>}
      {group&&<small>同组第 {group.findIndex(p=>p.id===item.photo.id)+1} / {group.length} 张</small>}</div>
    {item.face.colorCover&&<p className="dialog-note">双眼上下都有大面积色彩覆盖，请查看原片判断眼睑。</p>}
    <div className="set-tools"><span>原片 100%</span>{[0,1].map(i=><button className={'secondary small '+(i===side?'selected':'')}
      aria-pressed={i===side} key={i} disabled={busy} onClick={()=>{if(i!==side){setReady(false);setSide(i);}}}>{i?'图中右眼':'图中左眼'}</button>)}</div>
    <EyePixels photo={item.photo} source={item.ev.source} face={item.face} eye={side} onReady={setReady}/>
    <ExpressionReference key={[item.photo.id,item.photo.revision,item.ev.source,item.ev.frame,item.face.key].join('/')} photo={item.photo} source={item.ev.source} face={item.face}/>
    <div className="set-tools" style={{marginTop:8}}>
      {(['clear','flag','unknown'] as const).map(v=><button key={v} className="secondary small" disabled={busy||!ready}
        onKeyDown={e=>{if(e.repeat)e.preventDefault();}} onClick={e=>{if(e.detail<2)void decide(v);}}>{{clear:'确认睁眼',flag:'确认闭眼',unknown:'人工待判'}[v]}</button>)}
      {manual!=='auto'&&<button className="text-button" disabled={busy||!ready} onClick={()=>void decide('auto')}>恢复自动</button>}
    </div>
    <div className="set-tools"><small>判定后自动进入下一项</small><button className="text-button" disabled={busy||!hasNext} onClick={onNext}>跳过此项</button>
      {group&&<button className="secondary small" disabled={busy} aria-expanded={compare} onClick={()=>setCompare(!compare)}>{compare?'收起邻片':'对照相邻帧'}</button>}
      {startSeries&&<button className="secondary small" disabled={busy} onClick={startSeries}>逐张复核同组位置</button>}</div></div>
    {compare&&group&&<AdjacentFrame group={group} item={item} rows={rows} side={side}/>}
  </section>;
}

export function EyeQueue({photos,rows,groups,level,save,openPhoto,analyze,progress,busy,setBusy,series,setSeries}:{
  photos:Photo[];rows:ReviewRows;groups:Photo[][];level:number;save:SaveFace;openPhoto:(photo:Photo)=>void;analyze:()=>void;
  progress:string;busy:boolean;setBusy:(v:boolean)=>void;series:SeriesAnchor|null;setSeries:(v:SeriesAnchor|null)=>void;
}){
  const model=useMemo(()=>queueItems(photos,rows,groups,level),[photos,rows,groups,level]);
  const [filter,setFilter]=useState<Filter>('pending'),[wanted,setWanted]=useState(''),[comparing,setComparing]=useState(false);
  const seriesModel=useMemo(()=>series?positionSeries(series,model.membership.get(series.id),rows):null,[series,model.membership,rows]);
  const list=series?seriesModel?.list||[]:model.lists[filter],index=Math.max(0,list.findIndex(i=>i.id===wanted)),item=list[index];
  const group=item?model.membership.get(item.photo.id):undefined,compare=comparing&&!!group;
  const start=Math.floor(index/8)*8;
  const next=list[index+1]||list[0];
  const allProcessed=model.lists.deferred.length+model.lists.done.length>0;
  const emptyText=series?(seriesModel?'这个位置的可匹配帧已处理完。':'该位置的分析或分组已变化，请重新选择。'):filter!=='pending'?'当前范围没有此类人脸。':model.missing.length?'已分析的照片中没有待复核人脸。':
    allProcessed?'当前范围的待复核人脸已处理完。':'当前范围未发现疑难人脸。';
  return <div className="set-review-body">
    {series?<div className="set-tools" role="group" aria-label="同组位置复核">
      {seriesModel&&<><strong>{seriesModel.photo.name} · 人脸 {seriesModel.number} 的位置</strong>
        <span>待看 {list.length} 张 · 已处理 {seriesModel.processed} 张 · 位置未匹配 {seriesModel.unmatched.length} 张</span></>}
      <button className="secondary small" disabled={busy} onClick={()=>{setSeries(null);setWanted('');}}>返回疑难队列</button>
      {seriesModel&&<ReviewPhotoList photos={seriesModel.unmatched} caption="手动查看未匹配位置" open={openPhoto} disabled={busy}/>}
    </div>:<div className="set-tools" role="group" aria-label="复核进度">
      {(Object.keys(categories) as Filter[]).map(k=><button className={'secondary small '+(k===filter?'selected':'')} key={k} disabled={busy}
        aria-pressed={k===filter} onClick={()=>{setFilter(k);setWanted('');}}>{categories[k]} {model.lists[k].length}</button>)}
    </div>}
    {model.missing.length+model.old>0&&<div className="set-tools"><small>{model.missing.length} 张缺少逐脸分析{model.old>0?' · '+model.old+' 张使用旧分析':''}</small>
      <button className="secondary small" disabled={busy} onClick={analyze}>{progress?'停止分析':'分析当前范围'}</button><span role="status">{progress}</span></div>}
    {item?<div style={compare?undefined:grid}>
      <div><div className="set-tools"><button className="secondary small" disabled={busy||index===0} onClick={()=>setWanted(list[index-1].id)}>上一项</button>
        <span>{index+1} / {list.length}</span><button className="secondary small" disabled={busy||index+1>=list.length} onClick={()=>setWanted(list[index+1].id)}>下一项</button>
        {compare&&<select aria-label="队列人脸" disabled={busy} value={item.id} onChange={e=>setWanted(e.target.value)}>
          {list.slice(start,start+8).map(entry=><option key={entry.id} value={entry.id}>{entry.photo.name} · 人脸 {entry.number}</option>)}</select>}</div>
        {!compare&&<div aria-label="队列人脸" style={{display:'grid',gap:3}}>
          {list.slice(start,start+8).map((entry)=><button key={entry.id} disabled={busy} className={'secondary small '+(entry.id===item.id?'selected':'')}
            style={{justifyContent:'flex-start',textAlign:'left',overflow:'hidden',textOverflow:'ellipsis',borderColor:entry.id===item.id?'var(--accent)':undefined}} title={entry.photo.path} aria-pressed={entry.id===item.id}
            onClick={()=>setWanted(entry.id)}>{entry.photo.name} · 人脸 {entry.number} · {entry.face.manual&&entry.face.manual!=='auto'?reviewed[entry.face.manual]:automatic[entry.face.eyes[level]]}</button>)}
        </div>}
      </div>
      <CurrentFace key={item.id} item={item} level={level} group={group} rows={rows} save={save} compare={compare} setCompare={setComparing}
        startSeries={group&&!series?()=>{setSeries({id:item.photo.id,source:item.ev.source,frame:item.ev.frame,key:item.face.key});setWanted(item.id);}:undefined}
        busy={busy||!!progress} setBusy={setBusy} hasNext={list.length>1} onNext={()=>setWanted(next?.id||'')}/>
    </div>:<p role="status" className="dialog-note">{emptyText}</p>}
    {(model.unlocated.length>0||model.whole>0)&&<div className="set-tools" style={{marginTop:12}}>
      <ReviewPhotoList photos={model.unlocated} caption="未定位人脸" open={openPhoto} disabled={busy}/>
      {model.whole>0&&<small>已做整片眼睛复核 {model.whole} 张</small>}
    </div>}
  </div>;
}
