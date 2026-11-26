import React,{useEffect,useRef,useState} from 'react';
import {Modal} from './widgets';
import {ExportMeasure,invoke,messageOf,Preview,requestId} from './model';
import {MatteFilter,mayHaveAlpha} from './transparency';
import {LastOutput,PositionOutput,QualityProof} from './share-tools';

export type ExportSettings={format:'jpeg'|'png'|'tiff';longEdge:number;quality:number;maxBytes?:number;minEdge?:number;matte?:string;measuredBytes?:number;
  layout?:'pad'|'crop';outputWidth?:number;outputHeight?:number;positionX?:number;positionY?:number;background?:string};
export const defaultExport:ExportSettings={format:'jpeg',longEdge:0,quality:95};
export const validExport=(s:ExportSettings)=>Number.isInteger(s.longEdge)&&s.longEdge>=0&&s.longEdge<=65535&&Number.isInteger(s.quality)&&s.quality>=1&&s.quality<=100&&
  (!s.layout||[s.outputWidth,s.outputHeight].every(v=>Number.isInteger(v)&&v!>0&&v!<=65535)&&s.outputWidth!*s.outputHeight!<=80000000)&&
  (s.format!=='jpeg'||(Number.isInteger(s.maxBytes??0)&&(s.maxBytes??0)>=0&&(s.maxBytes??0)<=500000000&&/^#[a-f\d]{6}$/i.test(s.matte||'#ffffff')));
export const exportSize=(n:number)=>n>=1000000?(n/1000000).toFixed(2)+' MB':(n/1000).toFixed(1)+' KB';
export function ExportFields({value,change,busy,width,height,note=true,batch=false}:{value:ExportSettings;change:(v:ExportSettings)=>void;busy:boolean;width?:number;height?:number;note?:boolean;batch?:boolean}){
  const [manual,setCustom]=useState(false);
  const [customBudget,setCustomBudget]=useState(false);
  const [budgetText,setBudgetText]=useState(()=>value.maxBytes?String(value.maxBytes/1000000):'');
  const [customMatte,setCustomMatte]=useState(false);
  const matte=value.matte||'#ffffff',showColor=customMatte||!['#ffffff','#000000'].includes(matte);
  const budget=value.maxBytes??0;
  const customLimit=customBudget||![0,500000,1000000,2000000,5000000].includes(budget);
  const custom=manual||![0,1080,2048,4096].includes(value.longEdge);
  const size=width&&height&&validExport(value)?Math.min(1,(value.longEdge||Math.max(width,height))/Math.max(width,height)):null;
  return <fieldset className="export-fields" disabled={busy}><div className="export-grid">
    <label>文件格式<select aria-label="导出格式" value={value.format} onChange={e=>change({...value,format:e.target.value as ExportSettings['format']})}>
      <option value="jpeg">JPEG</option><option value="png">PNG · 16 位</option><option value="tiff">TIFF · 16 位</option></select></label>
    <label>导出尺寸<select aria-label="导出尺寸" value={value.layout?'exact':custom?'custom':value.longEdge} onChange={e=>{
      const selected=e.target.value;setCustom(selected==='custom');
      if(selected==='exact')change({...value,longEdge:0,layout:'crop',outputWidth:value.outputWidth||300,outputHeight:value.outputHeight||400});
      else change({...value,layout:undefined,longEdge:selected==='custom'?1600:Number(selected)});
    }}><option value={0}>构图后的原尺寸</option>{[1080,2048,4096].map(n=><option key={n} value={n}>长边至多 {n} px</option>)}<option value="custom">自定义长边</option>{width&&height&&!batch?<option value="exact">指定宽高</option>:null}</select></label>
    {value.layout&&<>{(['outputWidth','outputHeight'] as const).map((key,i)=><label key={key}>成品{i?'高':'宽'}（像素）<input type="number" min={1} max={65535} aria-label={i?'成品高度':'成品宽度'}
      value={Number.isFinite(value[key])?value[key]:''} onChange={e=>change({...value,[key]:e.target.valueAsNumber})}/></label>)}
      <label>画面放置<select aria-label="画面放置" value={value.layout} onChange={e=>change({...value,layout:e.target.value as ExportSettings['layout']})}><option value="crop">裁切填满</option><option value="pad">保留整图并加边</option></select></label>
      {value.layout==='pad'&&<><label>加边背景<select aria-label="加边背景" value={!value.background||value.background==='transparent'?'transparent':'solid'} onChange={e=>change({...value,background:e.target.value==='solid'?'#ffffff':'transparent'})}>
        <option value="transparent">{value.format==='jpeg'?'使用 JPEG 底色':'透明'}</option><option value="solid">纯色</option></select></label>
        {value.background&&value.background!=='transparent'&&<label>边框颜色<input type="color" aria-label="边框颜色" value={value.background} onChange={e=>change({...value,background:e.target.value})}/></label>}</>}</>}
    {custom&&!value.layout&&<label>最长边（像素）<input type="number" aria-label="最长边像素" min={1} max={65535} step={1} value={Number.isFinite(value.longEdge)?value.longEdge:''}
      onChange={e=>change({...value,longEdge:e.target.value?Math.max(1,e.target.valueAsNumber):NaN})}/></label>}
    {value.format==='jpeg'&&<><label>{budget?'JPEG 最高质量':'JPEG 质量'}<input aria-label="JPEG 质量" type="number" min={1} max={100} step={1} value={Number.isFinite(value.quality)?value.quality:''}
      onChange={e=>change({...value,quality:e.target.valueAsNumber})}/></label>
      {value.layout?<label>体积上限（字节，0 为不限）<input type="number" aria-label="体积上限字节" min={0} max={500000000} step={1} value={Number.isFinite(budget)?budget:''} onChange={e=>change({...value,maxBytes:e.target.value===''?NaN:e.target.valueAsNumber})}/></label>:
      <label>文件体积上限<select aria-label="JPEG 体积上限" value={customLimit?'custom':budget} onChange={e=>{
        const custom=e.target.value==='custom';setCustomBudget(custom);if(custom)setBudgetText('0.25');change({...value,maxBytes:custom?250000:Number(e.target.value)});
      }}><option value={0}>不限体积</option><option value={500000}>500 KB</option>{[1,2,5].map(n=><option key={n} value={n*1000000}>{n} MB</option>)}<option value="custom">自定义上限</option></select></label>}
      {customLimit&&!value.layout&&<label>上限（0.001 ～ 500 MB）<input type="number" aria-label="自定义上限 MB" min={.001} max={500} step={.001} value={budgetText}
        onChange={e=>{setBudgetText(e.target.value);change({...value,maxBytes:e.target.value!==''&&e.target.valueAsNumber>=.001?Math.round(e.target.valueAsNumber*1000000):NaN});}}/></label>}
      {batch&&budget>0&&<label>原尺寸超限时<select aria-label="超限时允许缩小" value={value.minEdge??0} onChange={e=>change({...value,minEdge:Number(e.target.value)})}>
        <option value={0}>保留尺寸并报告</option>{[2048,1080,640,320].map(n=><option key={n} value={n}>可缩至长边 {n} px</option>)}</select></label>}
      <label>JPEG 透明处底色<select aria-label="JPEG 底色" value={showColor?'custom':matte} onChange={e=>{
        const custom=e.target.value==='custom';setCustomMatte(custom);change({...value,matte:custom?matte:e.target.value});
      }}><option value="#ffffff">白色</option><option value="#000000">黑色</option><option value="custom">自选颜色</option></select></label>
      {showColor&&<label>自选底色<input type="color" aria-label="自选 JPEG 底色" value={matte} onChange={e=>change({...value,matte:e.target.value})}/></label>}</>}
  </div>{note&&<p className="export-note">{value.layout?(validExport(value)?'成品 '+value.outputWidth+' × '+value.outputHeight+' 像素。':'宽高须为 1–65,535，总像素须在 8000 万以内。'):size&&width&&height?'成品 '+Math.max(1,Math.round(size*width))+' × '+Math.max(1,Math.round(size*height))+' 像素。':'按各图构图比例缩小。'}导出时移除拍摄信息和 GPS 位置，嵌入 sRGB 色彩配置。</p>}</fieldset>;
}
export function ExportDialog({id,revision,name,width,height,cutout,busy,write,close}:{id:string;revision:number;name:string;width:number;height:number;cutout?:boolean;busy:boolean;write:(v:ExportSettings)=>void;close:()=>void}){
  const [options,setOptions]=useState<ExportSettings>(defaultExport);
  const [plan,setPlan]=useState<ExportMeasure[]>(),[choice,setChoice]=useState(-1),[error,setError]=useState(''),[measuring,setMeasuring]=useState(false);
  const [proof,setProof]=useState('');const active=useRef('');
  const [proofOpen,setProofOpen]=useState(false),[point,setPoint]=useState({x:.5,y:.5});
  const limited=options.format==='jpeg'&&!!options.maxBytes;
  useEffect(()=>{
    setPlan(undefined);setChoice(-1);setMeasuring(false);setError('');
    return ()=>{if(active.current){void invoke('cancelJpegPlan',{ticket:active.current}).catch(()=>{});active.current='';}};
  },[id,revision]);
  useEffect(()=>{
    let live=true;setProof('');
    void invoke<Preview>('preview',{id,revision,maxEdge:480}).then(p=>{if(live)setProof(p.image);}).catch(()=>{});
    return ()=>{live=false;};
  },[id,revision,name]);
  function change(v:ExportSettings){setOptions(v);setPlan(undefined);setChoice(-1);setError('');}
  async function measure(){
    const ticket=requestId();active.current=ticket;setMeasuring(true);setError('');
    try{
      const fits=await invoke<ExportMeasure[]>('jpegExportPlan',{...options,id,revision,ticket});
      if(active.current!==ticket)return;
      setPlan(fits);
      const requested=Math.min(Math.max(width,height),options.longEdge||Math.max(width,height));
      setChoice(fits.length&&(options.layout||Math.max(fits[0].width,fits[0].height)===requested)?0:-1);
    }catch(e){if(active.current===ticket)setError(messageOf(e));}
    finally{if(active.current===ticket){active.current='';setMeasuring(false);}}
  }
  const picked=choice>=0?plan?.[choice]:undefined;
  const selected=limited&&picked?{...options,longEdge:options.layout?0:Math.max(picked.width,picked.height),quality:picked.quality,measuredBytes:picked.bytes,minEdge:0}:options;
  return <Modal title="导出设置" close={busy?()=>{}:close}><p className="dialog-note">{name}</p>
    <LastOutput id={id} revision={revision}/>
    <ExportFields value={options} change={change} busy={busy||measuring} width={width} height={height}/>
    {options.layout&&validExport(options)&&<fieldset className="export-fields" disabled={busy||measuring}><PositionOutput value={options} change={change} image={proof} width={width} height={height}/></fieldset>}
    {options.format==='jpeg'&&proof&&!options.layout&&(cutout||mayHaveAlpha(name))&&<div className="export-matte-proof"><MatteFilter id="export-paper" color={options.matte||'#ffffff'}/><img src={proof} alt="JPEG 底色预览"/></div>}
    {limited&&<div className="export-budget"><button className="secondary small" disabled={busy||measuring||!validExport(options)} onClick={()=>void measure()}>{measuring?'正在试算…':'试算可用尺寸'}</button>
      <small>按带色彩配置的实际 JPEG 计算，1 MB = 1,000,000 字节。</small>
      {plan&&<div role="group" aria-label="可用导出尺寸">{plan.length===0?<p role="status">{options.layout?'指定宽高在最低 JPEG 质量下仍超过上限，请提高上限或修改宽高。':'这个上限低于可生成的最小 JPEG，请提高体积上限。'}</p>:<>
        {choice<0&&<p>当前尺寸超过上限，请选择较小尺寸。</p>}
        {plan.map((v,i)=><label key={i}><input type="radio" name="jpeg-size" checked={choice===i} onChange={()=>setChoice(i)}/>
          {v.width} × {v.height} px · 质量 {v.quality} · {v.bytes.toLocaleString()} 字节</label>)}
        {picked&&picked.quality<40&&<p>这个尺寸需要较强压缩。可检查小字和纹理，或提高体积上限。</p>}
        </>}</div>}
      {error&&<p role="alert" className="field-error">{error}</p>}</div>}
    {options.format==='jpeg'&&(!limited||picked)&&validExport(options)&&<details className="export-quality-proof" open={proofOpen} onToggle={e=>setProofOpen(e.currentTarget.open)}><summary>选择位置检查 JPEG 细节（100%）</summary>
      {proofOpen&&<QualityProof id={id} revision={revision} options={selected} width={width} height={height} point={point} select={setPoint}/>}</details>}
    <div className="dialog-actions"><button className="secondary" disabled={busy} onClick={close}>取消</button>
      <button className="primary" disabled={busy||measuring||!validExport(options)||limited&&!picked} onClick={()=>write(selected)}>选择保存位置</button></div>
  </Modal>;
}

type BatchStatus={active:boolean;started?:boolean;total?:number;written?:number;failed?:number;file?:string;cancelling?:boolean};
export function BatchProgress({token,delivery=false}:{token:string;delivery?:boolean}){
  const [progress,setProgress]=useState<BatchStatus>({active:false});
  const [cancelRequest,setCancelRequest]=useState(false);
  const [error,setError]=useState('');
  useEffect(()=>{
    let live=true,timer:number|undefined;
    async function poll(){
      try{const next=await invoke<BatchStatus>('batchExportStatus',{batchToken:token});if(live){setProgress(next);setError('');}}
      catch(e){if(live)setError(messageOf(e));}
      finally{if(live)timer=window.setTimeout(poll,300);}
    }
    void poll();return()=>{live=false;window.clearTimeout(timer);};
  },[token]);
  const written=progress.written??0,failed=progress.failed??0,total=progress.total??0;
  return <div className="batch-progress" role="status">
    <p>{progress.cancelling?'正在取消本批导出…':progress.started?'正在导出 '+progress.file:delivery?'正在准备交付…':'等待选择导出目录…'}</p>
    <progress value={written+failed} max={Math.max(1,total)} aria-label="批量导出进度"/>
    <div className="frame-actions"><span>{written} {delivery?'张已成套':'张已写入'}{failed?' · '+failed+(delivery?' 张待续交':' 张失败'):''} / 共 {total} 张</span>
      <button className="secondary small" disabled={!progress.started||progress.cancelling||cancelRequest} onClick={async()=>{
        setCancelRequest(true);try{setProgress(await invoke<BatchStatus>('cancelBatchExport',{batchToken:token}));}
        catch(e){setError(messageOf(e));}finally{setCancelRequest(false);}
      }}>停止导出</button></div>
    {error&&<p className="field-error">{error}</p>}
  </div>;
}
