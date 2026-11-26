import React, {useEffect, useLayoutEffect, useMemo, useRef, useState} from 'react';
import {BodyPerson, BrushStroke, FaceRect, NormalizedPoint, Photo, clamp, invoke, messageOf} from './model';
import {Icon, IconName} from './icons';
import {IconButton} from './widgets';
import {MaskCoverage, MaskTrace, addMaskSegment, brushCoverage, displayMask, isErase, isMaskStroke, isPaint} from './maskCoverage';
import type {MaskDisplay} from './maskCoverage';
import {Category,MaskBox,detectMask} from './mask-detector';
import {MaskList,ManageMask} from './mask-list';
import {GradientHandles, isGradientKind, validGradient} from './gradient';
import type {GradientGeometry, GradientKind, GradientMask, GradientPreview} from './gradient';
import {isRangeKind,useRangeMask} from './range-mask';
import type {RangeKind,RangePreview,RangeSettings} from './range-mask';
import {useBrushGuide,seedEdge,liveEdge,packEdge} from './brush-edge';
import type {EdgeDomain} from './brush-edge';
import type {WriteDraft} from './editing-drafts';

type Tool = Exclude<BrushStroke['kind'],'maskPaint'|'maskErase'>|'restore'|'hand';
export type LocalSection='brush'|'face'|'segment';
export function localSection(wanted:LocalSection,brush:boolean,face:boolean,segment:boolean):LocalSection{
  if(wanted==='segment'&&segment)return 'segment';
  if(wanted==='face'&&face)return 'face';
  if(wanted==='brush'&&brush)return 'brush';
  return brush?'brush':face?'face':'segment';
}
type MaskInfo={slot:number;name:string;kind:string};
type MaskState={masks:MaskInfo[];slot:number;alpha?:string|null;guard?:string|null;geometry?:GradientGeometry|null;range?:RangeSettings|null;structure?:BrushStroke[]};
type Markings = MaskState & {id:string;revision:number;base:BrushStroke[];draft:BrushStroke[];conflict:boolean;
  past:BrushStroke[][];future:BrushStroke[][];draftTool?:Tool};
type FaceResult = {id:string;revision:number;faces:FaceRect[];selected?:number};
const emptyStrokes:BrushStroke[]=[];
const modes: {value:Tool; label:string; icon:IconName; hint:string}[]=[
  {value:'paint',label:'画笔',icon:'brush',hint:'在照片上涂出要调整的区域，可以连续画多笔。'},
  {value:'erase',label:'擦除',icon:'reset',hint:'沿多涂的地方擦过，留下的选区会受到调整。'},
  {value:'protect',label:'保护',icon:'lock',hint:'涂出需要保留原状的背景或直线。'},
  {value:'warp',label:'液化',icon:'channels',hint:'沿网格拖动目标，网格线帮助定位。'},
  {value:'heal',label:'手动瑕疵修复',icon:'target',hint:'在瑕疵中心单击一次，检查修复效果后保存笔迹。'},
  {value:'redEye',label:'单眼红眼修正',icon:'target',hint:'用圆圈套住一只红瞳，点击瞳孔中心。白色眼神光会保留。'},
  {value:'sampleRepair',label:'取样修污点',icon:'target',hint:'把画笔调得比污点稍大。遇到接缝，可沿同一条缝选取干净纹理。'},
  {value:'restore',label:'恢复笔',icon:'reset',hint:'擦回之前的瑕疵修复、液化和该处的手绘选区。'},
  {value:'hand',label:'平移',icon:'hand',hint:'拖动画布查看放大后的照片。'},
];
const equalStrokes=(a:BrushStroke[],b:BrushStroke[])=>a===b||a.length===b.length&&a.every((s,i)=>{
  const t=b[i];return s===t||s.kind===t.kind&&s.radius===t.radius&&s.strength===t.strength&&s.edge===t.edge&&
    s.points.length===t.points.length&&s.points.every((p,j)=>p.x===t.points[j].x&&p.y===t.points[j].y);
});
export const isSpotRepair=(kind:Tool|BrushStroke['kind'])=>kind==='heal'||kind==='sampleRepair'||kind==='redEye';
// Older full-tool erase strokes can be part of a repair, and older paint can bound a warp.
// Removing such history would roll back the repair itself. New mask strokes can edit over it.
function firstEditableMaskStroke(strokes:BrushStroke[]){
  let first=0,haveHeal=false;
  strokes.forEach((stroke,i)=>{
    if(isSpotRepair(stroke.kind))haveHeal=true;
    if(stroke.kind==='warp'||(stroke.kind==='erase'&&haveHeal))first=i+1;
  });
  return first;
}
function validFace(f:FaceRect) {
  return f && [f.x,f.y,f.width,f.height].every(Number.isFinite) && f.x>=0 && f.y>=0 && f.width>0 && f.height>0 && f.x+f.width<=1.001 && f.y+f.height<=1.001;
}

export function useLocalEdits(photo:Photo|null, revision:number, read:boolean, notify:(s:string,err?:boolean)=>void, sampleActive:boolean,persist:WriteDraft) {
  const [records,setRecords]=useState<Record<string,Markings>>({});
  const [loadError,setLoadError]=useState('');
  const [reload,setReload]=useState(0);
  const [tool,setTool]=useState<Tool>('paint');
  const [choice,setChoice]=useState({id:'',slot:0});
  const structure=!['paint','erase','hand'].includes(tool);
  const wanted=structure?0:choice.id===photo?.id?choice.slot:0;
  const knownMasks=Object.values(records).find(r=>r.id===photo?.id&&r.revision===revision)?.masks;
  const recordKey=(photo?.id||'')+'#'+(structure&&knownMasks&&!knownMasks.some(m=>m.slot===0)?'structure':wanted);
  const record=photo?records[recordKey]:undefined;
  const ready=!!record && record.revision===revision;
  const [category,setCategory]=useState<{key:string;kind:Category;alpha?:string;guard?:string|null;token?:string;error?:string}|null>(null);
  const [gradientJob,setGradientJob]=useState<(GradientPreview & {key:string})|null>(null);
  const categoryJob=useRef(0);
  const [radius,setRadius]=useState(.065);
  const [strength,setStrength]=useState(1);
  const [showOverlay,setShowOverlay]=useState(true);
  const [displayMode,setDisplayMode]=useState<MaskDisplay>('pink');
  const [heldMask,setHeldMask]=useState('');
  const [garmentGuide,setGarmentGuide]=useState<{left:number;top:number;width:number}|null>(null);
  const [inspectingLight,setInspectingLight]=useState(false);
  const visible=showOverlay&&!inspectingLight;
  const setVisible=(value:boolean)=>{setShowOverlay(value);setInspectingLight(false);};
  const [healPreview,setHealPreview]=useState(false);
  const [section,setSection]=useState<LocalSection>('brush');
  const edgeBrush=useBrushGuide(photo?.id,revision,sampleActive&&section==='brush'&&(tool==='paint'||tool==='erase'));
  const [detected,setDetected]=useState<FaceResult|null>(null);
  const [detecting,setDetecting]=useState(false);
  const [faceError,setFaceError]=useState('');
  const activeKey=useRef('');activeKey.current=(photo?.id||'')+'@'+revision;
  const [sample,setSample]=useState<{key:string;point:NormalizedPoint}|null>(null);
  const samplePoint=sampleActive&&section==='brush'&&tool==='sampleRepair'&&sample?.key===activeKey.current?sample.point:null;
  useEffect(()=>{setSample(null);},[photo?.id,revision,section,tool,sampleActive]);
  const detectionJob=useRef(0);
  const strokes=ready?record.draft:emptyStrokes;
  const candidate=category?.key===activeKey.current?category:null;
  const kind=ready?record.masks.find(m=>m.slot===record.slot)?.kind:undefined;
  const savedRange=ready&&isRangeKind(kind)&&record.range?{kind,range:record.range}:null;
  const rangeEditor=useRangeMask(photo?.id,revision,read&&section==='brush'&&!structure),range=rangeEditor.job;
  const rangePreview=useMemo<RangePreview|undefined>(()=>range?.sampled&&!range.creating&&range.alpha?
    {kind:range.kind,range:range.range,creating:false}:undefined,[range]);
  const savedGradient=useMemo<GradientMask|null>(()=>ready&&record.geometry&&isGradientKind(kind)?{kind,geometry:record.geometry}:null,[ready,record?.geometry,kind]);
  const gradient=gradientJob?.key===activeKey.current&&section==='brush'&&read?gradientJob:null;
  const gradientPreview=useMemo<GradientPreview|undefined>(()=>gradient&&validGradient(gradient)?
    {kind:gradient.kind,geometry:gradient.geometry,creating:gradient.creating}:undefined,[gradient]);
  const holdKey=sampleActive&&section==='brush'&&!structure&&ready&&record.slot>=0&&!record.conflict&&
    (!candidate||!!candidate.error)&&!gradient?.creating&&!range?.creating?recordKey+'@'+revision+'#'+record.slot:'';
  const bypassSlot=holdKey&&heldMask===holdKey?record?.slot:undefined;
  useEffect(()=>{setHeldMask('');},[holdKey]);
  useEffect(()=>{
    const release=()=>setHeldMask('');
    const visibility=()=>{if(document.hidden)release();};
    window.addEventListener('blur',release);
    document.addEventListener('visibilitychange',visibility);
    return ()=>{window.removeEventListener('blur',release);document.removeEventListener('visibilitychange',visibility);};
  },[]);
  const shownGradient=candidate?.alpha||range?null:gradient?gradientPreview:savedGradient;
  const displayedStrokes=candidate?.alpha||gradient?.creating||range?.creating?emptyStrokes:strokes;
  const prediction=range?range.alpha:gradient?null:candidate?.alpha||(ready?record.alpha:null);
  const guard=range||gradient?null:candidate?.alpha?candidate.guard:ready?record.guard:null;
  const previousCoverage=useRef<MaskCoverage|null>(null);
  const coverage=useMemo(()=>ready&&read&&photo?brushCoverage(Math.max(1,photo.width),Math.max(1,photo.height),displayedStrokes,previousCoverage.current,prediction,guard,shownGradient):null,
    [ready,read,photo?.id,photo?.width,photo?.height,displayedStrokes,prediction,guard,shownGradient]);
  useLayoutEffect(()=>{if(coverage)previousCoverage.current=coverage;},[coverage]);
  useEffect(()=>{
    setLoadError('');
    if(!photo || !read || ready)return;
    let live=true;
    const id=photo.id;
    invoke<Partial<MaskState>&{strokes:BrushStroke[]}>('localData',{id,revision,maskSlot:wanted,structure}).then(result=>{
      if(!live)return;
      if(!Array.isArray(result?.strokes))throw new Error('无法读取当前照片的笔迹。');
      const base=result.strokes;
      setRecords(all=>{
        const old=all[recordKey], hasDraft=old&&old.slot===(result.slot??0)&&!equalStrokes(old.base,old.draft);
        const conflict=!!hasDraft&&!equalStrokes(old.base,base)&&!equalStrokes(old.draft,base);
        const fresh={id,revision,slot:result.slot??0,masks:result.masks??[{slot:0,name:'手绘蒙版 1',kind:'brush'}],
          alpha:result.alpha,guard:result.guard,geometry:result.geometry,range:result.range,structure:result.structure,base,draft:hasDraft?old.draft:base,conflict,
          past:hasDraft&&!conflict?old.past:[],future:hasDraft&&!conflict?old.future:[],draftTool:old?.draftTool};
        const next={...all,[recordKey]:fresh};
        if(Object.keys(next).length>16){
          const evict=Object.keys(next).find(key=>key!==recordKey&&equalStrokes(next[key].base,next[key].draft));
          if(evict)delete next[evict];
        }
        return next;
      });
    }).catch(error=>{if(live)setLoadError(messageOf(error));});
    return ()=>{live=false;};
  },[photo?.id,revision,read,reload,ready,recordKey]);
  useEffect(()=>{detectionJob.current++;setDetecting(false);setFaceError('');},[photo?.id,revision]);
  useEffect(()=>{setHealPreview(false);setInspectingLight(false);},[photo?.id]);
  useEffect(()=>{categoryJob.current++;setCategory(null);setGradientJob(null);},[photo?.id,revision,section,tool,read]);
  const dirty=ready && !equalStrokes(record.base,record.draft);
  const draftKind='brush-'+recordKey.split('#')[1];
  const update=(fn:(r:Markings)=>Markings,history=true)=>{
    if(!record||!ready)return;
    let next=fn(record);
    if(next===record)return;
    if(history)next={...next,past:[...record.past,record.draft],future:[],draftTool:tool};
    persist(record.id,draftKind,equalStrokes(next.base,next.draft)?null:{slot:next.slot,base:next.base,draft:next.draft,tool:next.draftTool??tool});
    setRecords(all=>({...all,[recordKey]:next}));
  };
  function restore(id:string,kind:string,data:unknown){
    const saved=data as {slot:number;base:BrushStroke[];draft:BrushStroke[];tool:Tool};
    const key=id+'#'+kind.slice(6),past:BrushStroke[][]=[saved.base];
    if(saved.draft.length>saved.base.length&&equalStrokes(saved.base,saved.draft.slice(0,saved.base.length)))
      for(let i=saved.base.length+1;i<saved.draft.length;i++)past.push(saved.draft.slice(0,i));
    setRecords(all=>({...all,[key]:{...saved,id,revision:-1,masks:[],conflict:false,past,future:[],draftTool:saved.tool}}));
  }
  function discardRecovered(id:string,kind:string){
    persist(id,kind,null);
    setRecords(all=>{const next={...all};delete next[id+'#'+kind.slice(6)];return next;});
  }
  function openRecovered(id:string,kind:string){
    const saved=records[id+'#'+kind.slice(6)];
    if(saved){chooseMask(saved.slot);setTool(saved.draftTool??'paint');setSection('brush');setVisible(true);}
  }
  function stepDraft(redo:boolean){
    if(!ready||record.conflict)return;
    const from=redo?record.future:record.past;
    if(!from.length)return;
    update(r=>({...r,draft:from[from.length-1],past:redo?[...r.past,r.draft]:r.past.slice(0,-1),
      future:redo?r.future.slice(0,-1):[...r.future,r.draft]}),false);
  }
  const add=(stroke:BrushStroke)=>{
    if(!ready || record.conflict || stroke.points.length===0)return false;
    const total=record.draft.reduce((n,s)=>n+s.points.length,stroke.points.length);
    if(record.draft.length>=256||total>8192){notify('每张照片最多支持 256 笔、8192 个控制点。请撤回部分笔迹后再试。',true);return false;}
    update(r=>({...r,draft:[...r.draft,stroke]}));
    return true;
  };
  const sampleClick=(stroke:BrushStroke)=>{
    const target=stroke.points[0];
    if(!samplePoint){setSample({key:activeKey.current,point:target});return false;}
    const samePixel=(a:number,b:number,dimension:number)=>Math.round(a*(dimension-1))===Math.round(b*(dimension-1));
    if(photo&&samePixel(target.x,samplePoint.x,photo.width)&&samePixel(target.y,samplePoint.y,photo.height)){
      notify('请点击干净取样点以外的污点。');return false;
    }
    if(!add({...stroke,points:[target,samplePoint]}))return false;
    setSample(null);return true;
  };
  const save=async(writer:(id:string,revision:number,strokes:BrushStroke[],slot:number)=>Promise<number|null>)=>{
    if(!ready||!dirty||record.conflict)return;
    const base=record;
    const rev=await writer(base.id,base.revision,base.draft,base.slot);
    if(rev===null)return;
    persist(base.id,draftKind,null);
    setRecords(all=>({...all,[recordKey]:{...base,revision:rev,base:base.draft,conflict:false,past:[],future:[]}}));
  };
  const chooseMask=(slot:number)=>{
    if(!photo)return;
    rangeEditor.cancel();
    categoryJob.current++;setCategory(null);setGradientJob(null);setInspectingLight(false);setShowOverlay(true);
    setChoice({id:photo.id,slot:Math.max(0,slot)});
  };
  const startGradient=(kind:GradientKind,creating=true)=>{
    if(!ready||dirty||(!creating&&savedGradient?.kind!==kind))return;
    rangeEditor.cancel();
    categoryJob.current++;setCategory(null);setInspectingLight(false);setShowOverlay(true);
    const geometry=!creating&&savedGradient?savedGradient.geometry:{x:0,y:0,dx:0,dy:0,feather:kind==='linear'?1:.5,invert:false};
    setGradientJob({key:activeKey.current,kind,creating,geometry});
  };
  const changeGradient=(geometry:GradientGeometry)=>{
    setGradientJob(job=>job?.key===activeKey.current?{...job,geometry}:job);
  };
  const gradientChanged=!!gradientPreview&&(gradientPreview.creating||!savedGradient||
    (Object.keys(gradientPreview.geometry) as (keyof GradientGeometry)[]).some(k=>gradientPreview.geometry[k]!==savedGradient.geometry[k]));
  const analyzeMask=async(kind:Category,personIndex?:number,garmentPart?:'upper'|'lower',garmentLine?:number)=>{
    if(!photo||dirty)return;
    rangeEditor.cancel();
    const key=activeKey.current,seq=++categoryJob.current;
    setInspectingLight(false);setShowOverlay(true);setCategory({key,kind});
    try{
      const result=await invoke<{token:string;image:string;box?:MaskBox}>('maskInput',{id:photo.id,revision,kind,personIndex,garmentPart,garmentLine});
      if(categoryJob.current!==seq||key!==activeKey.current)return;
      const alpha=await detectMask(result.image,kind,result.box);
      if(categoryJob.current!==seq||key!==activeKey.current)return;
      const preview=await invoke<{alpha:string;guard:string|null}>('maskPreview',{id:photo.id,revision,alpha,token:result.token});
      if(categoryJob.current===seq&&key===activeKey.current)setCategory({key,kind,...preview,token:result.token});
    }catch(error){if(categoryJob.current===seq&&key===activeKey.current)setCategory({key,kind,error:messageOf(error)});}
  };
  const analyze=async()=>{
    if(!photo || detecting)return;
    const id=photo.id, key=activeKey.current, token=++detectionJob.current;
    setDetecting(true);setFaceError('');setDetected(null);
    try{
      const result=await invoke<FaceResult>('analyzeFaces',{id,revision});
      if(activeKey.current!==key || token!==detectionJob.current)return;
      if(result?.id!==id||result.revision!==revision||!Array.isArray(result.faces)||!result.faces.every(validFace))throw new Error('人脸检测结果不可用于当前照片。');
      setDetected(result);
    }catch(error){if(token===detectionJob.current && activeKey.current===key)setFaceError(messageOf(error));}
    finally{if(token===detectionJob.current)setDetecting(false);}
  };
  const faces=detected?.id===photo?.id&&detected?.revision===revision?detected:null;
  const selectFace=async(index:number,writer:(id:string,rev:number,index:number)=>Promise<number|null>)=>{
    if(!faces||!faces.faces[index])return;
    const result=await writer(faces.id,faces.revision,index);
    if(result!==null)setDetected({...faces,revision:result,selected:index});
  };
  const chooseTool=(value:Tool)=>{setTool(value);setInspectingLight(false);setSample(null);if(isSpotRepair(value))setHealPreview(false);
    if(value==='redEye')setRadius(.015);else if(tool==='redEye')setRadius(r=>Math.max(.005,r));};
  return {ready,dirty,tool,setTool:chooseTool,radius,setRadius,strength,setStrength,visible,setVisible,displayMode,setDisplayMode,healPreview,setHealPreview,loadError,garmentGuide,setGarmentGuide,
    bypassSlot,canBypass:!!holdKey,holdMask:()=>setHeldMask(holdKey),releaseMask:()=>setHeldMask(''),
    edgeBrush,samplePoint,sampleClick,clearSample:()=>setSample(null),
    slot:ready?record.slot:wanted,masks:ready?record.masks:[],category:candidate,alpha:ready?record.alpha:null,
    gradient,savedGradient,gradientPreview,gradientChanged,startGradient,changeGradient,cancelGradient:()=>setGradientJob(null),
    range,rangePreview,savedRange,changeRange:rangeEditor.change,pickRange:rangeEditor.pick,cancelRange:rangeEditor.cancel,
    startRange:(kind:RangeKind,creating=true,point?:NormalizedPoint)=>{
      if(!ready||dirty||(!creating&&savedRange?.kind!==kind))return;
      categoryJob.current++;setCategory(null);setGradientJob(null);setInspectingLight(false);setShowOverlay(true);
      rangeEditor.start(kind,creating?undefined:savedRange?.range,point);
    },
    structure:ready?record.structure??record.base:emptyStrokes,chooseMask,analyzeMask,cancelMask:()=>{categoryJob.current++;setCategory(null);},
    strokes,coverage,hasPaint:!!coverage?.hasPaint,inspectLight:()=>setInspectingLight(true),resumePainting:()=>setInspectingLight(false),
    base:ready?record.base:emptyStrokes,add,save,conflict:ready&&record.conflict,
    retry:()=>setReload(i=>i+1),undo:(paintOnly:boolean)=>update(r=>{
      let last=r.draft.length-1;
      while(paintOnly&&last>=0&&!isPaint(r.draft[last].kind)&&!isErase(r.draft[last].kind))last--;
      if(paintOnly&&last<firstEditableMaskStroke(r.draft))return r;
      return {...r,draft:r.draft.filter((_s,i)=>i!==last)};
    }),
    stepDraft,canUndoDraft:ready&&!record.conflict&&!!record.past.length,canRedoDraft:ready&&!record.conflict&&!!record.future.length,
    restoreDraft:restore,discardRecovered,openRecovered,
    discard:()=>update(r=>({...r,draft:r.base,conflict:false,past:[],future:[]}),false),reconcile:()=>update(r=>({...r,conflict:false})),
    clear:(paintOnly:boolean)=>update(r=>{
      const first=firstEditableMaskStroke(r.draft);
      return {...r,draft:paintOnly?r.draft.filter((s,i)=>i<first||(!isPaint(s.kind)&&!isErase(s.kind))):[]};
    }),
    section,setSection,faces,detecting,faceError,analyze,selectFace};
}
export type LocalEditor = ReturnType<typeof useLocalEdits>;

function drawMarks(context:CanvasRenderingContext2D,w:number,h:number,strokes:BrushStroke[]) {
  const p=(point:NormalizedPoint)=>[point.x*w,point.y*h] as const;
  for(const stroke of strokes){
    if(!stroke.points.length||(stroke.kind!=='warp'&&!isSpotRepair(stroke.kind)))continue;
    const radius=stroke.radius*Math.min(w,h);
    context.save();
    context.lineCap='round';context.lineJoin='round';
    if(isSpotRepair(stroke.kind)){
      const [x,y]=p(stroke.points[0]);
      context.strokeStyle='rgba(223,194,242,.85)';
      context.lineWidth=Math.max(1,Math.min(w,h)*.0018);
      context.setLineDash([Math.max(2,radius*.23),Math.max(2,radius*.17)]);
      context.beginPath();context.arc(x,y,radius,0,2*Math.PI);context.stroke();
      if(stroke.kind==='sampleRepair'&&stroke.points[1]){
        const [sx,sy]=p(stroke.points[1]);context.strokeStyle='#8cddcc';
        context.beginPath();context.moveTo(sx,sy);context.lineTo(x,y);context.stroke();
        context.setLineDash([]);context.beginPath();context.arc(sx,sy,radius,0,2*Math.PI);context.stroke();
      }
    }else if(stroke.kind==='warp'){
      if(stroke.points.length<2){context.restore();continue;}
      const [x,y]=p(stroke.points[0]),[tx,ty]=p(stroke.points[stroke.points.length-1]);
      const angle=Math.atan2(ty-y,tx-x),size=Math.max(5,Math.min(w,h)*.012);
      context.lineWidth=Math.max(1.3,size*.17);context.strokeStyle='rgba(255,218,157,.95)';context.fillStyle=context.strokeStyle;
      context.beginPath();context.moveTo(x,y);context.lineTo(tx,ty);context.stroke();
      context.beginPath();context.moveTo(tx,ty);context.lineTo(tx-size*Math.cos(angle-.5),ty-size*Math.sin(angle-.5));context.lineTo(tx-size*Math.cos(angle+.5),ty-size*Math.sin(angle+.5));context.closePath();context.fill();
      context.setLineDash([size*.5,size*.5]);context.strokeStyle='rgba(255,218,157,.6)';context.beginPath();context.arc(x,y,radius,0,2*Math.PI);context.stroke();
    }
    context.restore();
  }
}

export function LocalOverlay({photo,local,busy,faceMode,originalHeal=false}: {photo:Photo;local:LocalEditor;busy:boolean;faceMode:boolean;originalHeal?:boolean}) {
  const holder=useRef<HTMLDivElement>(null),baseCanvas=useRef<HTMLCanvasElement>(null),bitmap=useRef<HTMLCanvasElement|null>(null);
  const drawing=useRef<{stroke:BrushStroke;pointer:number;minDist:number;limit:number;trace?:MaskTrace;domain?:EdgeDomain} | null>(null);
  const animation=useRef(0);
  const [cursor,setCursor]=useState<NormalizedPoint|null>(null);
  const [canvasSize,setCanvasSize]=useState({w:1,h:1});
  useLayoutEffect(()=>{
    if(!holder.current)return;
    const measure=()=>{const r=holder.current!.getBoundingClientRect();const k=Math.min(2,window.devicePixelRatio||1,1600/Math.max(1,r.width,r.height));setCanvasSize({w:Math.max(1,Math.round(r.width*k)),h:Math.max(1,Math.round(r.height*k))});};
    const ob=new ResizeObserver(measure);ob.observe(holder.current);measure();return ()=>ob.disconnect();
  },[]);
  const renderOverlay=()=>{
    const context=baseCanvas.current?.getContext('2d'); if(!context)return;
    const {w,h}=canvasSize;context.clearRect(0,0,w,h);
    if(!local.visible||faceMode)return;
    if(local.coverage){
      if(!bitmap.current)bitmap.current=document.createElement('canvas');
      const showTools=local.tool==='protect'||local.tool==='warp'||isSpotRepair(local.tool)||local.tool==='restore';
      displayMask(context,bitmap.current,local.coverage,!originalHeal,showTools,drawing.current?.trace,showTools?'pink':local.displayMode);
      if(showTools)drawMarks(context,w,h,originalHeal?local.strokes.filter(s=>isSpotRepair(s.kind)):local.strokes);
    }
    if(local.tool==='warp'){
      context.save();context.strokeStyle='rgba(245,240,219,.24)';context.lineWidth=1;
      const cell=Math.min(w,h)/8;
      context.beginPath();for(let x=cell;x<w;x+=cell){context.moveTo(x,0);context.lineTo(x,h);}
      for(let y=cell;y<h;y+=cell){context.moveTo(0,y);context.lineTo(w,y);}context.stroke();context.restore();
    }
    if(drawing.current&&!drawing.current.trace)drawMarks(context,w,h,[drawing.current.stroke]);
  };
  useLayoutEffect(()=>{drawing.current=null;setCursor(null);},[photo.id,photo.revision,faceMode,local.tool,local.slot,local.ready,busy,local.edgeBrush.enabled]);
  useLayoutEffect(()=>{
    renderOverlay();
    return ()=>{cancelAnimationFrame(animation.current);animation.current=0;};
  },[local.coverage,local.visible,local.displayMode,local.tool,faceMode,originalHeal,canvasSize,busy,photo.revision]);
  const redraw=()=>{
    if(!animation.current)animation.current=requestAnimationFrame(()=>{animation.current=0;renderOverlay();});
  };
  const pointAt=(e:React.PointerEvent<HTMLDivElement>)=>{
    const r=e.currentTarget.getBoundingClientRect();
    return {x:Number(clamp((e.clientX-r.left)/r.width,0,1).toFixed(5)),y:Number(clamp((e.clientY-r.top)/r.height,0,1).toFixed(5))};
  };
  const track=(point:NormalizedPoint,force=false)=>{
    const job=drawing.current;if(!job)return;
    const list=job.stroke.points;
    if(isSpotRepair(job.stroke.kind))return;
    if(job.stroke.kind==='warp'){job.stroke.points=[list[0],point];return;}
    const last=list[list.length-1];
    if(Math.abs(point.x-last.x)+Math.abs(point.y-last.y)<(force ? .00001 : job.minDist))return;
    if(list.length>=job.limit && list.length>2){
      job.stroke.points=list.filter((_v,i)=>i===0||i%2===0);
      job.minDist*=1.7;
    }
    if(job.stroke.points.length<job.limit){
      job.stroke.points.push(point);
      if(job.trace&&local.coverage)addMaskSegment(local.coverage,job.trace.weights,job.stroke,last,point,job.trace.edge);
    }
  };
  const guided=local.edgeBrush.enabled&&(local.tool==='paint'||local.tool==='erase');
  const enabled=!faceMode&&local.ready&&!busy&&!local.conflict&&!local.gradient&&!local.range&&local.tool!=='hand'&&local.slot>=0&&(!local.category||!!local.category.error)&&(!guided||!!local.edgeBrush.guide);
  const diameter=2*local.radius*Math.min(photo.width||1,photo.height||1);
  return <div ref={holder} className={'local-overlay '+(enabled?'interactive':'')} aria-label={faceMode?'人脸检测框':'局部画笔画布'}
    onPointerDown={e=>{
      if(!enabled||e.button!==0||drawing.current)return;
      e.stopPropagation();e.preventDefault();e.currentTarget.setPointerCapture(e.pointerId);
      const point=pointAt(e),available=8192-local.strokes.reduce((n,s)=>n+s.points.length,0);
      const limit=Math.max(1,Math.min(2048,available));
      if(local.tool==='hand')return;
      local.resumePainting();
      const stroke:BrushStroke={kind:local.tool==='paint'?'maskPaint':local.tool==='erase'?'maskErase':local.tool==='restore'?'erase':local.tool,
        points:[point],radius:local.radius,strength:local.strength};
      const domain=guided&&local.edgeBrush.guide?seedEdge(local.edgeBrush.guide,point):undefined;
      if(domain===null){local.edgeBrush.setHint('此处完全透明，请从可见的颜色上起笔。');return;}
      const trace=isMaskStroke(stroke.kind)&&local.coverage?{kind:stroke.kind,weights:new Float32Array(local.coverage.paint.length),edge:domain?liveEdge(domain):undefined}:undefined;
      if(trace&&local.coverage)addMaskSegment(local.coverage,trace.weights,stroke,point,point,trace.edge);
      drawing.current={pointer:e.pointerId,minDist:.0015,limit,stroke,trace,domain};setCursor(point);redraw();
    }} onPointerMove={e=>{
      if(!enabled)return;
      e.stopPropagation();const point=pointAt(e);setCursor(point);
      if(drawing.current?.pointer===e.pointerId){track(point);redraw();}
    }} onPointerLeave={()=>{if(!drawing.current)setCursor(null);}}
    onPointerUp={e=>{
      if(!enabled||drawing.current?.pointer!==e.pointerId)return;
      e.stopPropagation();track(pointAt(e),true);const {stroke,domain}=drawing.current;drawing.current=null;redraw();
      if(domain){
        const mark=packEdge(domain,stroke);stroke.edge=mark.edge;
        local.edgeBrush.setHint(mark.weak?'这笔附近轮廓不明显，请检查覆盖范围。可关闭贴边继续画或擦除。':'');
      }
      if(stroke.kind==='sampleRepair'){
        if(local.sampleClick(stroke)&&originalHeal)local.setHealPreview(true);
      }else if(stroke.kind!=='warp'||(stroke.points.length===2&&Math.hypot(stroke.points[0].x-stroke.points[1].x,stroke.points[0].y-stroke.points[1].y)>.0005)){
        if(local.add(stroke)&&isSpotRepair(stroke.kind)&&originalHeal)local.setHealPreview(true);
      }
    }} onPointerCancel={e=>{e.stopPropagation();drawing.current=null;redraw();setCursor(null);}}
    onLostPointerCapture={e=>{if(drawing.current?.pointer===e.pointerId){drawing.current=null;setCursor(null);redraw();}}} onDoubleClick={e=>{if(enabled)e.stopPropagation();}}>
    <canvas ref={baseCanvas} width={canvasSize.w} height={canvasSize.h}/>
    {!faceMode&&local.garmentGuide&&<span className="garment-guide" role="img" aria-label="衣物分界预览" style={{
      left:local.garmentGuide.left*100+'%',top:local.garmentGuide.top*100+'%',width:local.garmentGuide.width*100+'%'}}/>}
    {!faceMode&&local.gradient&&<GradientHandles shape={local.gradient} disabled={busy||!local.ready} onChange={local.changeGradient}/>}
    {!faceMode&&local.range&&local.visible&&<div aria-label="原片范围取样" style={{position:'absolute',inset:0,pointerEvents:'auto',touchAction:'none',cursor:'crosshair'}}
      onPointerDown={e=>{if(e.button!==0||busy||!local.range?.token)return;e.preventDefault();e.stopPropagation();local.pickRange(pointAt(e));}}
      onDoubleClick={e=>e.stopPropagation()}/>}
    {enabled&&local.visible&&local.samplePoint&&<i className="brush-ring protect" role="img" aria-label="干净纹理取样点" style={{left:local.samplePoint.x*100+'%',top:local.samplePoint.y*100+'%',width:diameter/Math.max(1,photo.width)*100+'%',height:diameter/Math.max(1,photo.height)*100+'%'}}/>}
    {enabled&&cursor&&<i className={'brush-ring '+(local.tool==='sampleRepair'?(local.samplePoint?'heal':'protect'):local.tool)} style={{left:cursor.x*100+'%',top:cursor.y*100+'%',width:diameter/Math.max(1,photo.width)*100+'%',height:diameter/Math.max(1,photo.height)*100+'%'}}/>}
    {faceMode&&local.faces?.faces.map((f,i)=><span className={'face-box '+(local.faces?.selected===i?'selected':'')} key={i} style={{left:f.x*100+'%',top:f.y*100+'%',width:f.width*100+'%',height:f.height*100+'%'}}><small>人物 {i+1}</small></span>)}
  </div>;
}

export function LocalPanel({local,canBrush,canFace,canSegment,segment,photo,people,personScene,busy,warpedHeal,previewReady,replacingBackground,onSave,onFace,onManage,lightControls,portraitPanel,children}:{
  local:LocalEditor;canBrush:boolean;canFace:boolean;canSegment:boolean;photo:Photo|null;busy:boolean;segment:React.ReactNode;
  warpedHeal:boolean;previewReady:boolean;portraitPanel?:React.ReactNode;
  people:BodyPerson[];personScene:string;
  onSave:()=>void;onFace:(index:number)=>void;onManage:ManageMask;lightControls?:React.ReactNode;children:React.ReactNode;replacingBackground?:boolean;
}) {
  const section=localSection(local.section,canBrush,canFace,canSegment);
  const locked=busy||!photo;
  const lightTool=local.tool==='paint'||local.tool==='erase'||local.tool==='hand';
  const selectedKind=local.masks.find(mask=>mask.slot===local.slot)?.kind;
  const handDetail=selectedKind==='lens'||selectedKind==='fabric';
  const brushCount=lightTool?local.strokes.filter(s=>isPaint(s.kind)||isErase(s.kind)).length:local.strokes.length;
  const firstMaskStroke=lightTool?firstEditableMaskStroke(local.strokes):0;
  const canUndoStroke=lightTool?local.strokes.slice(firstMaskStroke).some(s=>isPaint(s.kind)||isErase(s.kind)):brushCount>0;
  const brushStatus=!local.ready?'读取中…':!brushCount?(local.savedGradient?'渐变已保存':local.alpha?'自动选区':'尚未绘制'):(local.dirty?'待保存':lightTool&&!local.hasPaint?'无覆盖':'已保存')+' · '+brushCount+' 笔';
  const diameter=Math.round(2*local.radius*Math.min(photo?.width||1,photo?.height||1));
  const toolButton=(m:typeof modes[number])=><button key={m.value} disabled={locked||!local.ready}
    aria-pressed={local.tool===m.value} className={local.tool===m.value?'selected':''} onClick={()=>local.setTool(m.value)}><Icon name={m.icon} size={15}/>{m.label}</button>;
  return <div className="local-panel"><div className="section-heading"><h2>{section==='brush'?'局部蒙版':'局部工具'}</h2></div>
    {[canBrush,canFace,canSegment].filter(Boolean).length>1&&<div className="channel-tabs local-tabs">
      {canBrush&&<button onClick={()=>local.setSection('brush')} className={section==='brush'?'active':''}>手绘与液化</button>}
      {canSegment&&<button onClick={()=>local.setSection('segment')} className={section==='segment'?'active':''}>前景与背景</button>}
      {canFace&&<button onClick={()=>local.setSection('face')} className={section==='face'?'active':''}>人像</button>}</div>}
    {section==='face'&&canBrush&&<button type="button" className="secondary small" disabled={locked} onClick={()=>{local.setSection('brush');local.setTool('redEye');}}>单眼红眼修正</button>}
    {section==='segment'?segment:section==='face'?portraitPanel??<section className="local-section"><b>人脸检测</b><p>在当前照片中定位人脸，再选择需要调整的人物。</p>
      <button className="secondary small" disabled={locked||local.detecting} onClick={()=>void local.analyze()}><Icon name="target" size={13}/>{local.detecting?'正在检测…':local.faces?'重新检测':'检测人脸'}</button>
      {local.faceError&&<p className="field-error" role="alert">{local.faceError}</p>}
      {local.faces&&(local.faces.faces.length?<div className="face-choices">{local.faces.faces.map((_f,i)=><button key={i} className={'secondary small '+(local.faces?.selected===i?'selected':'')} disabled={locked} onClick={()=>onFace(i)}>人物 {i+1}{local.faces?.selected===i&&<Icon name="check" size={12}/>}</button>)}</div>:<p>未检测到可选人脸。</p>)}
    </section>:canBrush&&<section className="local-section">
      {lightTool&&<><div className="mask-display-controls">
        <label><input type="checkbox" checked={local.visible} onChange={e=>local.setVisible(e.target.checked)}/>显示蒙版</label>
        <label><i aria-hidden="true" className={'local-mask-swatch '+local.displayMode}/>显示方式
          <select aria-label="蒙版显示方式" value={local.displayMode} onChange={e=>local.setDisplayMode(e.target.value as MaskDisplay)}>
            <option value="pink">粉色</option><option value="cyan">青色</option><option value="bw">黑白</option>
          </select></label></div>
        {local.visible&&local.displayMode==='bw'&&<p>白色为完全选中，灰色为部分选中，黑色为未选中。</p>}
        <button type="button" className="secondary small mask-bypass" disabled={locked||!local.canBypass}
          aria-label="按住暂时关闭本层蒙版" aria-pressed={local.bypassSlot!==undefined} title="按住鼠标或空格键查看，松开恢复"
          onPointerDown={e=>{if(e.button===0&&e.isPrimary){e.currentTarget.setPointerCapture(e.pointerId);local.holdMask();}}}
          onPointerUp={local.releaseMask} onPointerCancel={local.releaseMask} onLostPointerCapture={local.releaseMask} onBlur={local.releaseMask}
          onKeyDown={e=>{if(e.key===' '||e.key==='Enter'){e.preventDefault();e.stopPropagation();if(!e.repeat)local.holdMask();}}}
          onKeyUp={e=>{if(e.key===' '||e.key==='Enter'){e.preventDefault();e.stopPropagation();local.releaseMask();}}}>
          {local.bypassSlot===undefined?'按住暂时关闭本层蒙版':'松开恢复本层蒙版'}</button>
        <MaskList key={personScene} local={local} people={people} busy={locked} manage={onManage} previewReady={previewReady} replacingBackground={replacingBackground}/></>}
      {!local.gradient&&!local.range&&<><div className="brush-modes local-mask-modes">{modes.filter(m=>m.value==='paint'||m.value==='erase'||m.value==='hand').map(toolButton)}</div>
      <p>{modes.find(m=>m.value===local.tool)?.hint}</p>
      {(local.tool==='paint'||local.tool==='erase')&&<><label><input type="checkbox" disabled={locked} checked={local.edgeBrush.enabled}
        onChange={e=>local.edgeBrush.setEnabled(e.target.checked)}/>画笔自动贴边</label>
        {local.edgeBrush.enabled&&<p role="status">{local.edgeBrush.error||local.edgeBrush.hint||(local.edgeBrush.pending?'正在读取原片轮廓…':'从想要保留或擦除的一侧起笔，沿轮廓移动。')}</p>}</>}
      {local.tool==='sampleRepair'&&(!warpedHeal||!local.healPreview)&&<div className="sample-repair-step" role="status">
        <b>{local.samplePoint?'② 点一下要修的污点':'① 点一处干净纹理'}</b>
        {local.samplePoint&&<button className="text-button" disabled={locked} onClick={local.clearSample}>重新选取样点</button>}
      </div>}
      {warpedHeal&&<><div className="channel-tabs heal-views"><button className={!local.healPreview?'active':''} disabled={locked} onClick={()=>local.setHealPreview(false)}>原片定位</button>
        <button className={local.healPreview?'active':''} disabled={locked} onClick={()=>{local.clearSample();local.setHealPreview(true);}}>效果预览</button></div>
        <p>{local.tool==='redEye'?(local.healPreview?'检查瞳孔和眼神光。要修另一只眼可切回原片，满意后保存笔迹。':'点一只红瞳的中心。若红色未变，可放大并调小定位圈重新点选。'):local.healPreview?'正在显示应用修复和液化后的照片。可切回原片继续点选。':'照片已有液化。请在原片定位，完成一笔后查看修复效果。'}</p></>}
      {local.tool!=='hand'&&<div className="brush-settings local-brush-settings">
        <label>{lightTool?'画笔大小':'画笔半径'} <span>{lightTool?diameter+' px':Number((local.radius*100).toFixed(1))+'%'}</span><input aria-label={lightTool?'画笔大小':'画笔半径'} type="range" min={local.tool==='redEye'?'.001':'.005'} max={local.tool==='redEye'?'.08':'.45'} step={local.tool==='redEye'?'.001':'.005'} value={local.radius} disabled={locked} onChange={e=>local.setRadius(Number(e.target.value))}/></label>
        <label>画笔强度 <span>{Math.round(local.strength*100)}%</span><input aria-label="画笔强度" type="range" min="0" max="1" step=".01" value={local.strength} disabled={locked} onChange={e=>local.setStrength(Number(e.target.value))}/></label>
      </div>}
      <div className="markings-row">{!lightTool&&<label><input type="checkbox" checked={local.visible} onChange={e=>local.setVisible(e.target.checked)}/>显示标记</label>}<span>{brushStatus}</span></div>
      {local.loadError&&<p className="field-error" role="alert">{local.loadError} <button className="text-button" onClick={local.retry}>重试</button></p>}
      {local.conflict&&<p className="field-error">已保存的笔迹发生变化。可取消草稿，或<button className="text-button" onClick={local.reconcile}>按草稿重新预览</button>。</p>}
      <div className="local-actions"><IconButton icon="undo" label={lightTool?'撤回最后一笔蒙版':'撤回最后一笔'} disabled={locked||!canUndoStroke||local.conflict} onClick={()=>local.undo(lightTool)}/>
        <IconButton icon="trash" label={lightTool?(firstMaskStroke?'清除修形后的蒙版改动':'预览清空手绘蒙版'):'预览清空全部笔迹'} disabled={locked||!canUndoStroke||local.conflict} onClick={()=>local.clear(lightTool)}/>
        <span className="spacer"/><button className="secondary small" disabled={locked||!local.dirty} onClick={local.discard}>取消</button><button className="primary small" disabled={locked||!local.dirty||local.conflict||!previewReady} onClick={onSave}>保存笔迹</button></div>
      {lightTool&&lightControls&&<div className="local-light-controls"><h3>{selectedKind==='lens'?'镜片反光':selectedKind==='fabric'?'衣料去褶':'局部光影与颜色'}</h3>
        <p>{!local.hasPaint?handDetail?'涂出要调整的位置后可预览强度。':'涂出区域后可以单独调节光影、冷暖和饱和度。':local.dirty?'拖动滑块看效果，松开会保存本次笔迹和调整。':'拖动滑块看效果，继续画可修改当前蒙版。'}</p>
        <fieldset aria-label="局部光影" disabled={locked||!local.hasPaint||local.conflict||!!local.category?.alpha}
          onInputCapture={local.inspectLight} onPointerDownCapture={e=>{if(e.target instanceof HTMLInputElement)local.inspectLight();}}>{lightControls}</fieldset></div>}
      <div className="local-extra-tools"><h3>保护与修复</h3><div className="brush-modes">{modes.filter(m=>m.value==='protect'||m.value==='warp'||isSpotRepair(m.value)||m.value==='restore').map(toolButton)}</div></div></>}
    </section>}
    {children}
  </div>;
}
