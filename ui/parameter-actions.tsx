import React,{useEffect,useMemo,useState} from 'react';
import {Control,PhotoDocument,Preview,Values,displayValue,invoke,messageOf} from './model';
import {Modal} from './widgets';

export type ActionMode='paste'|'person'|'color';
type PersonTarget={index:number;face?:{index:number};body?:{index:number}};
const colorGroups=['RGB 曲线','HSL 八色','色彩分级','色彩校准'];
function groupOf(c:Control){
  if(['temperature','tint'].includes(c.key))return '白平衡';
  if(['saturation','vibrance'].includes(c.key))return '色彩浓淡';
  if(colorGroups.includes(c.group))return c.group;
  if(c.group==='3D LUT'||c.group==='LUT 资源')return '3D LUT';
  if(c.group==='基础')return ['clarity','dehaze','sharpness','noise','noise_chroma','noise_luma'].includes(c.key)?'细节与降噪':'光影';
  return '';
}
export function actionGroups(mode:ActionMode,controls:Control[],doc:PhotoDocument,copied:Values|null,person?:PersonTarget){
  const result=new Map<string,{values:Values;rows:Control[]}>();
  const lutLocked=controls.some(c=>groupOf(c)==='3D LUT'&&doc.locks.includes(c.key));
  for(const c of controls){
    if(doc.locks.includes(c.key))continue;
    let group=groupOf(c);
    if(mode==='person'){
      const face=person?.face,body=person?.body;
      if(face&&c.key.startsWith('portrait_p'+(face.index+1)+'_'))group='脸部调整';
      else if(body&&(body.index===0?/^body_(shoulders|arms|waist|abdomen|hips|thighs|calves|legshape|leglength|proportion)$/.test(c.key):c.key.startsWith('body_p'+(body.index+1)+'_')))group='身体调整';
      else continue;
    }
    if(!group||(mode==='color'&&['细节与降噪','光影'].includes(group))||(group==='3D LUT'&&lutLocked))continue;
    const next=mode==='paste'?copied?.[c.key]:c.initial;
    if(next===undefined||!Number.isFinite(next)||next===(doc.parameters[c.key]??c.initial))continue;
    const item=result.get(group)??{values:{} as Values,rows:[] as Control[]};
    item.values[c.key]=next;item.rows.push(c);result.set(group,item);
  }
  const lut=result.get('3D LUT');
  if(lut?.rows.some(c=>c.group==='LUT 资源')){
    for(const c of controls.filter(c=>c.group==='LUT 资源'))lut.values[c.key]=mode==='paste'?copied![c.key]:c.initial;
  }
  return result;
}
export function ParameterAction({mode,doc,controls,copied,person,busy,close,apply}:{mode:ActionMode;doc:PhotoDocument;controls:Control[];copied:Values|null;person?:PersonTarget;
  busy:boolean;close:()=>void;apply:(v:Values,label:string)=>Promise<unknown>}){
  const groups=useMemo(()=>actionGroups(mode,controls,doc,copied,person),[mode,controls,doc,copied,person?.face?.index,person?.body?.index]);
  const [selection,set]=useState(()=>Array.from(groups.keys()));
  const [frame,show]=useState(''),[error,fail]=useState('');
  const values=useMemo(()=>Object.assign({},...selection.map(k=>groups.get(k)?.values||{})) as Values,[selection,groups]);
  useEffect(()=>{let live=true;show('');fail('');
    const timer=setTimeout(()=>{invoke<Preview>('preview',{id:doc.photoId,revision:doc.revision,maxEdge:480,values}).then(p=>{if(live)show(p.image);},e=>{if(live)fail(messageOf(e));});},120);
    return()=>{live=false;clearTimeout(timer);};},[doc.photoId,doc.revision,values]);
  const label=mode==='paste'?'选择性粘贴':mode==='person'?'还原人物 '+((person?.index??0)+1)+' 的美颜':'还原全图调色';
  return <Modal title={label} close={close}>{groups.size===0?<p>当前没有可更改的未锁定参数。</p>:<>
    <div className="parameter-action-preview">{frame?<img src={frame} alt="调整后的效果"/>:<span>{error||'正在渲染效果…'}</span>}</div>
    <div className="parameter-action-list">{Array.from(groups,([key,item])=><div key={key}><label><input type="checkbox" checked={selection.includes(key)} onChange={e=>set(e.target.checked?[...selection,key]:selection.filter(s=>s!==key))}/>{key}</label>
    <small>{item.rows.filter(c=>c.group!=='LUT 资源').map(c=>c.label+' '+displayValue(doc.parameters[c.key]??c.initial,c)+' → '+displayValue(item.values[c.key],c)).join('，')||'替换 LUT 文件'}</small></div>)}</div>
    <div className="dialog-actions"><button className="secondary" onClick={close}>取消</button><button className="primary" disabled={busy||!selection.length||!frame} onClick={async()=>{if(await apply(values,label))close();}}>应用所选调整</button></div></>}</Modal>;
}
