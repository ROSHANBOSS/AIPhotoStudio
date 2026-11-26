import React,{useRef,useState} from 'react';
import {invoke,messageOf} from './model';
import type {NormalizedPoint,PhotoDocument,Preview} from './model';

type BandReply={band:string|null;strength:number;overlay:string|null};
type BandSession={key:string;epoch:number;frameView:string;input?:Preview;point?:NormalizedPoint;result?:BandReply;
  showOverlay:boolean;showFinal:boolean;sampling:boolean;error?:string};
const bandNames:Record<string,string>={red:'红色',orange:'橙色',yellow:'黄色',green:'绿色',aqua:'青色',blue:'蓝色',purple:'紫色',magenta:'洋红'};

export function useColorBand(doc:PhotoDocument|null){
  const key=doc?doc.photoId+'@'+doc.revision:'';
  const [state,setState]=useState<BandSession|null>(null);
  const live=useRef<BandSession|null>(null),sequence=useRef(0);
  function update(next:BandSession|null){live.current=next;setState(next);}
  function change(patch:Partial<BandSession>){if(live.current)update({...live.current,...patch});}
  async function begin(frameView:string){
    if(!doc)return;
    const epoch=++sequence.current;
    update({key,epoch,frameView,showOverlay:true,showFinal:false,sampling:false});
    try{
      const input=await invoke<Preview>('colorBandInput',{id:doc.photoId,revision:doc.revision,frameView});
      if(live.current?.epoch===epoch)change({input});
    }catch(e){if(live.current?.epoch===epoch)change({error:messageOf(e)});}
  }
  async function pick(point:NormalizedPoint,showChannel:(band:string)=>void){
    const s=live.current;
    if(!doc||s?.key!==key||!s.input||s.showFinal)return;
    const epoch=++sequence.current;
    change({epoch,point,sampling:true,result:undefined,error:undefined});
    try{
      const result=await invoke<BandReply>('sampleColorBand',{id:doc.photoId,revision:doc.revision,...point,frameView:s.frameView});
      if(live.current?.epoch!==epoch)return;
      change({result,sampling:false});
      if(result.band)showChannel(result.band);
    }catch(e){if(live.current?.epoch===epoch)change({sampling:false,error:messageOf(e)});}
  }
  const session=state?.key===key?state:null;
  return {session,begin,pick,change,cancel:()=>update(null)};
}

export function ColorBandPanel({tool,canLocal,local}:{tool:ReturnType<typeof useColorBand>;canLocal:boolean;local:()=>void}){
  const s=tool.session;
  if(!s)return null;
  return <section className="color-band-panel" aria-label="点照片调色">
    <div className="band-feedback"><strong>{s.showFinal?'成片对照':'HSL 取色底图'}</strong>
      {s.error?<span role="alert">{s.error}</span>:<span role="status">{s.sampling?'正在计算全图范围…':
        s.result?.band?'全图'+bandNames[s.result.band]+'通道 · 此点影响 '+Math.max(1,Math.round(s.result.strength*100))+'%':
        s.result?'这个点接近中性灰，八色通道在这里没有作用。':s.input?'点照片里的颜色，定位全图通道。':'正在读取取色底图…'}</span>}
    </div>
    <div className="band-options">
      {s.result?.band&&<label><input type="checkbox" aria-label="显示全图影响范围" checked={s.showOverlay} disabled={s.showFinal}
        onChange={e=>tool.change({showOverlay:e.target.checked})}/>显示全图影响范围</label>}
      <button className="secondary small" disabled={!s.input} onClick={()=>tool.change({showFinal:!s.showFinal})}>{s.showFinal?'返回取色底图':'查看成片'}</button>
      {canLocal&&<button className="secondary small" disabled={!s.result} onClick={local}>用此点建局部颜色范围</button>}
      <button className="secondary small" onClick={tool.cancel}>结束取色</button>
    </div>
    <small>{s.result?'金色越浓，全图通道影响越大。只改一处可建局部范围，调容差或用擦除笔修边。':
      '底图保留当前构图，显示进入八色调节时的颜色和位置。'}</small>
  </section>;
}
