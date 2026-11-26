import React,{useEffect,useRef,useState} from 'react';
import {BodyPerson,Snapshot,invoke,messageOf} from './model';
import {decodeBodyInput} from './body-detector';
import {detectPortraits} from './portrait-detector';
import {PortraitSpotDialog} from './portrait-spots';
import {PortraitKeepDialog} from './portrait-keep';
import {PeopleChoices,usePeople} from './people';
import {PersonSearchControls} from './person-search';
export function PortraitPanel({id,revision,onSaved,people,selected,disabled,searching,boxes,showBoxes}:{
  people:BodyPerson[];selected?:number;disabled:boolean;searching:boolean;
  id:string;revision:number;onSaved:(snapshot:Snapshot)=>void;boxes:boolean;showBoxes:(enabled:boolean)=>void;
}){
  const [busy,setBusy]=useState(false),[error,setError]=useState('');
  const [spotBase,setSpotBase]=useState('');
  const [keepBase,setKeepBase]=useState('');
  const faceCount=usePeople().visible.filter(p=>p.face).length;
  const spotKey=id+'/'+revision+'/'+selected;
  const live=useRef({id,revision,mounted:true});live.current.id=id;live.current.revision=revision;
  useEffect(()=>{live.current.mounted=true;return ()=>{live.current.mounted=false;};},[]);
  useEffect(()=>{setError('');},[id,revision]);
  async function recognize(){
    if(busy||disabled)return;
    const matches=()=>live.current.mounted&&live.current.id===id&&live.current.revision===revision;
    setBusy(true);setError('');
    try{
      const input=await invoke<{image:string;token:string}>('portraitInput',{id,revision});
      if(!matches())return;
      const result=await detectPortraits(await decodeBodyInput(input.image));
      if(!matches())return;
      if(!result.meshes.length)throw new Error(people.length?'没有重新检测到清晰人脸。请核对原有定位与保留位置，已保存的调整仍可使用。':'没有检测到清晰、完整的人脸。请尝试正脸或较大的半身照。');
      const saved=await invoke<Snapshot>('portraitSave',{id,revision,token:input.token,result});
      onSaved(saved);
    }catch(e){if(matches())setError(messageOf(e));}
    finally{if(live.current.mounted)setBusy(false);}
  }
  return <section className="local-section portrait-detection"><b>分人脸精修</b>
    <p role="status">{busy?'正在本机定位五官…':faceCount>1?'检测到 '+faceCount+' 张脸。点照片上的框或编号，分别调整。':
      faceCount?(selected===undefined?'已识别人脸，点选人物即可精修。':'已选中这张脸。'):people.length?'此照片中已定位的人脸均已忽略。':'检测照片中的人脸后，可单独调整肤质与五官。'}</p>
    <button type="button" className="secondary small" onClick={()=>void recognize()} disabled={disabled||busy||searching}>
      {people.length?'重新检测人脸':'检测人脸'}</button>
    <PersonSearchControls disabled={disabled||busy}/>
    {people.length>0&&<label className="portrait-box-toggle"><input type="checkbox" checked={boxes} onChange={e=>showBoxes(e.target.checked)}/>显示人脸框</label>}
    {selected!==undefined&&<div className="portrait-mark-tools"><button className="secondary small" type="button" disabled={busy||disabled||searching} onClick={()=>setSpotBase(spotKey)}>查找小瑕疵</button>
      <button className="secondary small" type="button" disabled={busy||disabled||searching} onClick={()=>setKeepBase(spotKey)}>保留痣和雀斑</button></div>}
    {keepBase===spotKey&&selected!==undefined&&<PortraitKeepDialog key={spotKey} id={id} revision={revision} faceIndex={selected}
      close={()=>setKeepBase('')} onSaved={onSaved}/>}
    {spotBase===spotKey&&selected!==undefined&&<PortraitSpotDialog key={spotKey} id={id} revision={revision} faceIndex={selected}
      close={()=>setSpotBase('')} onSaved={onSaved}/>}
    {error&&<p className="field-error" role="alert">{error}</p>}
    <PeopleChoices view="face" disabled={disabled||busy||searching} manage/>
  </section>;
}
