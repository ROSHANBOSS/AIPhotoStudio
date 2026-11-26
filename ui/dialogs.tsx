import React, {useState} from 'react';
import {Photo, PhotoDocument, Project, Recipe, timeOf} from './model';
import {Icon} from './icons';
import {Modal} from './widgets';
import {Thumb} from './preview';
export {BatchDialog} from './batch';
export function PresetDialog({presets,busy,save,close}: {presets: Recipe[]; busy: boolean; save: (name:string)=>void; close:()=>void}) {
  const [name,setName]=useState('');
  const duplicate=presets.some(p=>p.name.toLocaleLowerCase()===name.trim().toLocaleLowerCase());
  return <Modal title="保存为预设" close={busy?()=>{}:close}>
    <p className="dialog-note">将这张照片当前已保存的参数添加到「我的预设」。</p>
    <form onSubmit={e=>{e.preventDefault(); if(name.trim()&&!duplicate&&!busy)save(name.trim());}}>
      <label className="field-label" htmlFor="preset-name">预设名称</label><input id="preset-name" className="field" data-focus value={name} autoComplete="off" maxLength={80} placeholder="为这套调整命名" onChange={e=>setName(e.target.value)}/>
      {duplicate&&<p className="field-error">已有同名预设，请换一个名称。</p>}
      <div className="dialog-actions"><button type="button" className="secondary" onClick={close} disabled={busy}>取消</button><button type="submit" className="primary" disabled={busy||duplicate||!name.trim()}>保存预设</button></div>
    </form>
  </Modal>;
}
export function CopyRecipeDialog({photos,target,busy,copy,close}:{photos:Photo[];target:Photo;busy:boolean;copy:(source:string)=>void;close:()=>void}){
  const choices=photos.filter(p=>p.id!==target.id);
  const [id,setId]=useState(choices[0]?.id||'');
  return <Modal title="从其他照片复制配方" close={busy?()=>{}:close}><p className="dialog-note">将所选照片的编辑配方复制到「{target.name}」。应用后可通过编辑历史撤销。</p>
    <label className="field-label" htmlFor="source-photo">来源照片</label><select className="field" id="source-photo" value={id} disabled={busy} onChange={e=>setId(e.target.value)}>{choices.map(p=><option translate="no" key={p.id} value={p.id}>{p.name} · {p.path}</option>)}</select>
    <div className="dialog-actions"><button className="secondary" disabled={busy} onClick={close}>取消</button><button className="primary" disabled={busy||!id} onClick={()=>copy(id)}>复制配方</button></div>
  </Modal>;
}
export function HistoryPanel({doc,busy,restore}: {doc: PhotoDocument|null; busy:boolean; restore:(index:number)=>void}) {
  const list=(doc?.history||[]).map((item,idx)=>({item,idx:item.index??idx})).reverse();
  return <div className="history-panel"><div className="section-heading"><h2>编辑历史</h2><span className="section-detail">{list.length} 条</span></div>
    {doc ? <div className="history-list">{list.map(({item,idx})=><button key={item.id||idx} className={'history-item '+(idx===doc.cursor?'current ':'')+(idx>doc.cursor?'future':'')}
      disabled={busy||idx===doc.cursor} title={idx===doc.cursor?'当前版本':'恢复此版本'} onClick={()=>restore(idx)}>
      <span className="history-node"></span><span><b>{item.label}</b><small>{item.source==='ai'?'AI 调整':item.source==='manual'?'手动调整':item.source==='preset'?'预设调整':idx===0?'起始版本':'编辑版本'}{timeOf(item.when)&&' · '+timeOf(item.when)}</small></span>{idx===doc.cursor&&<em>当前</em>}
    </button>)}</div>:<p className="side-note">导入照片后可查看每一步调整。</p>}</div>;
}
export function ProjectDialog({projects,photos,initial,busy,close,save,member}:{
  projects:Project[]; photos:Photo[]; initial?:string; busy:boolean; close:()=>void;
  save:(name:string,id?:string)=>Promise<Project|undefined>; member:(p:string,id:string,include:boolean)=>void;
}){
  const [id,setId]=useState(initial||projects[0]?.id||'');
  const current=projects.find(p=>p.id===id);
  const [name,setName]=useState(current?.name||'');
  const [adding,setAdding]=useState(!current);
  const choose=(p:Project)=>{setId(p.id);setName(p.name);setAdding(false);};
  const duplicate=projects.some(p=>(adding||p.id!==id)&&p.name.toLowerCase()===name.trim().toLowerCase());
  return <Modal title="管理照片项目" className="batch-dialog" close={busy?()=>{}:close}>
    {!!projects.length&&<div className="project-tools"><select aria-label="选择项目" value={id} onChange={e=>{const p=projects.find(p=>p.id===e.target.value);if(p)choose(p);}} disabled={busy}>{projects.map(p=><option translate="no" key={p.id} value={p.id}>{p.name}</option>)}</select>
      <button className="secondary small" onClick={()=>{setAdding(true);setName('');}} disabled={busy}><Icon name="plus" size={12}/>新建</button></div>}
    <form className="project-form" onSubmit={async e=>{e.preventDefault();if(duplicate||!name.trim()||busy)return;const p=await save(name.trim(),adding?undefined:id);if(p)choose(p);}}>
      <label className="field-label" htmlFor="project-name">{adding?'新项目名称':'项目名称'}</label>
      <div><input id="project-name" className="field" maxLength={80} value={name} onChange={e=>setName(e.target.value)} disabled={busy} autoComplete="off" placeholder="输入项目名称"/>
        <button className="secondary small" type="submit" disabled={busy||!name.trim()||duplicate||(!adding&&name.trim()===current?.name)}>{adding?'创建':'重命名'}</button></div>
      {duplicate&&<p className="field-error">该项目名称已存在。</p>}
    </form>
    {current&&!adding&&<><div className="batch-select"><b>项目照片</b><span>已加入 {current.photoIds.length} 张</span></div>
      {photos.length?<div className="batch-list">{photos.map(p=><label key={p.id}><input type="checkbox" disabled={busy} checked={current.photoIds.includes(p.id)} onChange={e=>member(id,p.id,e.target.checked)}/><Thumb photo={p}/><span translate="no">{p.name}</span></label>)}</div>:<p className="dialog-note">导入照片后可在这里加入项目。</p>}</>}
    <div className="dialog-actions"><button className="primary" disabled={busy} onClick={close}>完成</button></div>
  </Modal>;
}
export function ShortcutDialog({close}: {close:()=>void}) {
  const items=[['导入照片','Ctrl + O'],['导出当前照片','Ctrl + Shift + E'],['从图库移除当前照片','Delete'],['撤销','Ctrl + Z'],['重做','Ctrl + Y'],['上一张 / 下一张','← / →'],['收藏当前照片','F'],['适应画布','0'],['原始大小','1'],['缩小 / 放大','− / +'],['前后对比','C'],['按住查看原片','空格'],['显示 / 隐藏面板','F8'],['在 AI 助手中发送','Ctrl + Enter']];
  return <Modal title="键盘快捷键" close={close}><div className="shortcut-list">{items.map(([t,k])=><div key={t}><span>{t}</span><kbd>{k}</kbd></div>)}</div></Modal>;
}
