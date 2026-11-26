import React,{useEffect,useRef,useState} from 'react';
import {clamp,invoke,Preview,messageOf} from './model';

type Box={x:number;y:number;width:number;height:number};
type Tile=Box&{image:string};

export function ViewNavigator({image,width,height,stage,pan,move}:{image:string;width:number;height:number;
  stage:{w:number;h:number};pan:{x:number;y:number};move:(x:number,y:number)=>void}){
  const map=useRef<HTMLSpanElement>(null),pointer=useRef<number|null>(null);
  const x=.5-pan.x/width,y=.5-pan.y/height;
  const left=clamp(x-stage.w/(2*width),0,1),right=clamp(x+stage.w/(2*width),0,1);
  const top=clamp(y-stage.h/(2*height),0,1),bottom=clamp(y+stage.h/(2*height),0,1);
  const scale=Math.min(136/width,90/height);
  function point(e:React.PointerEvent<HTMLButtonElement>){
    const r=map.current?.getBoundingClientRect();if(!r||!r.width||!r.height)return;
    move(clamp((e.clientX-r.left)/r.width,0,1),clamp((e.clientY-r.top)/r.height,0,1));
  }
  return <button type="button" className="viewport-navigator" aria-label="全图位置导航" title="点击或拖动跳转 · 方向键移动 · Home 居中"
    onPointerDown={e=>{e.stopPropagation();if(e.button!==0)return;pointer.current=e.pointerId;e.currentTarget.setPointerCapture(e.pointerId);point(e);}}
    onPointerMove={e=>{if(pointer.current===e.pointerId){e.stopPropagation();point(e);}}}
    onPointerUp={e=>{e.stopPropagation();pointer.current=null;}} onPointerCancel={()=>{pointer.current=null;}}
    onDoubleClick={e=>e.stopPropagation()} onKeyDown={e=>{
      const dx=e.key==='ArrowLeft'?-1:e.key==='ArrowRight'?1:0,dy=e.key==='ArrowUp'?-1:e.key==='ArrowDown'?1:0;
      if(!dx&&!dy&&e.key!=='Home'&&e.key!=='Enter'&&e.key!==' ')return;
      e.stopPropagation();e.preventDefault();
      if(!dx&&!dy)move(.5,.5);else move(clamp(x+dx*(right-left)/2,0,1),clamp(y+dy*(bottom-top)/2,0,1));
    }}>
    <span ref={map} className="view-map-image" style={{width:Math.max(1,width*scale),height:Math.max(1,height*scale)}}>
      <img alt="" draggable={false} src={image}/>
      <span className="view-map-window" style={{left:left*100+'%',top:top*100+'%',width:(right-left)*100+'%',height:(bottom-top)*100+'%'}}/>
    </span>
  </button>;
}
type Props={id:string;revision:number;width:number;height:number;scale:number;pan:{x:number;y:number};
  stage:{w:number;h:number};original:boolean;sourceView:boolean;clip?:number;clipping?:number;bypassMaskSlot?:number;onPhase?:(phase:'loading'|'ready'|'error')=>void};

export function CameraPoster({id,revision}:{id:string;revision:number}){
  const [image,setImage]=useState('');
  useEffect(()=>{
    let current=true;
    setImage('');
    void invoke<Preview|null>('cameraPoster',{id,revision}).then(frame=>{
      if(current&&frame?.image.startsWith('data:image/'))setImage(frame.image);
    }).catch(()=>{});
    return ()=>{current=false;};
  },[id,revision]);
  return image?<div className="camera-poster"><img src={image} alt="相机内嵌预览"/><span role="status">相机预览 · 正在解码 RAW</span></div>:
    <div className="canvas-message" role="status">正在读取 RAW 照片…</div>;
}

export function DetailLayer({id,revision,width,height,scale,pan,stage,original,sourceView,clip,clipping=0,bypassMaskSlot,onPhase}:Props){
  const identity=JSON.stringify([id,revision,width,height,original,sourceView,clipping,bypassMaskSlot]);
  const [shown,setShown]=useState<{identity:string;tiles:Tile[]}>({identity:'',tiles:[]});
  const [failure,setFailure]=useState('');
  const [retry,setRetry]=useState(0);
  const cache=useRef(new Map<string,Tile>());
  const lastIdentity=useRef('');
  const x0=Math.max(0,Math.floor((width/2-(stage.w/2+pan.x)/scale)/128)*128);
  const y0=Math.max(0,Math.floor((height/2-(stage.h/2+pan.y)/scale)/128)*128);
  const x1=Math.min(width,Math.ceil((width/2+(stage.w/2-pan.x)/scale)/128)*128);
  const y1=Math.min(height,Math.ceil((height/2+(stage.h/2-pan.y)/scale)/128)*128);
  useEffect(()=>{
    let active=true;
    setFailure('');
    const sameView=lastIdentity.current===identity;
    lastIdentity.current=identity;
    const requests:{rect:Box;key:string}[]=[];
    for(let y=y0;y<y1;y+=1280)for(let x=x0;x<x1;x+=1600){
      const rect={x,y,width:Math.min(x1-x,1600),height:Math.min(y1-y,1280)};
      requests.push({rect,key:JSON.stringify([identity,rect])});
    }
    const missing=requests.some(({key})=>!cache.current.has(key));
    if(missing)onPhase?.('loading');
    const load=()=>void (async()=>{
      const visible:Tile[]=[];
      for(const {rect,key} of requests){
        let tile=cache.current.get(key);
        if(!tile){
          const image=await invoke<Preview>('viewport',{id,revision,original,sourceView,clipping,bypassMaskSlot,...rect});
          if(!active)return;
          if(image.width!==rect.width||image.height!==rect.height||!image.image.startsWith('data:image/png;base64,'))throw Error('原像素区域尺寸不一致。');
          const ready=new Image();ready.src=image.image;await ready.decode();
          if(!active)return;
          tile={...rect,image:image.image};
          cache.current.set(key,tile);
          if(cache.current.size>6)cache.current.delete(cache.current.keys().next().value!);
        }
        visible.push(tile);
        if(active)setShown({identity,tiles:[...visible]});
      }
      if(active)onPhase?.('ready');
    })().catch(error=>{if(active){setFailure(messageOf(error));onPhase?.('error');}});
    // Coalesce motion across new regions. A released edit or an already decoded region can draw immediately.
    let timer:number|undefined;
    if(sameView&&missing)timer=window.setTimeout(load,65);
    else load();
    return ()=>{active=false;clearTimeout(timer);};
  },[identity,x0,y0,x1,y1,retry,onPhase]);
  const tiles=shown.identity===identity?shown.tiles:[];
  return <div className="exact-layer" aria-label={original?'原片原像素细节':'调整后原像素细节'}
    style={clip===undefined?undefined:{clipPath:'inset(0 '+(100-clip)+'% 0 0)'}}>
    {tiles.map(t=><img key={t.x+'/'+t.y} alt="" data-region={[t.x,t.y,t.width,t.height].join(',')} draggable={false} src={t.image}
      style={{left:(100*t.x/width)+'%',top:(100*t.y/height)+'%',width:(100*t.width/width)+'%',height:(100*t.height/height)+'%',
        backgroundPosition:(-t.x*scale)+'px '+(-t.y*scale)+'px'}}/>)}
    {failure&&<button className="detail-error" title={failure} onPointerDown={e=>e.stopPropagation()} onClick={()=>setRetry(v=>v+1)}>重读原像素细节</button>}
  </div>;
}
