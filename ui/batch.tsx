import React,{useEffect,useRef,useState} from 'react';
import {Icon} from './icons';
import {Photo,formatOf,invoke,messageOf,requestId} from './model';
import {Thumb} from './preview';
import {Modal} from './widgets';
import {BatchProgress,defaultExport,ExportFields,ExportSettings,validExport} from './export-settings';
import {language} from './locale';

type Variant=ExportSettings&{folder:string;pattern:string;colorSpace:'srgb'|'adobeRgb'};
type Orientation='landscape'|'portrait'|'square';
type Logo={hash:string;name:string;width:number;height:number};
type Mark={kind?:'text'|'png'|null;logo?:Logo|null;text:string;size:number;opacity:number}&Record<Orientation,string>;
type Preset={id:string;revision:number;name:string;proof:Variant;final:Variant;watermark:Mark};
type Pending={id:string;root:string;name:string;total:number;created:string};
type Shelf={presets:Preset[];selected:string|null;pending:Pending[];adobeRgbAvailable:boolean};
type Outcome={id:string;root:string;total:number;paired:number;partial:number;count:number;paths:string[];errors:string[];cancelled:boolean;pauseReason?:string};
function newDeliveryPreset():Preset {
  const english=language()==='en';
  return {id:'',revision:0,name:english?'Client delivery':'客户交付',
    proof:{format:'jpeg',longEdge:2048,quality:85,folder:english?'Proofs':'选片小样',pattern:english?'{index}-{name}-proof':'{index}-{name}-小样',colorSpace:'srgb'},
    final:{format:'jpeg',longEdge:0,quality:95,folder:english?'Finals':'成片',pattern:'{name}',colorSpace:'srgb'},
    watermark:{text:english?'Proof':'选片小样',size:6,opacity:75,landscape:'bottomRight',portrait:'bottomRight',square:'bottomRight'}};
}
const orientations:[Orientation,string][]=[['landscape','横图'],['portrait','竖图'],['square','方图']];
const positions=[['topLeft','左上'],['topRight','右上'],['center','居中'],['bottomLeft','左下'],['bottomRight','右下']];

function VariantFields({title,value,change,adobe,example}:{title:string;value:Variant;change:(v:Variant)=>void;adobe:boolean;example:string}){
  const stem=example.replace(/\.[^.]*$/,'');
  const filename=value.pattern.replace(/\{(name|index)\}/g,(_,key)=>key==='name'?stem:'0001');
  return <fieldset className="delivery-variant"><legend>{title}</legend>
    <ExportFields value={value} change={settings=>change({...value,...settings})} busy={false} note={false} batch/>
    <div className="export-grid"><label>子目录<input value={value.folder} maxLength={64} onChange={e=>change({...value,folder:e.target.value})}/></label>
      <label>色彩配置<select value={value.colorSpace} onChange={e=>change({...value,colorSpace:e.target.value as Variant['colorSpace']})}>
        <option value="srgb">sRGB</option><option value="adobeRgb" disabled={!adobe}>Adobe RGB (1998){adobe?'':' · 本机缺少配置'}</option></select></label>
      <label className="delivery-wide">文件名模板<input value={value.pattern} maxLength={120} onChange={e=>change({...value,pattern:e.target.value})}/></label></div>
    <p className="delivery-path" translate="no">{value.folder}\{filename}.{value.format==='jpeg'?'jpg':value.format==='tiff'?'tif':'png'}</p>
  </fieldset>;
}

function DeliveryPanel({ids,example,locked,lock,canDeliver}:{ids:string[];example:string;locked:boolean;lock:(busy:boolean)=>void;canDeliver?:(ids:string[])=>boolean}){
  const [shelf,setShelf]=useState<Shelf>();
  const [draft,setDraft]=useState<Preset>(newDeliveryPreset);
  const [error,setError]=useState(''),[notice,setNotice]=useState(''),[token,setToken]=useState('');
  const [logoPreview,setLogoPreview]=useState(''),[logoError,setLogoError]=useState('');
  const [outcome,setOutcome]=useState<Outcome|null>(null);
  const inFlight=useRef(false);
  function adopt(next:Shelf){setShelf(next);const saved=next.presets.find(p=>p.id===next.selected);setDraft(saved||newDeliveryPreset());return saved;}
  useEffect(()=>{let live=true;invoke<Shelf>('deliveryPresets').then(s=>{if(live)adopt(s);}).catch(e=>{if(live)setError(messageOf(e));});return()=>{live=false;};},[]);
  const saved=shelf?.presets.find(p=>p.id===draft.id);
  const dirty=!saved||JSON.stringify(saved)!==JSON.stringify(draft);
  const useLogo=draft.watermark.kind==='png',logoHash=useLogo?draft.watermark.logo?.hash:undefined;
  useEffect(()=>{
    let live=true;setLogoPreview('');setLogoError('');
    if(logoHash)invoke<string>('deliveryLogoPreview',{hash:logoHash}).then(src=>{if(live)setLogoPreview(src);}).catch(e=>{if(live)setLogoError(messageOf(e));});
    return()=>{live=false;};
  },[logoHash,draft.watermark.logo]);
  const unavailable=(v:Variant)=>v.colorSpace==='adobeRgb'&&!shelf?.adobeRgbAvailable;
  const valid=!!(draft.name.trim()&&(useLogo?logoHash:draft.watermark.text.trim())&&[draft.proof,draft.final].every(v=>validExport(v)&&v.folder.trim()&&v.pattern.trim()&&!unavailable(v))&&
    draft.watermark.size>=2&&draft.watermark.size<=20&&draft.watermark.opacity>=10&&draft.watermark.opacity<=100);
  async function run(fn:()=>Promise<void>){
    if(inFlight.current)return;inFlight.current=true;lock(true);setError('');setNotice('');
    try{await fn();}catch(e){setError(messageOf(e));}finally{inFlight.current=false;setToken('');lock(false);}
  }
  async function save(){const next=await invoke<Shelf>('deliverySave',draft);const stored=adopt(next);if(!stored)throw new Error('无法读取刚保存的交付预设。');return stored;}
  function deliver(jobId?:string){void run(async()=>{
    if(canDeliver&&!canDeliver(jobId?await invoke<string[]>('deliveryPhotoIds',{jobId}):ids))return;
    const preset=jobId?undefined:dirty?await save():draft;
    const batchToken=requestId();setToken(batchToken);setOutcome(null);
    const result=jobId?await invoke<Outcome>('deliveryResume',{jobId,batchToken}):
      await invoke<Outcome|null>('deliveryStart',{ids,presetId:preset!.id,revision:preset!.revision,batchToken});
    setOutcome(result);setShelf(await invoke<Shelf>('deliveryPresets'));
  });}
  function updateMark(patch:Partial<Mark>){setDraft({...draft,watermark:{...draft.watermark,...patch}});setNotice('');}
  return <div className="delivery-panel">
    {shelf&&<><fieldset className="delivery-form" disabled={locked}>
      <div className="export-grid"><label>交付预设<select value={draft.id} onChange={e=>{
        const id=e.target.value;if(!id){const fresh=newDeliveryPreset();setDraft({...fresh,name:shelf.presets.some(p=>p.name===fresh.name)?'':fresh.name});setNotice('');setError('');return;}
        void run(async()=>{adopt(await invoke<Shelf>('deliverySelect',{presetId:id}));});
      }}><option value="">新建预设</option>{shelf.presets.map(p=><option key={p.id} value={p.id} translate="no">{p.name}</option>)}</select></label>
      <label>预设名称<input value={draft.name} maxLength={80} onChange={e=>{setDraft({...draft,name:e.target.value});setNotice('');}}/></label></div>
      <div className="delivery-variants">{(['proof','final'] as const).map(k=><VariantFields key={k+draft.id+draft.revision}
        title={k==='proof'?'带水印小样':'无水印成片'} value={draft[k]} change={v=>{setDraft({...draft,[k]:v});setNotice('');}} adobe={shelf.adobeRgbAvailable} example={example}/>)}</div>
      <p className="export-note">{'{name}'} 取原片名，{'{index}'} 取本批序号。重名自动追加编号。图像在 sRGB 工作空间处理，再转换至所选 ICC。</p>
      <fieldset className="delivery-variant"><legend>小样水印</legend><div className="export-grid delivery-mark">
        <label>水印类型<select value={useLogo?'png':'text'} onChange={e=>updateMark({kind:e.target.value as 'png'|'text'})}><option value="text">文字</option><option value="png">PNG 标志</option></select></label>
        {useLogo?<div className="delivery-logo"><button className="secondary small" onClick={()=>void run(async()=>{
          const logo=await invoke<Logo|null>('deliveryPickLogo');if(logo)updateMark({logo,kind:'png'});
        })}>{logoHash?'更换 PNG 标志':'选择 PNG 标志'}</button>{draft.watermark.logo&&<span translate="no">{draft.watermark.logo.name} · {draft.watermark.logo.width} × {draft.watermark.logo.height}</span>}
          {logoPreview&&<img src={logoPreview} alt="所选 PNG 标志"/>}{logoError&&<small className="field-error">{logoError}</small>}</div>:
          <label className="delivery-mark-text">水印文字<input value={draft.watermark.text} maxLength={96} onChange={e=>updateMark({text:e.target.value})}/></label>}
        <label>{useLogo?'图高':'字高'}（短边 %）<input type="number" min={2} max={20} value={Number.isFinite(draft.watermark.size)?draft.watermark.size:''} onChange={e=>updateMark({size:e.target.valueAsNumber})}/></label>
        <label>不透明度（%）<input type="number" min={10} max={100} value={Number.isFinite(draft.watermark.opacity)?draft.watermark.opacity:''} onChange={e=>updateMark({opacity:e.target.valueAsNumber})}/></label>
        {orientations.map(([k,label])=><label key={k}>{label}位置<select value={draft.watermark[k]} onChange={e=>updateMark({[k]:e.target.value})}>{positions.map(([v,n])=><option key={v} value={v}>{n}</option>)}</select></label>)}
      </div></fieldset></fieldset>
    {shelf.pending.length>0&&!token&&<div className="delivery-pending"><h3>待续交</h3>{shelf.pending.map(j=><div key={j.id}><span><b><span translate="no">{j.name}</span> · {j.total} 张</b><small translate="no">{j.root}</small></span><button className="secondary small" disabled={locked} onClick={()=>deliver(j.id)}>续交缺少的文件</button></div>)}</div>}
    </>}
    {token&&<BatchProgress key={token} token={token} delivery/>}
    {outcome&&<div className="delivery-outcome" role="status"><b>{outcome.pauseReason?'交付已暂停':outcome.cancelled?'已停止交付':'交付结果'}</b>
      {outcome.pauseReason&&<p>{outcome.pauseReason}</p>}
      <p>{outcome.pauseReason&&'上次交付记录中，'}{outcome.paired} 张已成套，{outcome.partial} 张已确认一份，{outcome.total-outcome.paired-outcome.partial} 张待确认。共记录 {outcome.count} 个完整文件。</p>
      <p className="delivery-path" translate="no">{outcome.root}</p>
      {!!outcome.paths.length&&<details><summary>查看文件</summary><ul>{outcome.paths.map(p=><li key={p} translate="no">{p}</li>)}</ul></details>}
      {!!outcome.errors.length&&<ul className="field-error">{outcome.errors.map((e,i)=><li key={i}>{e}</li>)}</ul>}
    </div>}
    {error&&<p className="field-error" role="alert">{error} <button className="secondary small" disabled={locked} onClick={()=>void run(async()=>{adopt(await invoke<Shelf>('deliveryPresets'));})}>重新读取预设</button></p>}
    {notice&&<p role="status" className="export-note">{notice}</p>}
    {shelf&&<div className="dialog-actions"><button className="secondary" disabled={locked||!dirty||!valid} onClick={()=>void run(async()=>{await save();setNotice('已保存交付预设');})}>保存预设</button>
      <button className="primary" disabled={locked||!valid||!ids.length} onClick={()=>deliver()}><Icon name="export" size={15}/>{dirty?'保存并交付':'双份交付'} {ids.length} 张</button></div>}
  </div>;
}

export function BatchDialog({photos,source,busy,apply,write,close,options=false,batchToken,delivery=false,canDeliver}:{
  photos:Photo[];source:Photo|null;busy:boolean;apply?:(ids:string[])=>void;write:(ids:string[],settings?:ExportSettings)=>void;close:()=>void;options?:boolean;batchToken?:string;delivery?:boolean;canDeliver?:(ids:string[])=>boolean;
}){
  const [ids,setIds]=useState<string[]>(photos.map(p=>p.id));
  const [mode,setMode]=useState<'export'|'apply'|'delivery'>('export');
  const [settings,setSettings]=useState<ExportSettings>(defaultExport),[deliveryBusy,setDeliveryBusy]=useState(false);
  const locked=busy||deliveryBusy,targets=mode==='apply'?ids.filter(id=>id!==source?.id):ids;
  const selected=<><div className="batch-select"><label><input type="checkbox" disabled={locked} checked={photos.length>0&&ids.length===photos.length} onChange={e=>setIds(e.target.checked?photos.map(p=>p.id):[])}/>全选</label><span>已选 {targets.length} 张</span></div>
    <div className="batch-list">{photos.map(p=><label key={p.id} className={p.id===source?.id&&mode==='apply'?'source':''}>
      <input type="checkbox" disabled={locked||(mode==='apply'&&p.id===source?.id)} checked={ids.includes(p.id)&&!(mode==='apply'&&p.id===source?.id)} onChange={e=>setIds(prev=>e.target.checked?[...prev,p.id]:prev.filter(x=>x!==p.id))}/>
      <Thumb photo={p}/><span translate="no">{p.name}<small>{formatOf(p)} · {p.width} × {p.height}</small></span>{mode==='apply'&&p.id===source?.id&&<b>来源</b>}</label>)}</div></>;
  return <Modal title="批量处理" close={locked?()=>{}:close} className={'batch-dialog'+(mode==='delivery'?' delivery-dialog':'')}>
    {(apply||delivery)&&<div className="dialog-tabs"><button className={mode==='export'?'on':''} onClick={()=>setMode('export')} disabled={locked}><Icon name="export" size={15}/>导出照片</button>
      {delivery&&<button className={mode==='delivery'?'on':''} disabled={locked} onClick={()=>setMode('delivery')}><Icon name="stack" size={15}/>双份交付</button>}
      {apply&&<button className={mode==='apply'?'on':''} onClick={()=>setMode('apply')} disabled={locked||!source}><Icon name="copy" size={15}/>同步参数</button>}</div>}
    {mode==='delivery'?<><details className="delivery-selection"><summary>本批照片 · 已选 {targets.length} 张</summary>{selected}</details>
      <DeliveryPanel ids={targets} example={photos.find(p=>ids.includes(p.id))?.name||(language()==='en'?'Photo.jpg':'照片.jpg')} locked={locked} lock={setDeliveryBusy} canDeliver={canDeliver}/></>:<>
      <p className="dialog-note">{mode==='export'?'将所选照片按各自已保存的参数导出到同一目录。':'将「'+source?.name+'」已保存的参数同步到所选照片。每张照片保留各自的锁定参数和历史。'}</p>{selected}
      {batchToken?<BatchProgress key={batchToken} token={batchToken}/>:options&&mode==='export'&&<ExportFields value={settings} change={setSettings} busy={busy} batch/>}
      <div className="dialog-actions"><button className="secondary" disabled={busy} onClick={close}>取消</button><button className="primary" disabled={busy||!targets.length||(options&&mode==='export'&&!validExport(settings))} onClick={()=>{if(mode==='apply')apply?.(targets);else write(targets,options?settings:undefined);}}>
        <Icon name={mode==='export'?'export':'copy'} size={15}/>{mode==='export'?'导出':'同步'} {targets.length} 张</button></div></>}
  </Modal>;
}
