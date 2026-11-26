import React, {useEffect, useRef, useState} from 'react';
import {BodyPerson, Snapshot, invoke, messageOf} from './model';
import {decodeBodyInput, detectBody} from './body-detector';
import {PeopleChoices,usePeople} from './people';
import {PersonSearchControls} from './person-search';

type BodyProps = {
  people:BodyPerson[]; selected?:number; disabled:boolean;searching:boolean;
  photoId:string; revision:number; onSaved:(snapshot:Snapshot)=>void; onAuto:(index:number)=>Promise<void>;
};

export function BodyPanel({photoId, revision, people, selected, disabled, searching, onSaved, onAuto}:BodyProps) {
  const [busy, setBusy]=useState(false);
  const [feedback, setFeedback]=useState('');
  const [unlocated,setUnlocated]=useState<{id:string;faces:number[]}>({id:'',faces:[]});
  const current=useRef({photoId,revision});
  const mounted=useRef(true);
  current.current={photoId,revision};
  useEffect(()=>{mounted.current=true;return ()=>{mounted.current=false;};},[]);
  useEffect(()=>{setFeedback('');},[photoId,revision]);
  const subject=people.find(p=>p.index===selected);
  const selection=usePeople();
  async function recognize(automatic=false,fine=false) {
    if(busy||disabled)return;
    if(automatic&&subject){await onAuto(subject.index);return;}
    setBusy(true);
    setFeedback(fine?'正在从原图精细识别人物…':'正在本机识别人物…');
    const matches=()=>mounted.current&&current.current.photoId===photoId&&current.current.revision===revision;
    try {
      const args={id:photoId,revision};
      const input=await invoke<{image:string;token:string}>('bodyInput',{...args,fine});
      if(!matches())return;
      const result=await detectBody(await decodeBodyInput(input.image),undefined,fine);
      if(!matches())return;
      setUnlocated({id:photoId,faces:result.unlocatedFaces});
      if(!result.people)throw new Error(result.unlocatedFaces.length?'已定位人脸，肩躯的可见范围不足以进行人体美型。':'没有找到可调的人体。请检查肩膀或躯干是否清楚可见。');
      const snapshot=await invoke<Snapshot>('bodySave',{...args,token:input.token,result});
      const stillHere=matches();
      onSaved(snapshot);
      if(!stillHere)return;
      setFeedback('');
      if(automatic&&(!selection.all.length||selection.active)&&snapshot.bodyPeople?.length===1&&(snapshot.people?.length??1)===1)
        await onAuto(snapshot.bodyPeople[0].index);
    } catch(error){if(matches())setFeedback(messageOf(error));}
    finally{if(mounted.current)setBusy(false);}
  }
  let status='单人或合照都可以识别。半身照可调整清楚入镜的部位。';
  const bodyCount=selection.visible.filter(p=>p.body).length;
  if(bodyCount>1)status='已定位 '+bodyCount+' 人的身体。点画面中的人物框或下方编号，单独调整。';
  else if(bodyCount===1)status=subject?'已定位当前人物的身体。':'已识别身体。选择人物即可调整。';
  else if(people.length)status='此照片中已定位的身体均已忽略。';
  return <div className="body-detection">
    <div className="body-actions">
      <button type="button" onClick={()=>void recognize(true)} disabled={disabled||busy||searching||(people.length>0&&!subject?.available.length)}>自动美型预览</button>
      <button type="button" onClick={()=>void recognize()} disabled={disabled||busy||searching}>{busy?'正在识别…':people.length?'重新识别人体':'识别人物'}</button>
      <button type="button" title="从原图提取更多细节，适合远景和大合照" onClick={()=>void recognize(false,true)} disabled={disabled||busy||searching}>精细识别</button>
      <PersonSearchControls disabled={disabled||busy}/>
    </div>
    <p role="status" aria-live="polite">{feedback||status}</p>
    {unlocated.id===photoId && unlocated.faces.length>0&&<p className="body-scope">从左数第 {unlocated.faces.join('、')} 张脸的肩躯定位不足，无法单独进行人体美型。</p>}
    <PeopleChoices view="body" disabled={disabled||busy||searching} manage/>
    {subject&&<p className="body-scope">{selection.active?'当前选择人物 '+(selection.active.index+1)+'。':''}
      {subject.available.length===0?'可见部位不足，请选择其他人物。':subject.available.length<10?'仅显示能可靠定位的部位。':'可按部位微调。'}</p>}
  </div>;
}
