import React,{useEffect,useState} from 'react';
import type {LocalEditor} from './local';
import type {Args,BodyPerson} from './model';
import {usePeople} from './people';
import {RangePanel} from './range-mask';
import {categoryName} from './mask-detector';

export type ManageMask=(data:Args)=>Promise<number|null>;

export function MaskList({local,busy,manage,previewReady,replacingBackground}:{local:LocalEditor;people:BodyPerson[];busy:boolean;manage:ManageMask;previewReady:boolean;replacingBackground?:boolean}){
  const people=usePeople(),person=people.active;
  const missingBody=!!person&&!person.body?.available.length;
  const [renaming,setRenaming]=useState(false),[name,setName]=useState('');
  const selected=local.masks.find(m=>m.slot===local.slot);
  const [garment,setGarment]=useState<'all'|'upper'|'lower'>('all');
  const [automaticLine,setAutomaticLine]=useState(true),[line,setLine]=useState(58);
  const [adjustingLine,setAdjustingLine]=useState(false);
  const job=local.category;
  const locked=busy||!local.ready||local.dirty||!!job&&!job.error;
  const box=person?.body?.box,showLine=adjustingLine&&!locked&&!automaticLine&&garment!=='all';
  useEffect(()=>{
    if(showLine&&box)local.setGarmentGuide({left:box.x,top:box.y+box.h*line/100,width:box.w});
    else local.setGarmentGuide(null);
    return ()=>local.setGarmentGuide(null);
  },[showLine,line,box?.x,box?.y,box?.w,box?.h,local.setGarmentGuide]);
  useEffect(()=>{setRenaming(false);setName(selected?.name??'');},[selected?.slot,selected?.name]);
  async function apply(data:Args){
    const slot=await manage(data);
    if(slot!==null){
      local.chooseMask(slot);
      if(data.operation==='add'&&['brush','lens','fabric'].includes(String(data.kind))){
        local.setTool('paint');
        if(data.kind==='lens'){local.setRadius(.025);local.setVisible(true);}
        if(data.kind==='fabric')local.setVisible(true);
      }
      setRenaming(false);
    }
  }
  if(local.range){
    const range=local.range,saved=local.savedRange?.range;
    const changed=range.creating||!saved||Object.entries(saved).some(([key,value])=>range.range[key as keyof typeof saved]!==value);
    return <RangePanel job={range} busy={busy} visible={local.visible} canApply={changed&&previewReady}
      change={local.changeRange} cancel={local.cancelRange} apply={()=>void apply({operation:range.creating?'add':'range',maskSlot:local.slot,kind:range.kind,range:range.range,token:range.token})}/>;
  }
  if(local.gradient){
    const {kind,geometry,creating}=local.gradient;
    return <div className="mask-list gradient-editor"><h3>{kind==='linear'?'线性渐变':'径向渐变'}</h3>
      <p>{kind==='linear'?'从照片上拖出一道渐变。起点一侧覆盖最强，终点一侧最弱。可拖动两端的圆点。':
        '从中心向外拖出椭圆，横、纵两个圆点可分别调宽和调高。拖动中心圆点可移动选区。'}</p>
      <div className="brush-settings"><label>羽化 <span>{Math.round(geometry.feather*100)}%</span>
        <input aria-label="渐变羽化" type="range" min="0" max="100" step="1" disabled={busy} value={Math.round(geometry.feather*100)}
          onChange={e=>local.changeGradient({...geometry,feather:Number(e.target.value)/100})}/></label></div>
      <div className="markings-row"><label><input type="checkbox" checked={geometry.invert} disabled={busy}
        onChange={e=>local.changeGradient({...geometry,invert:e.target.checked})}/>反向</label>
      </div>
      <div className="segment-actions"><button className="primary small" disabled={busy||!local.gradientChanged||!previewReady}
        onClick={()=>void apply({operation:creating?'add':'gradient',maskSlot:local.slot,kind,geometry})}>应用渐变</button>
        <button className="secondary small" disabled={busy} onClick={local.cancelGradient}>取消渐变</button></div>
    </div>;
  }
  return <div className="mask-list">
    <label>当前蒙版 <select aria-label="当前蒙版" value={local.slot} disabled={locked||local.masks.length===0} onChange={e=>local.chooseMask(Number(e.target.value))}>
      {!local.masks.length&&<option value={-1}>尚无蒙版</option>}
      {local.masks.map(m=><option key={m.slot} value={m.slot}>{m.name}</option>)}</select></label>
    <div className="segment-actions">
      <button className="secondary small" title="新建手绘蒙版" disabled={locked} onClick={()=>void apply({operation:'add',kind:'brush'})}>画一块</button>
      <button className="secondary small" title="创建线性渐变" disabled={locked||local.masks.filter(m=>m.slot>0).length>=7} onClick={()=>local.startGradient('linear')}>拉一道渐变</button>
      <button className="secondary small" title="创建径向渐变" disabled={locked||local.masks.filter(m=>m.slot>0).length>=7} onClick={()=>local.startGradient('radial')}>圈一块</button>
    </div>
    <div className="segment-actions">
      <button className="secondary small" disabled={locked||local.masks.filter(m=>m.slot>0).length>=7} onClick={()=>local.startRange('colorRange')}>按颜色选择</button>
      <button className="secondary small" disabled={locked||local.masks.filter(m=>m.slot>0).length>=7} onClick={()=>local.startRange('luminanceRange')}>按亮度选择</button>
    </div>
    <div className="segment-actions">
      <button className="secondary small" disabled={locked||local.masks.filter(m=>m.slot>0).length>=7} onClick={()=>void apply({operation:'add',kind:'lens'})}>涂镜片反光</button>
      <button className="secondary small" disabled={locked||local.masks.filter(m=>m.slot>0).length>=7} onClick={()=>void apply({operation:'add',kind:'fabric'})}>涂精细衣料</button>
    </div>
    <div className="segment-actions">
      {local.savedGradient&&<button className="secondary small" disabled={locked} onClick={()=>local.startGradient(local.savedGradient!.kind,false)}>编辑渐变形状</button>}
      {local.savedRange&&<button className="secondary small" disabled={locked} onClick={()=>local.startRange(local.savedRange!.kind,false)}>编辑选择范围</button>}
      <button className="secondary small" disabled={locked||!selected} onClick={()=>setRenaming(v=>!v)}>重命名</button>
      <button className="secondary small" disabled={locked||!selected} onClick={()=>void apply({operation:'remove',maskSlot:local.slot})}>移除蒙版</button>
    </div>
    {renaming&&<form className="segment-actions" onSubmit={e=>{e.preventDefault();void apply({operation:'rename',maskSlot:local.slot,name:name.trim()});}}>
      <input aria-label="蒙版名称" value={name} maxLength={40} onChange={e=>setName(e.target.value)} autoFocus/>
      <button className="secondary small" disabled={locked||!name.trim()||name.trim()===selected?.name}>保存名称</button>
    </form>}
    {people.all.length>0?<label className="mask-scope">识别范围 <select aria-label="识别范围" value={person?.index??-1} disabled={locked}
      onChange={e=>people.choose(Number(e.target.value))}>
      <option value={-1}>整张照片</option>{people.visible.map(p=><option key={p.index} value={p.index}>人物 {p.index+1}{p.body?.available.length?'':'（需定位身体）'}</option>)}</select></label>:
      <p>要单独选合照中的一个人，请先在「人体美型」识别人物。</p>}
    {missingBody&&<p>该人物尚无可靠的人体轮廓。可框选补检，或新建手绘蒙版。</p>}
    {person?.body?.available.length?<div className="garment-division">
      <label>衣物范围 <select value={garment} disabled={locked} onChange={e=>setGarment(e.target.value as typeof garment)}>
        <option value="all">全部衣服</option><option value="upper">上装</option><option value="lower">下装</option></select></label>
      {garment!=='all'&&<label><input type="checkbox" checked={automaticLine} disabled={locked} onChange={e=>setAutomaticLine(e.target.checked)}/>按髋部定位</label>}
      {garment!=='all'&&!automaticLine&&<label>分界距人物顶部 {line}% <input aria-label="衣物分界" type="range" min="5" max="95" value={line} disabled={locked}
        onFocus={()=>setAdjustingLine(true)} onBlur={()=>setAdjustingLine(false)} onChange={e=>setLine(Number(e.target.value))}/></label>}
    </div>:null}
    <div className="segment-actions">
      <button className="secondary small" disabled={locked||missingBody} onClick={()=>void local.analyzeMask('hair',person?.body?.index)}>选择头发</button>
      <button className="secondary small" disabled={locked||missingBody} onClick={()=>void local.analyzeMask('clothes',person?.body?.index,
        person&&garment!=='all'?garment:undefined,person&&garment!=='all'&&!automaticLine?line/100:undefined)}>
        {person&&garment!=='all'?(garment==='upper'?'选择上装':'选择下装'):'选择衣服'}</button>
      <button className="secondary small" disabled={locked||!person?.body?.available.length} title="先识别并选中一个人物，再以此人的脸为肤色参照" onClick={()=>void local.analyzeMask('skin',person?.body?.index)}>选择人物肤色</button>
      <button className="secondary small" disabled={locked} title="选中人物以外的背景，可在棚拍照片上擦除道具、保留背景布" onClick={()=>void local.analyzeMask('backdrop')}>选择背景</button>
    </div>
    {selected?.kind==='backdrop'&&!job&&<><p>背景替换可选透明或纯色。用小画笔擦回发梢、眼镜腿，补涂指缝里的背景。导出 PNG 可保留透明边缘。</p>
      <div className="segment-actions"><button className="secondary small" disabled={busy} onClick={()=>{local.setTool('erase');local.setVisible(true);}}>{replacingBackground?'擦回前景':'保留清晰'}</button>
        <button className="secondary small" disabled={busy} onClick={()=>{local.setTool('paint');local.setVisible(true);}}>{replacingBackground?'补涂背景':'补虚化'}</button></div></>}
    {selected?.kind==='skin'&&!job&&<p>以所选人物的脸为肤色参照。脸部太暗或被遮挡时，可改用局部色温。</p>}
    {selected?.kind==='lens'&&!job&&<p>只涂镜片上仍能看见眼睛的浅色反光，再调「淡化镜片反光」。用小笔擦出虹膜和镜框，保留眼神光。</p>}
    {selected?.kind==='fabric'&&!job&&<p>涂出要淡化的衣褶，调节细褶或粗褶。织纹与亮片保护初始为 100%，可擦掉需要完整保留的花饰。</p>}
    {selected?.kind==='clothes'&&!job&&<p>婚纱、蕾丝和钉珠衣物可调高「保护织纹与亮片」。</p>}
    {job&&(job.error?<p role="alert" className="field-error">{job.error}</p>:<div role="status"><p>{job.alpha?'画布显示识别范围。应用后可用画笔或擦除修边。':'正在本机识别'+categoryName[job.kind]+'…'}</p>
      <div className="segment-actions">{job.alpha&&<button className="primary small" disabled={busy} onClick={()=>void apply({operation:'add',kind:job.kind,token:job.token})}>应用选区</button>}
        <button className="secondary small" disabled={busy} onClick={local.cancelMask}>取消识别</button></div></div>)}
  </div>;
}
