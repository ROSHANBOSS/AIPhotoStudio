import React,{useEffect,useLayoutEffect,useRef,useState} from 'react';
import {Photo,Preview,clamp,invoke,messageOf} from './model';
import {Evidence,EyeManual,EyeState,ReviewFace,faceState} from './set-review-data';

const labels:Record<EyeState,string>={suspect:'疑似闭眼',unknown:'待判',clear:'未见闭眼'};
export type EyeSelection={id:string;key:string;eye:number};

export function ExpressionReference({photo,source,face}:{photo:Photo;source:string;face:ReviewFace}){
  const [opened,setOpened]=useState(false),[retry,setRetry]=useState(0);
  const [result,setResult]=useState<{image?:string;region?:number[];error?:string}>({});
  useEffect(()=>{
    if(!opened||result.image)return;
    let live=true;
    void invoke<Preview&{region:number[]}>('setReviewExpression',{id:photo.id,revision:photo.revision??0,source,key:face.key})
      .then(frame=>{if(live)setResult({image:frame.image,region:frame.region});})
      .catch(error=>{if(live)setResult({error:messageOf(error)});});
    return ()=>{live=false;};
  },[opened,result.image,photo.id,photo.revision,source,face.key,retry]);
  return <div><button className="text-button" aria-expanded={opened} onClick={()=>setOpened(v=>!v)}>{opened?'收起整脸表情':'看整脸表情'}</button>
    {opened&&(result.image?<img alt="所选人物的整脸表情" data-region={result.region?.join(',')} src={result.image}
      style={{display:'block',maxWidth:'100%',maxHeight:220,width:'auto',height:'auto'}}/>:
      result.error?<div><small role="alert">{result.error}</small> <button className="text-button" onClick={()=>{setResult({});setRetry(n=>n+1);}}>重读整脸</button></div>:
      <small role="status">正在读取整脸…</small>)}</div>;
}

export function EyePixels({photo,source,face,eye,onReady,adjacent=false}:{photo:Photo;source:string;face:ReviewFace;eye:number;onReady?:(ready:boolean)=>void;adjacent?:boolean}){
  const host=useRef<HTMLDivElement>(null);
  const [view,setView]=useState({w:0,density:1});
  const [result,setResult]=useState({identity:'',image:'',error:''});
  const [retry,setRetry]=useState(0);
  useLayoutEffect(()=>{
    if(!host.current)return;
    const update=()=>setView({w:host.current!.clientWidth,density:window.devicePixelRatio||1});
    update();
    const observer=new ResizeObserver(update);
    observer.observe(host.current);window.addEventListener('resize',update);
    return ()=>{observer.disconnect();window.removeEventListener('resize',update);};
  },[]);
  const width=Math.min(photo.width,Math.max(1,Math.floor(view.w*view.density)));
  const height=Math.min(photo.height,Math.max(1,Math.floor(144*view.density)));
  const x=clamp(Math.round(face.centers[2*eye]*photo.width-width/2),0,photo.width-width);
  const y=clamp(Math.round(face.centers[2*eye+1]*photo.height-height/2),0,photo.height-height);
  const identity=JSON.stringify([photo.id,photo.revision,source,face.key,eye,x,y,width,height]);
  useEffect(()=>{
    if(!view.w)return;
    let live=true;
    void invoke<Preview>('viewport',{id:photo.id,revision:photo.revision??0,original:true,sourceView:true,reviewSource:source,
      ...(adjacent?{reviewPanel:'adjacent'}:{}),x,y,width,height})
      .then(async image=>{
        if(image.width!==width||image.height!==height||!image.image.startsWith('data:image/png;'))throw Error('读取的原片区域尺寸不符。');
        const frame=new Image();frame.src=image.image;await frame.decode();
        if(live)setResult({identity,image:image.image,error:''});
      }).catch(error=>{if(live)setResult({identity,image:'',error:messageOf(error)});});
    return ()=>{live=false;};
  },[identity,!!view.w,adjacent,retry]);
  const visible=result.identity===identity?result:null;
  useEffect(()=>{onReady?.(!!visible?.image);},[identity,visible?.image,onReady]);
  const span=Math.hypot((face.centers[0]-face.centers[2])*photo.width,(face.centers[1]-face.centers[3])*photo.height);
  const rx=Math.max(10,.34*span),ry=Math.max(7,.16*span);
  return <div ref={host} className="set-eye-pixels" style={{width:'min(360px,100%)',minHeight:Math.min(144,height/view.density),background:'#171819',display:'grid',placeItems:'center',overflow:'hidden'}}>
    {visible?.image?<div style={{position:'relative',overflow:'hidden'}}>
      <img src={visible.image} alt="眼部原片像素" data-region={[x,y,width,height].join(',')}
        style={{width:width/view.density,height:height/view.density,maxWidth:'none',display:'block'}}/>
      <span aria-hidden style={{position:'absolute',pointerEvents:'none',border:'1px solid #f6ca81',boxShadow:'0 0 0 1px #33271acc',borderRadius:'50%',
        left:(face.centers[2*eye]*photo.width-x-rx)/view.density,top:(face.centers[2*eye+1]*photo.height-y-ry)/view.density,
        width:2*rx/view.density,height:2*ry/view.density}}/>
      </div>:
      visible?.error?<div><small role="alert">{visible.error}</small> <button className="text-button" onClick={()=>{
        setResult({identity:'',image:'',error:''});setRetry(v=>v+1);}}>重读眼部原像素</button></div>:<small role="status">正在读取原片像素…</small>}
  </div>;
}

export function EyeReview({photo,evidence,level,selection,onSelect,onSave,onSeries,disabled}:{
  photo:Photo;evidence:Evidence;level:number;selection:EyeSelection|null;onSelect:(s:EyeSelection|null)=>void;
  onSave:(photo:Photo,e:Evidence,f:ReviewFace,v:EyeManual)=>void;onSeries?:(face:ReviewFace)=>void;disabled:boolean;
}){
  const faces=evidence.faces||[];
  const selected=selection?.id===photo.id?faces.findIndex(f=>f.key===selection.key):-1;
  const region=useRef<HTMLDivElement>(null);
  useEffect(()=>{if(selected>=0)region.current?.scrollIntoView({block:'nearest'});},[photo.id,selected]);
  if(!faces.length)return null;
  return <section aria-label="逐脸眼睛复核">
    <div className="set-tools">
      {faces.map((f,i)=><button key={f.key} className={'secondary small '+(i===selected?'selected':'')}
        aria-pressed={selected===i} onClick={()=>onSelect({id:photo.id,key:f.key,eye:f.closerEye})}>
        人脸 {i+1} · {f.manual&&f.manual!=='auto'?'人工'+({flag:'闭眼',clear:'睁眼',unknown:'待判'} as const)[f.manual]:labels[faceState(f,level)]} · 100%
      </button>)}
    </div>
    {selected>=0&&selection&&<div ref={region}>
      <div className="set-tools"><strong>人脸 {selected+1} 原像素</strong>
        {[0,1].map((n)=><button className={'secondary small '+(selection.eye===n?'selected':'')} key={n}
          aria-pressed={selection.eye===n} onClick={()=>onSelect({...selection,eye:n})}>{n===0?'图中左眼':'图中右眼'}</button>)}
        <label>这张脸 <select aria-label="这张脸的眼睛" value={faces[selected].manual||'auto'} disabled={disabled}
          onChange={ev=>onSave(photo,evidence,faces[selected],ev.target.value as EyeManual)}>
          <option value="auto">自动 · {labels[faces[selected].eyes[level]]}</option><option value="flag">人工标为闭眼</option>
          <option value="clear">人工确认睁眼</option><option value="unknown">人工留待判断</option></select></label>
        <button className="text-button" onClick={()=>onSelect(null)}>收起眼部细节</button>
        {onSeries&&<button className="secondary small" disabled={disabled} onClick={()=>onSeries(faces[selected])}>逐张复核同组位置</button>}
      </div>
      <EyePixels photo={photo} source={evidence.source} face={faces[selected]} eye={selection.eye}/>
      <ExpressionReference key={[photo.id,photo.revision,evidence.source,evidence.frame,faces[selected].key].join('/')} photo={photo} source={evidence.source} face={faces[selected]}/>
      {faces[selected].colorCover&&<p className="dialog-note">双眼上下都有大面积色彩覆盖，请查看原片判断眼睑。</p>}
      <small className="dialog-note">分析图中较窄的一眼宽约 {Math.round(faces[selected].minEyePixels)} 像素。</small>
    </div>}
  </section>;
}
