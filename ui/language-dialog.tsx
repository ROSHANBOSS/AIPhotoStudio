import React,{useState} from 'react';
import type {Language,LanguagePreference} from './locale';
import {Modal} from './widgets';

export function LanguageDialog({initial,startup,save,close}:{initial:LanguagePreference;startup:boolean;save:(lang:Language,ask:boolean)=>Promise<void>;close:()=>void}){
  const [ask,setAsk]=useState(initial.askOnStartup),[busy,setBusy]=useState(false),[error,setError]=useState('');
  async function choose(lang:Language){
    if(busy)return;setBusy(true);
    try{await save(lang,ask);}catch(e){setError(String(e));}finally{setBusy(false);}
  }
  return <Modal title={startup?'选择语言 / Choose a language':'界面语言'} close={startup?()=>{}:close} className={startup?'language-dialog startup-language':'language-dialog'}>
    <div data-language-picker="true" className="language-options">
      <button data-language="zh-CN" className="secondary" lang="zh-CN" translate="no" disabled={busy} aria-pressed={initial.language==='zh-CN'} onClick={()=>void choose('zh-CN')}>简体中文</button>
      <button data-language="en" className="secondary" lang="en" translate="no" disabled={busy} aria-pressed={initial.language==='en'} onClick={()=>void choose('en')}>English</button>
    </div>
    <label className="language-ask"><input type="checkbox" checked={ask} disabled={busy} onChange={e=>setAsk(e.target.checked)}/>{startup?'每次启动时询问 / Ask at every launch':'每次启动时询问'}</label>
    {error&&<p role="alert">{error}</p>}
  </Modal>;
}
