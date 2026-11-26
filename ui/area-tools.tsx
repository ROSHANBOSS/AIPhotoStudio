import React,{useEffect,useId,useLayoutEffect,useMemo,useRef,useState} from 'react';
import {NormalizedPoint,clamp,invoke,messageOf} from './model';
import type {FaceRect} from './model';
import type {WriteDraft} from './editing-drafts';

export type AreaTool='privacy'|'guard';
type GuardTool='protectRect'|'guardPaint'|'guardErase';
export type AreaMark={kind:'redactRect'|GuardTool;points:NormalizedPoint[];radius?:number|null};
export type AreaDraft={areaTool:AreaTool;areas:AreaMark[]};
type RecordState={id:string;tool:AreaTool;revision:number;base:AreaMark[];marks:AreaMark[];redactions:AreaMark[];conflict:boolean};
const empty:AreaMark[]=[];
const equal=(a:AreaMark[],b:AreaMark[])=>a===b||JSON.stringify(a)===JSON.stringify(b);

export function useAreaEditor(id:string|undefined,revision:number,tool:AreaTool|null,persist:WriteDraft){
  const [records,setRecords]=useState<Record<string,RecordState>>({});
  const [error,setError]=useState(''),[attempt,setAttempt]=useState(0);
  const [preview,setPreview]=useState(false),[replace,setReplace]=useState<number|null>(null);
  const [pickFaces,setPickFaces]=useState(false);
  const [guardTool,setGuardTool]=useState<GuardTool>('protectRect'),[radius,setRadius]=useState(.035);
  const key=(id??'')+'/'+tool,record=records[key];
  const ready=!!record&&record.revision===revision;
  useLayoutEffect(()=>{setPreview(false);setPickFaces(false);setReplace(null);setGuardTool('protectRect');setError('');},[key,revision]);
  useEffect(()=>{
    if(!id||!tool||ready)return;
    let live=true;
    const read=(areaTool:AreaTool)=>invoke<AreaMark[]>('areaData',{id,revision,areaTool});
    Promise.all([read(tool),tool==='guard'?read('privacy'):Promise.resolve(empty)]).then(([data,redactions])=>{
      if(!live)return;
      const base=data.map(m=>m.radius==null?{kind:m.kind,points:m.points}:m);
      setRecords(all=>{
        const old=all[key];const dirty=old&&!equal(old.base,old.marks)&&!equal(old.marks,base);
        const next={...all,[key]:{id,tool,revision,base,redactions,marks:dirty?old.marks:base,conflict:!!dirty&&!equal(old.base,base)}};
        if(Object.keys(next).length>16){const unused=Object.keys(next).find(k=>k!==key&&equal(next[k].base,next[k].marks));if(unused)delete next[unused];}
        return next;
      });
    }).catch(cause=>{if(live)setError(messageOf(cause));});
    return ()=>{live=false;};
  },[id,revision,tool,attempt,ready]);
  const marks=ready?record.marks:empty,dirty=useMemo(()=>!!record&&!equal(record.base,record.marks),[record]);
  const pending=useMemo(()=>Object.values(records).filter(r=>r.tool==='privacy'&&!equal(r.base,r.marks)).map(r=>r.id),[records]);
  function write(next:AreaMark[]){
    if(!ready||record.conflict)return;
    if(tool==='privacy')persist(record.id,tool,equal(record.base,next)?null:{base:record.base,marks:next});
    setRecords(previous=>({...previous,[key]:{...record,marks:next}}));setPreview(false);setReplace(null);
  }
  function restore(id:string,data:unknown){
    const entry=data as Pick<RecordState,'base'|'marks'>;
    setRecords(all=>({...all,[id+'/privacy']:{...entry,id,tool:'privacy',revision:-1,redactions:[],conflict:false}}));
  }
  function discard(id:string){
    persist(id,'privacy',null);
    setRecords(all=>{const next={...all};delete next[id+'/privacy'];return next;});
  }
  return {marks,ready,dirty,redactions:ready?record.redactions:empty,conflict:!!record?.conflict,error,preview,replace,guardTool,radius,setRadius,
    pickGuard:(kind:GuardTool)=>{setGuardTool(kind);setReplace(null);setPreview(false);},
    setPreview:(value:boolean)=>{setPreview(value);setReplace(null);},
    replaceAt:(index:number)=>{setReplace(replace===index?null:index);setGuardTool('protectRect');setPreview(false);},
    add:(mark:AreaMark)=>{if(replace!==null)write(marks.map((m,i)=>i===replace?mark:m));else if(marks.length<64)write([...marks,mark]);else setError('已有 64 处标记，请先移除或重画现有范围。');},
    remove:(index:number)=>write(marks.filter((_,i)=>i!==index)),
    restore,discard,pickFaces,setPickFaces,move:(index:number,mark:AreaMark)=>write(marks.map((m,i)=>i===index?mark:m)),
    reset:()=>{if(record){if(tool==='privacy')persist(record.id,tool,null);setRecords(all=>({...all,[key]:{...record,marks:record.base,conflict:false}}));}setReplace(null);setPreview(false);},
    retry:()=>{setError('');setAttempt(a=>a+1);},
    saved:()=>{if(record&&tool==='privacy')persist(record.id,tool,null);setRecords(all=>{const current=all[key];return current?{...all,[key]:{...current,base:current.marks,conflict:false}}:all;});},
    pendingPrivacy:(ids?:string[])=>pending.find(id=>!ids||ids.includes(id)),
  };
}
type Editor=ReturnType<typeof useAreaEditor>;

function rectOf(m:AreaMark){
  const [a,b]=m.points;return {x:Math.min(a.x,b.x),y:Math.min(a.y,b.y),width:Math.abs(a.x-b.x),height:Math.abs(a.y-b.y)};
}
function faceMark(face:FaceRect):AreaMark{
  const padX=face.width*.09,padY=face.height*.12;
  return {kind:'redactRect',points:[{x:clamp(face.x-padX,0,1),y:clamp(face.y-padY,0,1)},
    {x:clamp(face.x+face.width+padX,0,1),y:clamp(face.y+face.height+padY,0,1)}]};
}

function GuardShape({mark,w,h}:{mark:AreaMark;w:number;h:number}){
  if(mark.kind==='protectRect'){
    if(mark.points.length<2)return null;
    const rect=rectOf(mark);return <rect x={rect.x*w} y={rect.y*h} width={rect.width*w} height={rect.height*h} fill="white"/>;
  }
  const color=mark.kind==='guardErase'?'black':'white',r=(mark.radius??.035)*Math.min(w,h);
  if(mark.points.length===1)return <circle cx={mark.points[0].x*w} cy={mark.points[0].y*h} r={r} fill={color}/>;
  return <polyline points={mark.points.map(p=>(p.x*w)+','+(p.y*h)).join(' ')} fill="none" stroke={color} strokeWidth={2*r} strokeLinecap="round" strokeLinejoin="round"/>;
}

export function AreaOverlay({editor,tool,busy,w,h,faces}:{editor:Editor;tool:AreaTool;busy:boolean;w:number;h:number;faces:FaceRect[]}){
  const [gesture,setGesture]=useState<AreaMark|null>(null);
  const [hover,setHover]=useState<NormalizedPoint|null>(null);
  const brushing=tool==='guard'&&editor.guardTool!=='protectRect';
  const active=useRef<{pointer:number;mark:AreaMark;move?:{index:number;box:ReturnType<typeof rectOf>;start:NormalizedPoint}}|null>(null);
  const maskId=useId().replace(/:/g,'');
  useLayoutEffect(()=>{active.current=null;setGesture(null);setHover(null);},[tool,editor.ready,editor.replace,editor.guardTool,busy]);
  function point(e:React.PointerEvent<HTMLDivElement>){
    const rect=e.currentTarget.getBoundingClientRect();return {x:clamp((e.clientX-rect.left)/rect.width,0,1),y:clamp((e.clientY-rect.top)/rect.height,0,1)};
  }
  function extend(mark:AreaMark,p:NormalizedPoint){
    if(mark.kind==='protectRect'||mark.kind==='redactRect')return {...mark,points:[mark.points[0],p]};
    const last=mark.points[mark.points.length-1];if(Math.hypot((p.x-last.x)*w,(p.y-last.y)*h)<.8)return mark;
    const points=mark.points.length<512?mark.points:mark.points.filter((_p,i)=>i===0||i%2===1);
    return {...mark,points:[...points,p]};
  }
  const target=active.current?.move?.index??editor.replace;
  const displayed=gesture&&target!==null?editor.marks.map((m,i)=>i===target?gesture:m):gesture?[...editor.marks,gesture]:editor.marks;
  function moved(p:NormalizedPoint){
    const {box,start}=active.current!.move!;
    const x=clamp(box.x+p.x-start.x,0,1-box.width),y=clamp(box.y+p.y-start.y,0,1-box.height);
    return {kind:'redactRect' as const,points:[{x,y},{x:x+box.width,y:y+box.height}]};
  }
  return <div className="area-overlay" aria-label={tool==='privacy'?'隐私遮挡画布':'文字保护画布'} aria-busy={!editor.ready} onPointerDown={e=>{
    if(e.button!==0||active.current||!editor.ready||editor.conflict||busy)return;
    e.preventDefault();e.stopPropagation();e.currentTarget.setPointerCapture(e.pointerId);
    const p=point(e),kind=tool==='privacy'?'redactRect':editor.guardTool;
    const hit=(e.target as HTMLElement).dataset.move;
    if(tool==='privacy'&&editor.replace===null&&hit!==undefined){
      const index=Number(hit),mark=editor.marks[index];
      active.current={pointer:e.pointerId,mark,move:{index,box:rectOf(mark),start:p}};return;
    }
    const mark:AreaMark=kind==='redactRect'||kind==='protectRect'?{kind,points:[p,p]}:{kind,points:[p],radius:editor.radius};
    active.current={pointer:e.pointerId,mark};setGesture(mark);
  }} onPointerMove={e=>{
    const p=point(e);if(brushing)setHover(p);
    if(active.current?.pointer!==e.pointerId)return;e.stopPropagation();active.current.mark=active.current.move?moved(p):extend(active.current.mark,p);setGesture(active.current.mark);
  }} onPointerUp={e=>{
    const job=active.current;if(job?.pointer!==e.pointerId)return;e.stopPropagation();
    if(job.move){
      const p=point(e);
      if(Math.abs(p.x-job.move.start.x)+Math.abs(p.y-job.move.start.y)>.001)editor.move(job.move.index,moved(p));
      active.current=null;setGesture(null);return;
    }
    active.current=null;setGesture(null);
    const mark=extend(job.mark,point(e));
    if(mark.kind==='guardPaint'||mark.kind==='guardErase')editor.add(mark);
    else {const box=rectOf(mark),rect=e.currentTarget.getBoundingClientRect();if(box.width*rect.width>=1&&box.height*rect.height>=1)editor.add(mark);}
  }} onPointerLeave={()=>setHover(null)} onLostPointerCapture={()=>{active.current=null;setGesture(null);}} onPointerCancel={()=>{active.current=null;setGesture(null);}}>
    {tool==='guard'&&<svg className="area-guard-overlay" viewBox={'0 0 '+w+' '+h} preserveAspectRatio="none" aria-hidden="true">
      <defs><mask id={maskId} maskUnits="userSpaceOnUse" x="0" y="0" width={w} height={h}><rect width={w} height={h} fill="black"/>
        {displayed.map((m,i)=><GuardShape key={i} mark={m} w={w} h={h}/>)}</mask></defs>
      <rect width={w} height={h} fill="#37dbc0" opacity=".45" mask={'url(#'+maskId+')'}/>
      {brushing&&hover&&<circle cx={hover.x*w} cy={hover.y*h} r={editor.radius*Math.min(w,h)} stroke="white" strokeWidth="1.5" fill="none" vectorEffect="non-scaling-stroke"/>}</svg>}
    {(tool==='privacy'?displayed:editor.redactions).map((mark,i)=>{const box=rectOf(mark);return <span key={i} className="area-box redaction" data-move={tool==='privacy'?i:undefined} style={{left:box.x*100+'%',top:box.y*100+'%',width:box.width*100+'%',height:box.height*100+'%',
      pointerEvents:tool==='privacy'&&editor.replace===null?'auto':'none',cursor:'move'}}/>;})}
    {tool==='privacy'&&editor.pickFaces&&editor.replace===null&&faces.map((face,i)=>{
      const mark=faceMark(face),selected=editor.marks.some(m=>equal([m],[mark]));
      if(selected)return null;
      return <button type="button" key={i} className="person-box privacy-face" aria-label={'遮挡画面中的人脸 '+(i+1)}
        style={{left:face.x*100+'%',top:face.y*100+'%',width:face.width*100+'%',height:face.height*100+'%'}}
        disabled={!editor.ready||busy||editor.conflict} onPointerDown={e=>e.stopPropagation()} onClick={e=>{e.stopPropagation();editor.add(mark);}}><span>人脸 {i+1}</span></button>;
    })}
  </div>;
}

export function AreaPanel({editor,tool,busy,close,save,faces,detect,detecting,faceError}:{editor:Editor;tool:AreaTool;busy:boolean;close:()=>void;save:()=>void;
  faces:FaceRect[];detect?:()=>void;detecting:boolean;faceError:string}){
  const privacy=tool==='privacy';const labels:Record<AreaMark['kind'],string>={redactRect:'遮挡',protectRect:'保护框',guardPaint:'补保护',guardErase:'擦保护'};
  return <section className="area-panel"><h2 className="body-title">{privacy?'隐私遮挡':'文字与直线保护'}</h2>
    <p className="local-hint">{privacy?'在原片上拖框遮住姓名、工牌或人脸。拖动黑框可挪动位置。应用后，遮挡会写进成品。':'在原片上框住标题或门框，青色区域在人体美型时保持原位。可用画笔补边或擦开重叠的肩部。'}</p>
    {privacy&&(detect||faces.length>0)&&<><button className="secondary small" disabled={busy||!editor.ready||detecting||editor.conflict}
      aria-pressed={editor.pickFaces} onClick={()=>{editor.setPreview(false);editor.setPickFaces(!editor.pickFaces);if(!editor.pickFaces&&!faces.length)detect?.();}}>点选人脸遮挡</button>
      {editor.pickFaces&&<p role="status">{detecting?'正在本机检测人脸…':faceError||(!faces.length?'未找到可选人脸，可手动画框。':'点击画面上的人脸框。遗漏的人脸可手动画框。')}</p>}</>}
    {!privacy&&<><div className="area-actions">{(['protectRect','guardPaint','guardErase'] as GuardTool[]).map(kind=><button key={kind} aria-pressed={editor.guardTool===kind} disabled={busy||!editor.ready} onClick={()=>editor.pickGuard(kind)}>{kind==='protectRect'?'画保护框':labels[kind]}</button>)}</div>
      {editor.guardTool!=='protectRect'&&<label className="area-radius">笔刷半径 <input aria-label="保护笔半径" type="range" min="0.003" max="0.18" step="0.001" value={editor.radius} onChange={e=>editor.setRadius(Number(e.target.value))}/></label>}</>}
    {editor.error?<p role="alert">{editor.error} <button onClick={editor.retry}>重读选区</button></p>:!editor.ready?<p role="status">正在读取选区…</p>:null}
    {editor.conflict&&<p role="alert">这张照片的选区已经改变。请点“撤回草稿”载入当前版本。</p>}
    {editor.marks.map((m,i)=><div className="area-row" key={i}><span>{labels[m.kind]} {i+1}</span>
      {(m.kind==='redactRect'||m.kind==='protectRect')&&<button disabled={busy||editor.conflict} aria-pressed={editor.replace===i} onClick={()=>editor.replaceAt(i)}>重画</button>}
      <button disabled={busy||editor.conflict} aria-label={'移除'+labels[m.kind]+' '+(i+1)} onClick={()=>editor.remove(i)}>移除</button></div>)}
    {editor.replace!==null&&<p role="status">拖出新框，替换第 {editor.replace+1} 处{privacy?'遮挡':'保护范围'}。</p>}
    <div className="area-actions"><button disabled={!editor.ready||busy||editor.conflict} onClick={()=>editor.setPreview(!editor.preview)}>{editor.preview?'回到原片定位':'预览成品'}</button>
      <button disabled={!editor.dirty||busy} onClick={editor.reset}>撤回草稿</button>
      <button className="primary" disabled={!editor.ready||!editor.dirty||busy||editor.conflict} onClick={save}>{privacy?'应用遮挡':'应用保护范围'}</button>
      <button onClick={close} disabled={busy}>{privacy?'返回调色':'返回人体美型'}</button></div>
  </section>;
}
