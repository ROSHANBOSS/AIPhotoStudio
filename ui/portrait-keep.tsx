import React,{useEffect,useRef,useState} from 'react';
import {BodyPerson,Snapshot,clamp,invoke,messageOf} from './model';
import {Modal} from './widgets';

type KeepPoint={x:number;y:number;radius:number};
type KeepInput={mesh:string;image:string;width:number;height:number;box:BodyPerson['box'];points:KeepPoint[]};

export function PortraitKeepDialog({id,revision,faceIndex,close,onSaved}:{
  id:string;revision:number;faceIndex:number;close:()=>void;onSaved:(state:Snapshot)=>void;
}){
  const [info,setInfo]=useState<KeepInput|null>(null),[points,setPoints]=useState<KeepPoint[]>([]);
  const [size,setSize]=useState(6),[saving,setSaving]=useState(false),[error,setError]=useState('');
  const live=useRef(true);
  useEffect(()=>{
    live.current=true;
    void invoke<KeepInput>('portraitKeepInput',{id,revision,faceIndex}).then(result=>{
      if(live.current){setInfo(result);setPoints(result.points);}
    }).catch(e=>{if(live.current)setError(messageOf(e));});
    return ()=>{live.current=false;};
  },[id,revision,faceIndex]);
  const box=info?.box;
  const left=box?Math.max(0,box.x-.06*box.w):0,top=box?Math.max(0,box.y-.06*box.h):0;
  const width=box?Math.min(1,box.x+1.06*box.w)-left:1,height=box?Math.min(1,box.y+1.06*box.h)-top:1;
  const aspect=info?width*info.width/(height*info.height):1;
  function add(event:React.MouseEvent<HTMLDivElement>){
    if(!info||saving)return;
    const rect=event.currentTarget.getBoundingClientRect();
    const x=left+clamp((event.clientX-rect.left)/rect.width,0,1)*width;
    const y=top+clamp((event.clientY-rect.top)/rect.height,0,1)*height;
    const b=info.box;
    if(x<b.x||y<b.y||x>b.x+b.w||y>b.y+b.h){setError('请在这张脸的轮廓内点选。');return;}
    if(points.length>=96){setError('每张照片最多保留 96 处。');return;}
    const radius=.01*size*Math.min(b.w*info.width,b.h*info.height)/Math.min(info.width,info.height);
    setPoints(list=>[...list,{x,y,radius}]);setError('');
  }
  async function save(){
    if(!info||saving)return;
    setSaving(true);setError('');
    try{
      const next=await invoke<Snapshot>('portraitKeepSave',{id,revision,faceIndex,mesh:info.mesh,points:points.map(({x,y,radius})=>({x,y,radius}))});
      if(live.current){onSaved(next);close();}
    }catch(e){if(live.current){setError(messageOf(e));setSaving(false);}}
  }
  const dirty=!!info&&(points.length!==info.points.length||points.some((p,i)=>p!==info.points[i]));
  return <Modal title={'人脸 '+(faceIndex+1)+' · 保留痣和雀斑'} close={saving?()=>{}:close} className="portrait-spot-dialog">
    {info?<><p className="dialog-note">在原片上点痣或雀斑。圆内的肤质会保留，点已标记的圆可以移除。</p>
      <div className="portrait-spot-image portrait-keep-image" onClick={add} style={{aspectRatio:String(aspect),
        width:'min(100%, '+(50*aspect)+'vh)',cursor:'crosshair'}}>
        <img alt="所选人脸的原片" draggable={false} src={info.image}
          style={{width:100/width+'%',height:100/height+'%',left:-100*left/width+'%',top:-100*top/height+'%'}}/>
        {points.map((p,i)=>{
          const diameter=2*p.radius*Math.min(info.width,info.height);
          return <button type="button" className="portrait-keep-point" key={i} disabled={saving} aria-label={'移除保留点 '+(i+1)}
            style={{left:100*(p.x-left)/width+'%',top:100*(p.y-top)/height+'%',width:100*diameter/(width*info.width)+'%',
              height:100*diameter/(height*info.height)+'%'}} onClick={e=>{e.stopPropagation();setPoints(list=>list.filter((_p,j)=>j!==i));setError('');}}/>;
        })}
      </div>
      <label className="keep-radius">新标记半径 <input type="range" aria-label="新标记半径" min="2" max="12" step="1" value={size}
        disabled={saving} onChange={e=>setSize(Number(e.target.value))}/><span>人脸短边的 {size}%</span></label>
      <p className="dialog-note" role="status">已标记 {points.length} 处</p></>:!error&&<p role="status">正在读取这张脸的原片…</p>}
    {error&&<p className="field-error" role="alert">{error}</p>}
    <div className="dialog-actions"><button type="button" className="secondary" disabled={saving} onClick={close}>取消</button>
      <button type="button" className="primary" disabled={!dirty||saving} onClick={()=>void save()}>保存保留位置</button></div>
  </Modal>;
}
