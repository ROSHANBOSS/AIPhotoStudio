import React,{useEffect,useState} from 'react';
import {Snapshot,invoke,messageOf} from './model';
import {Modal} from './widgets';

type Disk={root:string;originals:number;unknown:number;edits:number;backups:number;previews:number;browser:number;released:number;last?:string};
type Transfer={path:string;photos:number;assets?:number;missing?:number;names?:string[]};
const size=(bytes:number)=>bytes<1024?bytes+' B':bytes<1048576?(bytes/1024).toFixed(1)+' KiB':bytes<1073741824?(bytes/1048576).toFixed(1)+' MiB':(bytes/1073741824).toFixed(2)+' GiB';
async function carryOldPreferences(state:Snapshot){
  const prefix='aiphoto/people/1/';
  let keys:string[]=[];
  try{keys=Object.keys(localStorage).filter(k=>k.startsWith(prefix));}catch{return;}
  for(const key of keys){
    let pair:unknown,pref:string|null;
    try{pair=JSON.parse(key.slice(prefix.length));pref=localStorage.getItem(key);}catch{continue;}
    if(Array.isArray(pair)&&pair[0]===state.dataRoot&&typeof pair[1]==='string'&&pref)
      await invoke('libraryPeople',{id:pair[1],preference:pref,onlyIfMissing:true});
  }
}
export function LibraryStorage({state,close}:{state:Snapshot;close:()=>void}){
  const [disk,setDisk]=useState<Disk>(),[busy,setBusy]=useState(''),[error,setError]=useState(''),[report,setReport]=useState<Transfer>();
  const [note,setNote]=useState('');
  useEffect(()=>{let live=true;void invoke<Disk>('libraryDisk').then(v=>{if(live)setDisk(v);}).catch(e=>{if(live)setError(messageOf(e));});return ()=>{live=false;};},[]);
  async function act(kind:'clear'|'backup'|'restore'|'open'|'refresh'){
    setBusy(({clear:'正在清理预览…',backup:'正在打包编辑资料…',restore:'正在校验并恢复图库…',open:'正在打开图库…',refresh:'正在统计…'})[kind]);setError('');setNote('');
    try{
      if(kind==='open'){await invoke('libraryOpenRestored');return;}
      if(kind==='clear'||kind==='refresh'){
        const info=await invoke<Disk>(kind==='clear'?'libraryClearPreviews':'libraryDisk');setDisk(info);
        if(kind==='clear')setNote('已清理 '+size(info.released)+' 照片预览。再次打开照片会按需生成。');
        return;
      }
      if(kind==='backup')await carryOldPreferences(state);
      const value=await invoke<Transfer|null>(kind==='backup'?'libraryBackup':'libraryRestore');
      if(value){setReport(value);if(kind==='restore')setDisk(await invoke<Disk>('libraryDisk'));}
    }catch(e){setError(messageOf(e));}finally{setBusy('');}
  }
  return <Modal title="图库与空间" close={busy?()=>{}:close}>
    <h3>图库占用</h3>{disk?<><dl className="storage-sizes"><dt>原片引用 <small>按登记大小，可能在其他磁盘</small></dt><dd>{size(disk.originals)}{disk.unknown>0?'，另有 '+disk.unknown+' 张大小未知':''}</dd>
      <dt>编辑资料与个人预设</dt><dd>{size(disk.edits)}</dd><dt>升级前的数据库备份</dt><dd>{size(disk.backups)}</dd>
      <dt>可重建的照片预览</dt><dd>{size(disk.previews)}</dd><dt>桌面界面数据</dt><dd>{size(disk.browser)}</dd></dl>
      <div className="button-line"><button className="secondary small" disabled={!!busy||!disk.previews} onClick={()=>void act('clear')}>清理照片预览 · {size(disk.previews)}</button>
      <button className="text-button" disabled={!!busy} onClick={()=>void act('refresh')}>重新统计</button></div></>:<p>正在统计占用…</p>}
    <h3>备份或换电脑</h3><p className="dialog-note">图库包包含编辑历史、人物选择、蒙版、参数锁、项目和个人预设。原片需要单独携带。恢复时选择一个新的图库目录。</p>
    <div className="button-line"><button className="primary small" disabled={!!busy} onClick={()=>void act('backup')}>备份整库</button>
      <button className="secondary small" disabled={!!busy} onClick={()=>void act('restore')}>从备份恢复</button>
      {disk?.last&&<button className="secondary small" title={disk.last} disabled={!!busy} onClick={()=>void act('open')}>打开已恢复的图库</button>}</div>
    {report&&<div className="transfer-report"><strong>{report.missing===undefined?'已备份 '+report.photos+' 条照片记录':'已恢复 '+report.photos+' 张在库照片'}</strong><p>{report.path}</p>
      {report.missing!==undefined&&<p>{report.missing?'有 '+report.missing+' 张需要在新图库点「找回原片」：'+(report.names??[]).join('、'):'所有在库原片均可在当前电脑定位。'}</p>}</div>}
    {(busy||note)&&<p role="status">{busy||note}</p>}{error&&<p className="field-error" role="alert">{error}</p>}
  </Modal>;
}
