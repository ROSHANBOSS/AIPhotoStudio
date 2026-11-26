import React,{useEffect,useRef,useState} from 'react';
import {Photo,invoke,messageOf} from './model';
import {Modal} from './widgets';

type Signature={signature:string;image:string;source:string|null;width:number;height:number};
type Match={photo:Photo;sample:Signature;exact:boolean;distance:number};

// Compare spatially aligned colour at three scales. The local cutoff helps reject
// a changed face or an added caption even when the overall colour stays similar.
export function copyDistance(a:Signature,b:Signature) {
  if(a.source&&a.source===b.source)return 0;
  const ar=a.width/a.height,br=b.width/b.height;
  if(Math.abs(ar/br-1)>.012)return Infinity;
  const av=atob(a.signature),bv=atob(b.signature);
  if(av.length!==3072||bv.length!==av.length)return Infinity;
  let sum=0,variance=0,mean=0,tail=0;
  const patches=new Float64Array(64);
  for(let i=0;i<1024;i++){
    let error=0,luma=0;
    for(let c=0;c<3;c++){const v=av.charCodeAt(3*i+c);error+=Math.abs(v-bv.charCodeAt(3*i+c));luma+=v;}
    error/=3;luma/=3;sum+=error;mean+=luma;variance+=luma*luma;if(error>22)tail++;
    patches[(i%32>>2)+(i>>7)*8]+=error/16;
  }
  if(variance/1024-(mean/1024)**2<65||tail>23||Math.max(...patches)>18)return Infinity;
  return sum/1024<=5.5?sum/1024:Infinity;
}
function useDetail(p:Photo|undefined,point:{x:number;y:number}){
  const [view,setView]=useState<{src?:string;error?:string}>({});
  useEffect(()=>{let current=true;setView({});
    if(p)void invoke<{image:string}>('copyDetail',{id:p.id,revision:p.revision??0,...point}).then(v=>{if(current)setView({src:v.image});})
      .catch(e=>{if(current)setView({error:messageOf(e)});});
    return ()=>{current=false;};
  },[p?.id,p?.revision,point]);
  return view;
}
export function CopyCompare({photos,initial,choose,close}:{photos:Photo[];initial:string|null;choose:(p:Photo)=>void;close:()=>void}){
  const [id,setId]=useState(photos.find(p=>p.id===initial)?.id??photos[0]?.id??'');
  const anchor=photos.find(p=>p.id===id);
  const [sample,setSample]=useState<Signature>();
  const [matches,setMatches]=useState<Match[]>([]),[picked,setPicked]=useState('');
  const [done,setDone]=useState(0),[total,setTotal]=useState(0),[misses,setMisses]=useState(0),[error,setError]=useState('');
  const [scanning,setScanning]=useState(false),[point,setPoint]=useState({x:.5,y:.5});
  const serial=useRef(0);
  useEffect(()=>()=>{serial.current++;},[]);
  const match=matches.find(m=>m.photo.id===picked)??matches[0];
  const left=useDetail(match?anchor:undefined,point),right=useDetail(match?.photo,point);
  function reset(key:string){serial.current++;setId(key);setSample(undefined);setMatches([]);setDone(0);setTotal(0);setMisses(0);setPicked('');setError('');setScanning(false);}
  async function scan(){
    if(!anchor)return;
    const token=++serial.current;setScanning(true);setError('');setMatches([]);setPicked('');setDone(0);setMisses(0);setSample(undefined);
    const candidates=photos.filter(p=>p.id!==anchor.id&&Math.abs((p.width/p.height)/(anchor.width/anchor.height)-1)<=.012);
    setTotal(candidates.length);
    const request=(p:Photo)=>invoke<Signature>('copySignature',{id:p.id,revision:p.revision??0});
    try{
      const a=await request(anchor);if(serial.current!==token)return;setSample(a);
      for(const [i,p] of candidates.entries()){
        try{
          const b=await request(p);if(serial.current!==token)return;
          const distance=copyDistance(a,b);
          if(Number.isFinite(distance))setMatches(old=>[...old,{photo:p,sample:b,exact:!!a.source&&a.source===b.source,distance}].sort((x,y)=>x.distance-y.distance));
        }catch{if(serial.current!==token)return;setMisses(v=>v+1);}
        setDone(i+1);
      }
    }catch(e){if(serial.current===token)setError(messageOf(e));}
    finally{if(serial.current===token)setScanning(false);}
  }
  function pane(p:Photo,s:Signature,detail:typeof left){
    return <div className="copy-pane"><strong>{p.name}</strong><small>{p.width} × {p.height} · {typeof p.fileBytes==='number'?(p.fileBytes/1e6).toFixed(2)+' MB':'大小未知'}</small>
      <button className="copy-overview" aria-label={'选择对照细节 '+p.name} onClick={e=>{const b=e.currentTarget.getBoundingClientRect();if(e.detail)setPoint({x:Math.min(1,Math.max(0,(e.clientX-b.left)/b.width)),y:Math.min(1,Math.max(0,(e.clientY-b.top)/b.height))});}}>
        <img src={s.image} alt="全图"/><i style={{left:point.x*100+'%',top:point.y*100+'%'}}/></button>
      <div className="copy-detail">{detail.src?<img src={detail.src} alt={p.name+' 原像素细节'}/>:<small>{detail.error||'正在读取原像素…'}</small>}</div>
      <button className="secondary small" onClick={()=>choose(p)}>编辑这张照片</button></div>;
  }
  return <Modal title="查找同画面副本" close={close}><label className="copy-select">基准照片 <select aria-label="副本基准照片" value={id} onChange={e=>reset(e.target.value)}>{photos.map(p=><option key={p.id} value={p.id}>{p.name}</option>)}</select></label>
    <p className="dialog-note">在当前图库范围内比对构图接近的原片。可逐张选择要编辑的版本，两张照片都会留在图库。</p>
    <div className="button-line"><button className="primary small" disabled={!anchor||scanning} onClick={()=>void scan()}>查找副本</button>
    {scanning&&<button className="secondary small" onClick={()=>{serial.current++;setScanning(false);}}>停止查找</button>}</div>
    {sample&&<p role="status">已比较 {done} / {total} 张 · 找到 {matches.length} 张{misses>0?' · '+misses+' 张无法读取':''}</p>}{error&&<p role="alert">{error}</p>}
    {anchor&&sample&&match&&<><label className="copy-select">候选照片 <select aria-label="副本候选照片" value={match.photo.id} onChange={e=>setPicked(e.target.value)}>{matches.map(m=><option key={m.photo.id} value={m.photo.id}>{m.photo.name}</option>)}</select></label>
    <p className="dialog-note">{match.exact?'文件内容相同。':'画面相似，请核对细节。'} 点任一全图可同步查看两张照片同一位置的原像素。</p>
    <div className="copy-pair">{pane(anchor,sample,left)}{pane(match.photo,match.sample,right)}</div></>}</Modal>;
}
