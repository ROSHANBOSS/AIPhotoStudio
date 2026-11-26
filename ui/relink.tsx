import React, {useState} from 'react';
import {baseName, invoke, messageOf, Snapshot} from './model';
import {Modal} from './widgets';
import {Icon} from './icons';

type Candidate = {index:number; path:string; bytes:number; hash:string; match:string; detail:string; width:string; height:string; camera:string; date:string};
type Row = {row:number; path:string; bytes:number|null; hash:string|null; width:string; height:string; camera:string; date:string;
  status:string; suggested:number|null; more:boolean; candidates:Candidate[]};
type Plan = {token:string; folder:string; scanned:number; connected:number; skipped:number; rows:Row[]};
type Choice = {candidate:number; manual:boolean};
type State = Snapshot & {relinkCount:number};
const labels:Record<string,string> = {
  exact:'唯一指纹匹配', review:'需选择匹配文件', unverified:'旧记录缺少指纹', missing:'未找到候选',
  different:'内容或文件信息不同', occupied:'候选已被占用', blocked:'原路径暂时无法读取',
};
const pageSize=10;
function info(item:{bytes:number|null;width:string;height:string;camera:string;date:string}) {
  return [item.bytes==null?'':item.bytes.toLocaleString()+' 字节',item.width&&item.height?item.width+' × '+item.height:'',
    item.camera,item.date].filter(Boolean).join(' · ');
}
function digest(value:string) {
  return Array.from(atob(value),c=>c.charCodeAt(0).toString(16).padStart(2,'0')).join('');
}

export function RelinkDialog({close, saved}:{close:()=>void; saved:(snapshot:State)=>void}) {
  const [plan,setPlan]=useState<Plan|null>(null);
  const [choices,setChoices]=useState<Record<number,Choice>>({});
  const [busy,setBusy]=useState(''), [error,setError]=useState(''), [coverage,setCoverage]=useState('');
  const [page,setPage]=useState(0);
  const [pictures,setPictures]=useState<Record<number,{index:number;url:string}>>({});
  const [expanded,setExpanded]=useState<number|null>(null);
  function load(next:Plan,keep=false) {
    const picks:Record<number,Choice>={};
    for (const row of next.rows) {
      const previous=keep?choices[row.row]:undefined;
      if(previous&&row.candidates.some(c=>c.index===previous.candidate&&['verified','manual'].includes(c.match)))
        picks[row.row]={candidate:previous.candidate, manual:false};
      else if(!keep && row.suggested!=null) picks[row.row]={candidate:row.suggested,manual:false};
    }
    setChoices(picks); setPlan(next); setPictures({});
    if(!keep){setPage(0);setExpanded(null);}
  }
  async function run(title:string, action:()=>Promise<void>) {
    if(busy)return;
    setBusy(title);setError('');
    try{await action();}catch(e){setError(messageOf(e));}finally{setBusy('');}
  }
  function scan() {
    void run('正在扫描文件夹',async()=>{
      const next=await invoke<Plan|null>('relinkScan');
      if(next)load(next);
    });
  }
  function prepare() {
    void run('正在登记原片指纹',async()=>{
      const {known,total}=await invoke<{known:number;total:number}>('relinkPrepare');
      setCoverage('已登记 '+known+' / '+total+' 张原片的指纹。'+(known<total?'其余照片需接回原盘，或在找回时逐张核对。':''));
    });
  }
  function pick(row:Row) {
    if(!plan)return;
    void run('正在核对候选文件',async()=>{
      const next=await invoke<Plan|null>('relinkPick',{token:plan.token,row:row.row});
      if(next){load(next,true);setExpanded(row.row);}
    });
  }
  function inspect(row:Row,file:Candidate) {
    if(!plan)return;
    void run('正在读取候选照片',async()=>{
      const {image}=await invoke<{image:string}>('relinkPreview',{token:plan.token,row:row.row,candidate:file.index});
      setPictures(current=>({...current,[row.row]:{index:file.index,url:image}}));
    });
  }
  function choose(row:Row,file?:Candidate) {
    setChoices(current=>{
      const next={...current};
      if(file)next[row.row]={candidate:file.index,manual:false};else delete next[row.row];
      return next;
    });
    if(file?.match==='manual'&&pictures[row.row]?.index!==file.index)inspect(row,file);
  }
  const entries=Object.entries(choices);
  const needsReview=entries.some(([i,pick])=>plan?.rows[Number(i)]?.candidates.find(c=>c.index===pick.candidate)?.match==='manual'&&!pick.manual);
  const shared=entries.length!==new Set(entries.map(([,p])=>p.candidate)).size;
  function apply() {
    if(!plan||!entries.length||needsReview||shared)return;
    void run('正在核对文件并保存',async()=>{
      const next=await invoke<State>('relinkApply',{token:plan.token, choices:entries.map(([row,c])=>({row:Number(row),...c}))});
      saved({...next,relinkCount:next.relinkCount??entries.length});
    });
  }
  function cancelScan() {void invoke('relinkCancel').catch(e=>setError(messageOf(e)));}
  const exact=plan?.rows.filter(r=>r.status==='exact').length??0;
  const pageCount=Math.ceil((plan?.rows.length??0)/pageSize);
  return <Modal title="找回原片" className="relink-dialog" close={()=>{if(!busy)close();else if(busy==='正在扫描文件夹')cancelScan();}}>
    <p className="dialog-note">选择搬移后的文件夹，程序会在其中及子文件夹查找原片。核对后确认，即可让原有编辑、蒙版和项目继续引用这些照片。</p>
    <div className="button-line relink-tools">
      <button className="secondary" disabled={!!busy} onClick={scan}><Icon name="folder" size={15}/>{plan?'重新扫描文件夹':'选择新文件夹'}</button>
      <button className="text-button" disabled={!!busy} onClick={prepare}>移动前登记指纹</button>
      {busy==='正在扫描文件夹'&&<button className="text-button" onClick={cancelScan}>取消扫描</button>}
    </div>
    {coverage&&<p className="dialog-note" role="status">{coverage}</p>}
    {plan&&<>
      <p className="relink-folder">{plan.folder}</p>
      <p className="dialog-note">原路径可用 {plan.connected} 张，待处理 {plan.rows.length} 张，唯一匹配 {exact} 张。已检查 {plan.scanned} 个照片文件。
        {plan.skipped>0&&' 有 '+plan.skipped+' 个文件、文件夹或链接未读取，请复核匹配结果。'}</p>
      {plan.rows.length>1&&<div className="button-line relink-tools">
        <button className="text-button" disabled={!!busy||!exact} onClick={()=>setChoices(prev=>{
          const updated={...prev};for(const row of plan.rows)if(row.suggested!=null)updated[row.row]={candidate:row.suggested,manual:false};
          return updated;
        })}>选择全部唯一匹配</button>
        <button className="text-button" disabled={!!busy||!entries.length} onClick={()=>setChoices({})}>清除选择</button>
      </div>}
      {plan.rows.length>0&&<div className="relink-list">
        {plan.rows.slice(page*pageSize,(page+1)*pageSize).map(row=>{
          const choice=choices[row.row], picture=pictures[row.row];
          const selected=row.candidates.find(c=>c.index===choice?.candidate);
          const isOpen=expanded===row.row;
          return <div className="relink-row" key={row.row}>
            <div className="relink-title">
              <button className="relink-summary" aria-expanded={isOpen} onClick={()=>setExpanded(isOpen?null:row.row)}>
                <Icon name="chevron" size={13}/><strong>{baseName(row.path)}</strong><span>{labels[row.status]}</span>
              </button>
              {row.suggested!=null&&<label><input type="checkbox" checked={!!choice} disabled={!!busy}
                onChange={e=>choose(row,e.target.checked?row.candidates.find(c=>c.index===row.suggested):undefined)}/>使用匹配</label>}
            </div>
            <div className="relink-path">原位置　{row.path}</div>
            {selected&&!isOpen&&<div className="relink-path">新位置　{selected.path}</div>}
            {isOpen&&<div className="relink-options">
              <p>{info(row)}{row.hash&&<code title="旧记录的 SHA-256">{digest(row.hash)}</code>}</p>
              {row.status==='unverified'&&<p>旧记录没有可比对的内容指纹。请逐张查看候选照片，核对拍摄内容后再决定。</p>}
              {row.status==='review'&&<p>有多个候选、重复的图库记录或未读完的目录。请指定要引用的文件。</p>}
              {row.status==='blocked'&&<p>请先检查原路径的访问权限及文件占用，然后重新扫描。</p>}
              {row.status==='different'&&<p>找到的候选与旧记录不符。可以指定另一份原片，或将不同的照片另行导入图库。</p>}
              {row.status!=='blocked'&&<label><input type="radio" name={'relink-'+row.row} disabled={!!busy} checked={!choice} onChange={()=>choose(row)}/>跳过这张</label>}
              {row.candidates.map(c=><div key={c.index} className="relink-candidate">
                <label><input type="radio" name={'relink-'+row.row} checked={choice?.candidate===c.index}
                  disabled={!!busy||!['verified','manual'].includes(c.match)} onChange={()=>choose(row,c)}/><span>{c.path}</span></label>
                <small>{c.detail} · {info(c)}</small><code title="候选文件的 SHA-256">{digest(c.hash)}</code>
                <button className="text-button" onClick={()=>inspect(row,c)} disabled={!!busy}>查看候选照片</button>
                {picture?.index===c.index&&<img className="relink-photo" src={picture.url} alt={'候选原片 '+baseName(c.path)}/>}
                {c.match==='manual'&&choice?.candidate===c.index&&<label className="relink-consent"><input type="checkbox" disabled={!!busy||picture?.index!==c.index}
                  checked={choice.manual} onChange={e=>{const manual=e.target.checked;setChoices(prev=>({...prev,[row.row]:{candidate:c.index,manual}}));}}/>
                  我已看过候选照片，确认用它承接这张照片的编辑和蒙版</label>}
              </div>)}
              {row.more&&<p>还有其他候选，可用下面的按钮直接指定文件。</p>}
              {row.status!=='blocked'&&<button className="secondary small" disabled={!!busy} onClick={()=>pick(row)}>手动指定文件</button>}
            </div>}
          </div>;
        })}
      </div>}
      {pageCount>1&&<div className="relink-pages"><button disabled={page===0} onClick={()=>setPage(page-1)}>上一页</button>
        <span>{page+1} / {pageCount}</span><button disabled={page+1>=pageCount} onClick={()=>setPage(page+1)}>下一页</button></div>}
    </>}
    {shared&&<p className="field-error">多条记录选中了同一个文件，请修改选择。</p>}
    {needsReview&&<p className="dialog-note">请查看并逐张确认无指纹照片，或选择跳过。</p>}
    {error&&<p role="alert" className="field-error">{error}</p>}
    {busy&&<p role="status" className="dialog-note">{busy}…</p>}
    <div className="dialog-actions"><button className="secondary" disabled={!!busy} onClick={close}>关闭</button>
      {plan&&<button className="primary" onClick={apply} disabled={!!busy||!entries.length||needsReview||shared}>确认找回 {entries.length} 张</button>}</div>
  </Modal>;
}
