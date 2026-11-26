import React, {useEffect, useLayoutEffect, useRef, useState} from 'react';
import {Args, BrushStroke, Photo, Preview, Values, clamp, formatOf, invoke, messageOf} from './model';
import {Icon} from './icons';
import {CameraPoster,DetailLayer,ViewNavigator} from './detail-view';
import type {GradientPreview} from './gradient';
import type {RangePreview} from './range-mask';
import type {AreaDraft} from './area-tools';

type Frame = {id: string; key: string; space: string; sequence: number; value: Preview};
type PreviewJob = {id: string; key: string; space: string; family: string; sequence: number; args: Args; method: string; interactive: boolean; canonical:boolean};
type Lane = {running:boolean; pending:PreviewJob|null; lastSent:number; timer?:number};

export function usePreview(photoId: string | null, revision: number, values?: Values, original = false, maxEdge = 1400, strokes?:BrushStroke[], gesture?:number, settling=false,maskSlot=0,frameView:'source'|'output'|'default'='default',layoutKey='',gradient?:GradientPreview|RangePreview,clipping=0,bypassMaskSlot?:number,areas?:AreaDraft) {
  const [frame, setFrame] = useState<Frame | null>(null);
  const [failure, setFailure] = useState<{key: string; text: string} | null>(null);
  const [attempt, setAttempt] = useState(0);
  const geometry=useRef({strokes, serial:0});
  if(geometry.current.strokes!==strokes)geometry.current={strokes,serial:geometry.current.serial+1};
  const space=frameView+'/'+layoutKey;
  const family=JSON.stringify([photoId,revision,maxEdge,original,geometry.current.serial,strokes||gradient?maskSlot:null,attempt,gesture??null,settling,Object.keys(values||{}).sort(),space,gradient,clipping,bypassMaskSlot,areas]);
  const key=family+JSON.stringify(values || null);
  const desired=useRef({key,family});
  desired.current={key,family};
  const lanes=useRef<{drag:Lane; exact:Lane}>({
    drag:{running:false,pending:null,lastSent:-Infinity},exact:{running:false,pending:null,lastSent:-Infinity}
  });
  const sequence=useRef(0);
  const canonical=useRef<Frame|null>(null);
  const mounted = useRef(true);
  useEffect(() => {
    mounted.current=true;
    return () => {
      mounted.current=false;
      for(const lane of Object.values(lanes.current)){lane.pending=null;window.clearTimeout(lane.timer);}
    };
  }, []);
  useEffect(() => {
    if(!photoId||settling)return;
    const interactive=gesture!==undefined;
    // A drag samples the same native recipe at a smaller edge. Fit, zoom, proposals,
    // restored history and the settled photo always use the requested full-size path.
    const edge=interactive?Math.min(maxEdge,320):maxEdge;
    const args:Args={id:photoId,revision,maxEdge:edge};
    if(!original&&clipping)args.clipping=clipping;
    if(bypassMaskSlot!==undefined)args.bypassMaskSlot=bypassMaskSlot;
    if(frameView!=='default')args.frameView=frameView;
    if(interactive)args.profile='interactive';
    if(areas)Object.assign(args,areas);
    else if(strokes!==undefined||gradient){
      if(strokes!==undefined)args.strokes=strokes;
      if(gradient)args['range' in gradient?'rangeMask':'gradient']=gradient;
      args.maskSlot=maskSlot;
    }else args.original=original;
    if (values && Object.keys(values).length) args.values = values;
    const job:PreviewJob={id:photoId,key,space,family,args,sequence:++sequence.current,method:strokes!==undefined||gradient||areas?'localPreview':'preview',
      interactive,canonical:!interactive&&!values&&strokes===undefined&&!gradient&&!areas&&bypassMaskSlot===undefined};
    const lane=interactive?lanes.current.drag:lanes.current.exact;
    // Each lane owns one native request and only the latest pending input. Separating
    // the two lets a final render start as soon as the committed revision arrives.
    function pump(){
      if(lane.running||!mounted.current||!lane.pending)return;
      if(lane.pending.key!==desired.current.key){lane.pending=null;return;}
      const delay=lane.pending.interactive?16-(performance.now()-lane.lastSent):0;
      if(delay>0){
        if(lane.timer===undefined)lane.timer=window.setTimeout(()=>{lane.timer=undefined;pump();},delay);
        return;
      }
      const next=lane.pending;
      lane.pending=null; lane.running=true; lane.lastSent=performance.now();
      void invoke<Preview>(next.method,next.args).then(result=>{
        if(typeof result?.image!=='string'||!/^data:image\/(?:jpeg|jpg|png|webp);base64,/i.test(result.image))throw Error('未能读取这张照片的预览。');
        if(!mounted.current || (desired.current.key!==next.key && !(next.interactive&&desired.current.family===next.family)))return;
        const delivered={id:next.id,key:next.key,space:next.space,sequence:next.sequence,value:result};
        if(next.canonical)canonical.current=delivered;
        setFrame(previous=>!previous||previous.sequence<delivered.sequence?delivered:previous);
        setFailure(null);
      }).catch(error=>{
        if(mounted.current&&desired.current.key===next.key)setFailure({key:next.key,text:messageOf(error)});
      }).finally(()=>{lane.running=false;pump();});
    }
    const submit=()=>{lane.pending=job;pump();};
    // Deferred previews for full-resolution proposal/brush edits coalesce separately.
    if(!interactive&&(values||strokes||gradient||areas)){
      const timer=window.setTimeout(submit,45); return ()=>window.clearTimeout(timer);
    }
    submit();
  }, [key]);
  const saved=canonical.current;
  const displayed=frame?.id===photoId&&frame?.space===space&&(!areas||frame.key===key) ? frame : null;
  const current=!settling&&!values&&strokes===undefined&&!gradient&&!areas&&saved?.key===key?saved:displayed;
  return {
    image: current?.value || null, clipping, bypassMaskSlot,
    // A committed native tile carries its own revision. Request it alongside the overview when a canvas is visible.
    revision,detailReady:!!current&&!settling&&!values&&strokes===undefined&&!gradient&&!areas&&gesture===undefined&&failure?.key!==key,
    ready: !!photoId && !settling && gesture===undefined && current?.key === key,
    error: failure?.key === key ? failure.text : null,
    retry: () => setAttempt(v => v + 1),
  };
}

const thumbs = new Map<string, string>();
const thumbRequests = new Map<string, Promise<string>>();
const thumbQueue: (() => void)[] = [];
let thumbActive = 0;
function thumbnail(photo: Photo): Promise<string> {
  const key = photo.id + '@' + photo.path + '@' + (photo.selectedRawFrame??0)+'@'+(photo.hasEdits===undefined?'legacy':photo.revision);
  const saved = thumbs.get(key);
  if (saved) return Promise.resolve(saved);
  const pending = thumbRequests.get(key);
  if (pending) return pending;
  let run: () => void;
  const request = new Promise<string>((resolve, reject) => {
    run = () => {
      thumbActive++;
      invoke<Preview>(photo.hasEdits===undefined?'preview':'libraryThumbnail', {id: photo.id, revision: photo.revision ?? 0, original: true, maxEdge: 144})
        .then(result => {
          if (!result?.image?.startsWith('data:image/')) throw new Error('无缩略图');
          if (thumbs.size >= 180) thumbs.delete(thumbs.keys().next().value!);
          thumbs.set(key, result.image);
          resolve(result.image);
        }).catch(reject).finally(() => {
          thumbRequests.delete(key);
          thumbActive--;
          thumbQueue.shift()?.();
        });
    };
  });
  thumbRequests.set(key, request);
  if (thumbActive < 2) run!();
  else thumbQueue.push(run!);
  return request;
}
export function Thumb({photo}: {photo: Photo}) {
  const holder = useRef<HTMLSpanElement>(null);
  const [url, setUrl] = useState('');
  useEffect(() => {
    let live = true;
    setUrl('');
    const load = () => { thumbnail(photo).then(src => { if (live) setUrl(src); }).catch(() => {}); };
    let observer: IntersectionObserver | undefined;
    if (holder.current && 'IntersectionObserver' in window) {
      observer = new IntersectionObserver(entries => {
        if (entries.some(entry => entry.isIntersecting)) { observer?.disconnect(); load(); }
      }, {rootMargin: '90px'});
      observer.observe(holder.current);
    } else load();
    return () => { live = false; observer?.disconnect(); };
  }, [photo.id, photo.path,photo.selectedRawFrame,photo.revision]);
  return <span className="thumb" ref={holder}>
    {url ? <img alt="" src={url} draggable={false}/> : <span className="thumb-placeholder"><Icon name="photo"/><small>{formatOf(photo)}</small></span>}
  </span>;
}

export type ViewCommand = {action: 'fit' | 'actual' | 'in' | 'out'; serial: number};
type Point = {x: number; y: number};
type CanvasProps = {
  photo: Photo; edited: ReturnType<typeof usePreview>; original: ReturnType<typeof usePreview>;
  compare: boolean; peek: boolean; command: ViewCommand; onZoom: (percent: number) => void;
  transient: string; picker?: (x: number, y: number) => void; pickerOverlay?:React.ReactNode; pickerImage?:Preview|null;
  overlay?:React.ReactNode; neutral?:boolean;detailSourceView?:boolean;detailEnabled?:boolean;cameraPoster?:boolean;
};
export function PhotoCanvas({photo, edited, original, compare, peek, command, onZoom, transient, picker, pickerOverlay,pickerImage,overlay,neutral=false,detailSourceView=false,detailEnabled=false,cameraPoster=false}: CanvasProps) {
  const stage = useRef<HTMLDivElement>(null);
  const canvas = useRef<HTMLDivElement>(null);
  const drag = useRef<{origin: Point; initial: Point; pointer: number} | null>(null);
  const wipe = useRef(false);
  const [size, setSize] = useState({w: 0, h: 0});
  const [factor, setFactor] = useState(1);
  const [pan, setPan] = useState({x: 0, y: 0});
  const [split, setSplit] = useState(50);
  const [dragging, setDragging] = useState(false);
  const [detailPhase,setDetailPhase]=useState<'loading'|'ready'|'error'>('loading');
  const width = Math.max(1, photo.width || edited.image?.width || 1);
  const height = Math.max(1, photo.height || edited.image?.height || 1);
  const margin = size.w < 550 ? 34 : 62;
  const fit = Math.min(Math.max(1, size.w - margin) / width, Math.max(1, size.h - 46) / height, 1);
  const scale = fit * factor;
  const density=window.devicePixelRatio||1;
  const imageW = width * scale, imageH = height * scale;
  useLayoutEffect(() => {
    if (!stage.current) return;
    const update = () => { const r = stage.current!.getBoundingClientRect(); setSize({w: r.width, h: r.height}); };
    const observer = new ResizeObserver(update);
    observer.observe(stage.current); update();
    return () => observer.disconnect();
  }, []);
  useEffect(() => { if(size.w>0 && size.h>0)onZoom(Math.round(scale*density*100)); }, [scale,density,onZoom,size.w,size.h]);
  const boundedPan = (point: Point, zoom: number): Point => ({
    x: clamp(point.x, -Math.max(0, (width * fit * zoom - size.w + 100) / 2), Math.max(0, (width * fit * zoom - size.w + 100) / 2)),
    y: clamp(point.y, -Math.max(0, (height * fit * zoom - size.h + 100) / 2), Math.max(0, (height * fit * zoom - size.h + 100) / 2)),
  });
  const zoom = (target: number, anchor = {x: 0, y: 0}) => {
    const next = clamp(target, Math.min(1, .05 / fit), Math.max(1, 8 / fit));
    const ratio = next / factor;
    setFactor(next);
    setPan(boundedPan({x: anchor.x - (anchor.x - pan.x) * ratio, y: anchor.y - (anchor.y - pan.y) * ratio}, next));
  };
  useEffect(() => {
    if (command.action === 'fit') { setPan({x: 0, y: 0}); setFactor(1); }
    else if (command.action === 'actual') zoom(1 / (fit*density));
    else zoom(factor * (command.action === 'in' ? 1.25 : .8));
  }, [command.serial]);
  useEffect(() => {
    const el = stage.current;
    if (!el) return;
    const wheel = (ev: WheelEvent) => {
      if((ev.target as Element)?.closest('.viewport-navigator'))return;
      ev.preventDefault();
      const r = el.getBoundingClientRect();
      zoom(factor * Math.exp(-clamp(ev.deltaY, -200, 200) * .002), {x: ev.clientX - r.left - r.width / 2, y: ev.clientY - r.top - r.height / 2});
    };
    el.addEventListener('wheel', wheel, {passive: false});
    return () => el.removeEventListener('wheel', wheel);
  }, [factor, pan, fit, size]);
  const changeSplit = (clientX: number) => {
    const r = canvas.current?.getBoundingClientRect();
    if (r) setSplit(clamp((clientX - r.left) * 100 / r.width, 0, 100));
  };
  const source = pickerImage!==undefined?pickerImage:neutral ? (original.ready?original.image:null) : peek && original.ready ? original.image : edited.image;
  const activeFrame=neutral?original:edited;
  const detail=detailEnabled&&pickerImage===undefined&&scale*density>=.995&&Math.max(width,height)>2048&&size.w>0&&size.h>0;
  const detailProps={id:photo.id,revision:edited.revision,width,height,scale,pan,stage:size,sourceView:detailSourceView};
  return <div className={'photo-stage' + (dragging ? ' dragging' : '') + (picker ? ' picking' : '')} ref={stage}
    onPointerDown={e => {
      if (e.button !== 0 || !source || picker) return;
      e.currentTarget.setPointerCapture(e.pointerId);
      drag.current = {origin: {x: e.clientX, y: e.clientY}, initial: pan, pointer: e.pointerId}; setDragging(true);
    }}
    onPointerMove={e => {
      const start = drag.current;
      if (start && start.pointer === e.pointerId) setPan(boundedPan({x: start.initial.x + e.clientX - start.origin.x, y: start.initial.y + e.clientY - start.origin.y}, factor));
    }}
    onPointerUp={() => { drag.current = null; setDragging(false); }}
    onPointerCancel={() => { drag.current = null; setDragging(false); }}
    onDoubleClick={() => { if (picker) return; if (Math.abs(factor - 1) < .001) zoom(1 / (fit*density)); else {setFactor(1); setPan({x: 0, y: 0});} }}>
    {source && <div ref={canvas} className="image-plane" style={{width: imageW, height: imageH, left: 'calc(50% + ' + pan.x + 'px)', top: 'calc(50% + ' + pan.y + 'px)'}}
      onClick={e => { if (!picker) return; const r = e.currentTarget.getBoundingClientRect(); picker(clamp((e.clientX-r.left)/r.width,0,1), clamp((e.clientY-r.top)/r.height,0,1)); }}>
      <img className="main-image" src={source.image} alt={photo.name} draggable={false}/>
      {detail&&(neutral||peek?original.detailReady:edited.detailReady)&&<DetailLayer {...detailProps} original={neutral||peek} clipping={neutral||peek?0:edited.clipping} bypassMaskSlot={neutral||peek?undefined:edited.bypassMaskSlot} onPhase={setDetailPhase}/>}
      {!compare&&!peek&&!picker&&overlay}
      {!compare&&!peek&&picker&&pickerOverlay}
      {compare && !peek && original.ready && original.image && <>
        <img className="before-image" src={original.image.image} alt="原片" draggable={false} style={{clipPath: 'inset(0 ' + (100-split) + '% 0 0)'}}/>
        {detail&&original.detailReady&&<DetailLayer {...detailProps} original clip={split}/>}
        <span className="compare-tag before-tag">原片</span><span className="compare-tag after-tag">调整后</span>
        <div className="wipe-line" style={{left: split + '%'}}><button type="button" className="wipe-grip" aria-label="移动前后对比分界线" title="拖动或用左右方向键移动"
          role="slider" aria-valuemin={0} aria-valuemax={100} aria-valuenow={Math.round(split)}
          onPointerDown={e => { e.stopPropagation(); e.currentTarget.setPointerCapture(e.pointerId); wipe.current = true; }}
          onPointerMove={e => { if (wipe.current) {e.stopPropagation(); changeSplit(e.clientX);} }}
          onPointerUp={e => { e.stopPropagation(); wipe.current = false; }} onPointerCancel={() => {wipe.current=false;}}
          onKeyDown={e => { if (['ArrowLeft','ArrowRight','Home','End'].includes(e.key)) {e.preventDefault(); e.stopPropagation(); setSplit(s => e.key === 'Home' ? 0 : e.key === 'End' ? 100 : clamp(s + (e.key === 'ArrowLeft' ? -2 : 2), 0, 100));} }}>
          <Icon name="left" size={11}/><Icon name="right" size={11}/></button></div>
      </>}
    </div>}
    {source&&size.w>0&&size.h>0&&(imageW>size.w+1||imageH>size.h+1)&&<ViewNavigator image={source.image} width={imageW} height={imageH} stage={size} pan={pan}
      move={(x,y)=>setPan(boundedPan({x:(.5-x)*imageW,y:(.5-y)*imageH},factor))}/>}
    {detail&&source&&<span className="detail-note">{!(neutral||peek?original.detailReady:edited.detailReady)?'快速预览':detailPhase==='ready'?'原像素细节':detailPhase==='error'?'原像素读取失败':'正在加载原像素细节…'}</span>}
    {(!source || activeFrame.error) && (cameraPoster&&!activeFrame.error&&!neutral&&!peek&&/\.(nef|nrw|cr2|cr3|crw|arw|sr2|srf|raf|rw2|orf|dng)$/i.test(photo.name)?
      <CameraPoster id={photo.id} revision={edited.revision}/>:
      <div className="canvas-message" role="status">{activeFrame.error || (neutral?'正在读取取样用原片…':'正在读取照片…')}{activeFrame.error && <button className="text-button" onClick={activeFrame.retry}>重新读取</button>}</div>)}
    {(compare || peek) && original.error && <div className="canvas-message">{original.error}<button className="text-button" onClick={original.retry}>重读原片</button></div>}
    {(transient || peek || (compare && !original.ready && !original.error)) && <span className="state-pill">{peek ? (original.ready ? '原片' : '正在读取原片…') : transient || '正在读取原片…'}</span>}
  </div>;
}
