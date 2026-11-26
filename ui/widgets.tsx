import React, {useEffect, useId, useLayoutEffect, useMemo, useRef, useState} from 'react';
import {Control, PhotoDocument, Preview, Values, displayValue, groupControls, groupName, manualGroup, quantize} from './model';
import {ControlVisit,useVisitPanel} from './adjusted';
import {Icon, IconName} from './icons';
import {AdvancedProps, CurveEditor, GradingEditor, HslEditor} from './advanced';
import {parameterSearch} from './parameter-search';

export function IconButton({icon, label, onClick, disabled, active, className = '', size = 16}: {
  icon: IconName; label: string; onClick: () => void; disabled?: boolean; active?: boolean; className?: string; size?: number;
}) {
  return <button type="button" className={'icon-button ' + className + (active ? ' active' : '')} title={label} aria-label={label}
    aria-pressed={active === undefined ? undefined : active} disabled={disabled} onClick={onClick}><Icon name={icon} size={size}/></button>;
}
export function Modal({title, children, close, className = ''}: {title: string; children: React.ReactNode; close: () => void; className?: string}) {
  const panel = useRef<HTMLDivElement>(null);
  const closeRef = useRef(close); closeRef.current = close;
  const heading = useId();
  useEffect(() => {
    const previous = document.activeElement as HTMLElement | null;
    const targets = () => Array.from(panel.current?.querySelectorAll<HTMLElement>('button:not(:disabled), input:not(:disabled), select:not(:disabled), textarea:not(:disabled), [tabindex="0"]') || []);
    const timer = window.setTimeout(() => (panel.current?.querySelector<HTMLElement>('[data-focus]') || targets()[0] || panel.current)?.focus(), 0);
    const trap = (event: KeyboardEvent) => {
      if (event.key === 'Escape') { event.stopImmediatePropagation(); event.preventDefault(); closeRef.current(); }
      if (event.key !== 'Tab') return;
      const choices = targets();
      if (!choices.length) { event.preventDefault(); return; }
      const first = choices[0], last = choices[choices.length - 1];
      if (event.shiftKey && document.activeElement === first) { event.preventDefault(); last.focus(); }
      else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first.focus(); }
    };
    document.addEventListener('keydown', trap, true);
    return () => {window.clearTimeout(timer); document.removeEventListener('keydown', trap, true); if (previous?.isConnected) previous.focus();};
  }, []);
  return <div className="scrim" onPointerDown={e => {if (e.target === e.currentTarget) close();}}>
    <div role="dialog" aria-modal="true" aria-labelledby={heading} tabIndex={-1} className={'dialog ' + className} ref={panel}>
      <div className="dialog-title"><h2 id={heading}>{title}</h2><IconButton label="关闭对话框" icon="close" onClick={close}/></div>
      {children}
    </div>
  </div>;
}
export function Histogram({frame, empty}: {frame: Preview | null; empty: boolean}) {
  const [open, setOpen] = useState(true);
  const bins = frame?.histogram;
  const valid = Array.isArray(bins) && bins.length > 1 && bins.every((n: number) => Number.isFinite(n) && n >= 0);
  const maximum = valid ? Math.max(1, ...bins) : 1;
  const outline = valid ? bins.map((v: number, i: number) => (i === 0 ? 'M' : 'L') + (i * 255 / (bins.length - 1)).toFixed(2) + ' ' + (50 - v / maximum * 47).toFixed(1)).join(' ') : '';
  return <section className={'hist-section' + (open ? '' : ' closed')}>
    <button className="section-trigger" aria-expanded={open} onClick={() => setOpen(!open)}><span>直方图</span><span className="section-detail">亮度 <Icon name="chevron" className={open ? '' : 'folded'} size={14}/></span></button>
    {open && <div className="hist-chart">
      <svg viewBox="0 0 256 52" role="img" aria-label={valid ? '当前预览的亮度分布，横轴从阴影到高光' : '暂无直方图'} preserveAspectRatio="none">
        <path className="grid-line" d="M1 1v50 M64 1v50 M128 1v50 M192 1v50 M255 1v50 M1 26h254 M1 50h254"/>
        {valid && <><path className="hist-fill" d={outline + ' L255 50 L0 50Z'}/><path className="hist-outline" d={outline}/></>}
      </svg>
      {!valid && <span className="hist-empty">{empty ? '导入后显示' : '正在计算…'}</span>}
    </div>}
  </section>;
}

function groupIcon(label: string): IconName {
  if (/光|light|tone|basic|基础/i.test(label)) return 'sun';
  if (/HSL|曲线|curve/i.test(label)) return 'channels';
  if (/色|color|白平衡/i.test(label)) return 'palette';
  if (/局部|蒙版|人物|mask|portrait/i.test(label)) return 'brush';
  if (/细节|detail/i.test(label)) return 'detail';
  return 'sliders';
}
function SliderRow({control, value, committed, locked, disabled, version, draft, finish, cancel, more}: {
  control: Control; value: number; committed: number; locked: boolean; disabled: boolean; version:string;
  draft: (v: number) => void; finish: (v: number) => void; cancel: () => void; more: () => void;
}) {
  const numberInput=useRef<HTMLInputElement>(null);
  const editingNumber=useRef<string|null>(null);
  const active = useRef<string|null>(null);
  const pointer=useRef<number|null>(null);
  const id = useId();
  const formatted=displayValue(value,control);
  useLayoutEffect(()=>{
    if(editingNumber.current!==version && numberInput.current)numberInput.current.value=formatted;
  },[formatted,version]);
  const ratio = (value - control.min) / (control.max - control.min || 1) * 100;
  const middle = (control.initial - control.min) / (control.max - control.min || 1) * 100;
  const enterNumber = (input: HTMLInputElement) => {
    if(editingNumber.current!==version){editingNumber.current=null;return;}
    editingNumber.current=null;
    const raw=input.value;
    if (raw.trim() === '' || !Number.isFinite(Number(raw))) { input.value=displayValue(committed,control);cancel(); return; }
    const n = quantize(Number(raw), control);
    input.value=displayValue(n,control);
    if (n !== committed) finish(n); else cancel();
  };
  const commitRange = (raw: string) => {
    const owner=active.current; active.current=null;
    if(owner!==version)return;
    const n = quantize(Number(raw), control);
    if (n !== committed) finish(n); else cancel();
  };
  if(control.key.endsWith('_backgroundOutput'))return <div className="slider-row" data-parameter={control.key}>
    <div className="slider-caption"><label htmlFor={id}>背景替换</label><button className="param-more" type="button" onClick={more}
      disabled={disabled} aria-label="背景替换选项"><Icon name={locked?'lock':'dots'} size={13}/></button></div>
    <select id={id} value={Math.min(value,2)} disabled={disabled||locked} onChange={e=>finish(Number(e.target.value)===2?0xffffff+2:Number(e.target.value))}>
      <option value={0}>保留原图</option><option value={1}>透明背景</option><option value={2}>纯色背景</option></select>
    {value>=2&&<input type="color" aria-label="替换背景颜色" disabled={disabled||locked} value={'#'+(value-2).toString(16).padStart(6,'0')}
      onChange={e=>finish(parseInt(e.target.value.slice(1),16)+2)}/>}
  </div>;
  return <div className={'slider-row' + (locked ? ' locked' : '') + (committed !== control.initial ? ' modified' : '')} data-parameter={control.key}>
    <div className="slider-caption"><label htmlFor={id}>{control.label}</label>
      <button className={'param-more' + (locked ? ' is-locked' : '')} type="button" onClick={more}
        disabled={disabled} aria-label={control.label + '选项'} title={control.label + '选项'}><Icon name={locked ? 'lock' : 'dots'} size={13}/></button>
      <input ref={numberInput} className="number-entry" type="text" inputMode="decimal" aria-label={control.label + '数值'} defaultValue={formatted}
        disabled={disabled || locked} onFocus={e => {editingNumber.current=version;e.currentTarget.select();}}
        onKeyDown={e => {
          if(e.nativeEvent.isComposing)return;
          if(e.key==='Enter'){e.preventDefault();e.currentTarget.blur();}
          if(e.key==='Escape'){editingNumber.current=null;e.currentTarget.value=displayValue(committed,control);cancel();e.currentTarget.blur();}
        }}
        onBlur={e=>enterNumber(e.currentTarget)}/>
    </div>
    <input id={id} aria-label={control.label} type="range" min={control.min} max={control.max} step={control.step} value={value}
      disabled={disabled || locked} style={{'--pos': ratio + '%','--low': Math.min(ratio, middle) + '%','--high': Math.max(ratio, middle) + '%','--mid': middle + '%'} as React.CSSProperties}
      onPointerDown={e => {pointer.current=e.pointerId; active.current=version; e.currentTarget.setPointerCapture(e.pointerId);}}
      onChange={e => {if(active.current!==null && active.current!==version)return; active.current=version; draft(Number(e.target.value));}}
      onKeyUp={e => { if (['ArrowLeft','ArrowRight','ArrowDown','ArrowUp','Home','End','PageDown','PageUp'].includes(e.key)) commitRange(e.currentTarget.value); }}
      onPointerUp={e => {pointer.current=null; commitRange(e.currentTarget.value);}}
      onPointerCancel={() => {const owner=active.current; pointer.current=null; active.current=null; if(owner===version)cancel();}}
      onLostPointerCapture={e=>{if(pointer.current===e.pointerId){pointer.current=null; const owner=active.current; active.current=null; if(owner===version)cancel();}}}
      onBlur={e => {if(pointer.current===null)commitRange(e.currentTarget.value);}}/>
  </div>;
}
export function ParameterPanel({controls, doc, previewValues, busy, compact, draft, commit, draftMany, commitMany, importLut, cancel, more, openFaceLight,visit}: {
  controls: Control[]; doc: PhotoDocument | null; previewValues?: Values; busy: boolean; compact?:boolean; visit?:ControlVisit;
  draft: (c: Control, v: number) => void; commit: (c: Control, v: number) => void; cancel: () => void; more: (c: Control) => void;
  draftMany: (patch:Values)=>void; commitMany:(patch:Values,label:string)=>void; importLut?:()=>void; openFaceLight?:()=>void;
}) {
  const [expanded, setExpanded] = useState<Record<string, boolean>>({});
  const [query, setQuery] = useState('');
  const [searching, setSearching] = useState(false);
  const panel=useVisitPanel<HTMLDivElement>(visit);
  const groups = useMemo(()=>groupControls(controls,manualGroup),[controls]);
  const panelValues=useMemo(()=>previewValues?{...doc?.parameters,...previewValues}:doc?.parameters||{},[doc?.parameters,previewValues]);
  const term = query.trim().toLowerCase();
  const search=parameterSearch(term);
  const visibleGroups=term?groups.map(([group,items]):[string,Control[]]=>[group,items.filter(c=>search.matches(c,group))]):groups;
  const faceResult=!!openFaceLight&&search.faceLight;
  const version=doc?doc.photoId+'@'+doc.revision:'';
  const rows=(items:Control[])=>items.map(c=><SliderRow key={c.key} control={c}
    value={previewValues?.[c.key] ?? doc?.parameters[c.key] ?? c.initial} committed={doc?.parameters[c.key] ?? c.initial}
    locked={doc?.locks.includes(c.key) || false} disabled={!doc || busy} version={version} draft={v=>draft(c,v)} finish={v=>commit(c,v)} cancel={cancel} more={()=>more(c)}/>);
  if(compact)return <div ref={panel} className="parameter-panel compact">{rows(controls)}</div>;
  return <div ref={panel} className="parameter-panel">
    <div className="section-heading controls-heading"><h2>{controls.length>0&&controls.every(c=>c.group==='人体美型')?'手动微调':'手动调色'}</h2><IconButton label="搜索参数" icon="search" active={searching} onClick={() => {setSearching(v => !v); setQuery('');}}/></div>
    {searching && <div className="inline-search"><Icon name="search" size={14}/><input autoFocus placeholder="搜参数或问题，如“偏黄”" aria-label="搜索参数" value={query} onChange={e => setQuery(e.target.value)}/></div>}
    {faceResult&&<button type="button" className="frame-open" disabled={!doc||busy} onClick={openFaceLight}>
      <Icon name="target" size={15}/>只提亮选中的人脸<Icon name="right" size={13}/></button>}
    {visibleGroups.map(([group, shown], index) => {
      if (!shown.length) return null;
      const target=shown.find(c=>c.key===visit?.key);
      const open = !!term || (expanded[group] ?? (index === 0 || group==='色彩'||!!target));
      const changed = doc && shown.filter(c => (doc.parameters[c.key] ?? c.initial) !== c.initial).length;
      const advanced:AdvancedProps={list:shown,initialKey:target?.key,values:panelValues,locks:doc?.locks||[],disabled:!doc||busy,version,rows,draft:draftMany,commit:commitMany,cancel};
      return <section className="control-group" key={group}>
        <button className="group-head" onClick={() => setExpanded(s => ({...s, [group]: !open}))} aria-expanded={open}>
          <Icon name={groupIcon(group)} size={16}/><span>{groupName(group)}</span>
          {!!changed && <span className="change-count" title={changed + ' 项已调整'}>{changed}</span>}
          <Icon name="chevron" size={14} className={'group-chevron ' + (open ? '' : 'folded')}/>
        </button>
        {open && <div className="group-body">
          {importLut && shown.some(c=>c.key==='lut_strength')&&<button className="secondary small lut-import" disabled={!doc||busy} onClick={importLut}><Icon name="import" size={13}/>导入 .cube LUT</button>}
          {!term&&group==='RGB 曲线'?<CurveEditor {...advanced}/>:!term&&group==='HSL 八色'?<HslEditor {...advanced}/>:!term&&group==='色彩分级'?<GradingEditor {...advanced}/>:rows(shown)}</div>}
      </section>;
    })}
    {term && !faceResult && !visibleGroups.some(([,list])=>list.length) && <p className="muted empty-line">没有匹配的参数</p>}
  </div>;
}
