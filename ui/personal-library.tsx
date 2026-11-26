import React,{useEffect,useState} from 'react';
import {Photo,invoke,messageOf} from './model';
import {Modal} from './widgets';

export function PhotoNote({photo,busy,save}:{photo:Photo;busy:boolean;save:(taken:string,caption:string)=>Promise<unknown>}) {
  const [taken,date]=useState(photo.libraryDate||''),[caption,note]=useState(photo.caption||'');
  const changed=taken!==(photo.libraryDate||'')||caption!==(photo.caption||'');
  return <div className="photo-note"><label>补记拍摄日期<input aria-label="补记拍摄日期" maxLength={10} placeholder="2008、2008-07 或 2008-07-15" value={taken} onChange={e=>date(e.target.value)}/></label>
    <label>照片说明<textarea aria-label="照片说明" maxLength={500} value={caption} onChange={e=>note(e.target.value)}/></label>
    <button className="secondary" disabled={busy||!changed} onClick={()=>void save(taken,caption)}>保存图库备注</button></div>;
}

export function RemovedPhotos({busy,close,restore}:{busy:boolean;close:()=>void;restore:(p:Photo)=>Promise<unknown>}){
  const [rows,setRows]=useState<Photo[]>(),[error,fail]=useState(''),[generation,reload]=useState(0);
  useEffect(()=>{let live=true;invoke<Photo[]>('removedList').then(v=>{if(live)setRows(v);},e=>{if(live)fail(messageOf(e));});return()=>{live=false;};},[generation]);
  return <Modal title="最近移除" close={close}><p className="dialog-note">恢复后可继续原来的编辑，磁盘原片保留在原位置。</p>
    {error?<p role="alert">{error}</p>:!rows?<p>读取中…</p>:rows.length===0?<p>目前没有移除的照片。</p>:<div className="action-list">{rows.map(p=><button key={p.id} disabled={busy} title={p.path} onClick={async()=>{if(await restore(p))reload(n=>n+1);}}>恢复「{p.name}」<small>{String(p.folder||'')}</small></button>)}</div>}</Modal>;
}
