import React, {useEffect, useLayoutEffect, useMemo, useRef, useState} from 'react';
import {Photo, Project, Recipe, Snapshot, baseName, folderOf, formatOf} from './model';
import {PresetSwatches} from './preset-swatches';
import {Icon} from './icons';
import {IconButton} from './widgets';
import {Thumb} from './preview';
import {useGridReview} from './set-review';
import {GridExposureLabel,GridExposureSelect,useGridExposure} from './set-review-grid';
import {markLabels} from './set-review-data';
import {LibraryCriteria, LibraryCriteriaControls} from './library-filters';

export type LibraryFilter = {kind: 'all' | 'favorite' | 'folder' | 'project'; folder?: string; projectId?: string};
export function filterPhotos(photos: Photo[], filter: LibraryFilter, query = '', projects?: Project[]) {
  const term = query.toLocaleLowerCase().trim();
  const members=filter.kind==='project'?new Set(projects?.find(p=>p.id===filter.projectId)?.photoIds||[]):null;
  return photos.filter(p => (filter.kind !== 'favorite' || p.favorite)
    && (filter.kind !== 'folder' || folderOf(p.path) === filter.folder)
    && (!members || members.has(p.id))
    && (!term || (p.name + ' ' + (p.cameraModel || '') + ' ' + formatOf(p)+' '+(p.caption||'')+' '+(p.libraryDate||'')).toLocaleLowerCase().includes(term)));
}
export function ImportMenu({files, folder, sample, disabled}: {files: () => void; folder: () => void; sample: () => void; disabled: boolean}) {
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (!open) return;
    const click = (e: PointerEvent) => { if (!ref.current?.contains(e.target as Node)) setOpen(false); };
    const key = (e: KeyboardEvent) => { if(e.key === 'Escape') setOpen(false); };
    document.addEventListener('pointerdown', click); document.addEventListener('keydown', key);
    return () => {document.removeEventListener('pointerdown', click); document.removeEventListener('keydown', key);};
  }, [open]);
  const action = (fn: () => void) => {setOpen(false); fn();};
  return <div className="menu-anchor" ref={ref}>
    <button className="import-button" disabled={disabled} onClick={() => setOpen(!open)} aria-expanded={open} aria-label="导入照片或文件夹"><Icon name="plus" size={16}/>导入<Icon name="chevron" size={12}/></button>
    {open && <div className="popover import-menu">
      <button onClick={() => action(files)}><Icon name="photos"/>选择照片<kbd>Ctrl O</kbd></button>
      <button onClick={() => action(folder)}><Icon name="folder"/>导入文件夹</button>
      <button onClick={() => action(sample)}><Icon name="photo"/>打开示例</button>
    </div>}
  </div>;
}
type SideProps = {
  state: Snapshot | null; filter: LibraryFilter; onFilter: (f: LibraryFilter) => void;
  busy: boolean; files: () => void; folder: () => void; sample: () => void; swatchPhoto:Photo|null; swatchesReady:boolean;
  chosen: Recipe[]; togglePreset: (recipe: Recipe) => void; savePreset: () => void; close: () => void; manageProjects:()=>void;
};
export function Sidebar({state, filter, onFilter, busy, files, folder, sample, chosen, togglePreset, savePreset, close, manageProjects,swatchPhoto,swatchesReady}: SideProps) {
  const photos = state?.catalog || [];
  const {folders,favorites}=useMemo(()=>{
    const folders=new Map<string,number>();
    let favorites=0;
    for(const photo of photos){
      const folder=folderOf(photo.path);
      if(folder)folders.set(folder,(folders.get(folder)??0)+1);
      if(photo.favorite)favorites++;
    }
    return {folders,favorites};
  },[photos]);
  const [foldersOpen, setFoldersOpen] = useState(false);
  const [projectsOpen, setProjectsOpen] = useState(false);
  const [presetOpen, setPresetOpen] = useState(true);
  const [personal, setPersonal] = useState(false);
  const presets = (personal ? state?.userPresets : state?.presets) || [];
  return <aside className="sidebar" aria-label="照片与预设">
    <div className="panel-title"><span>资源管理</span><IconButton label="收起左侧面板" icon="collapseL" onClick={close}/></div>
    <div className="side-scroll">
      <div className="section-heading library-heading"><h2>照片库</h2><ImportMenu files={files} folder={folder} sample={sample} disabled={busy || !state}/></div>
      <div className="collections">
        <button className={'collection ' + (filter.kind==='all'?'selected':'')} aria-pressed={filter.kind==='all'} onClick={() => onFilter({kind:'all'})}><Icon name="photos"/><span>全部照片</span><small>{photos.length}</small></button>
        <button className={'collection ' + (filter.kind==='favorite'?'selected':'')} aria-pressed={filter.kind==='favorite'} onClick={() => onFilter({kind:'favorite'})}><Icon name="star"/><span>收藏夹</span><small>{favorites}</small></button>
        <button className={'collection folder-root ' + (filter.kind==='folder'?'in-folder':'')} aria-expanded={foldersOpen} onClick={() => setFoldersOpen(v=>!v)}><Icon name="folder"/><span>文件夹</span><Icon name="chevron" className={foldersOpen?'':'folded'} size={13}/></button>
        {foldersOpen && <div className="folder-list">{[...folders.entries()].map(([f, n]) => <button key={f} onClick={() => onFilter({kind:'folder',folder:f})} className={filter.folder===f?'selected':''} title={f}><span>{baseName(f)}</span><small>{n}</small></button>)}
          {!folders.size && <button onClick={folder} disabled={!state || busy}><Icon name="plus" size={12}/>导入文件夹</button>}</div>}
        {Array.isArray(state?.projects)&&<><div className="project-root"><button className="collection" aria-expanded={projectsOpen} onClick={()=>setProjectsOpen(v=>!v)}><Icon name="archive"/><span>项目</span><Icon name="chevron" className={projectsOpen?'':'folded'} size={13}/></button>
          <IconButton label="管理照片项目" icon="settings" size={13} disabled={busy} onClick={manageProjects}/></div>
          {projectsOpen&&<div className="folder-list">{state.projects.map(p=><button key={p.id} translate="no" title={p.name} className={filter.projectId===p.id?'selected':''} onClick={()=>onFilter({kind:'project',projectId:p.id})}><span translate="no">{p.name}</span><small>{p.photoIds.length}</small></button>)}
            {!state.projects.length&&<button disabled={busy} onClick={manageProjects}><Icon name="plus" size={12}/>创建项目</button>}</div>}
        </>}
      </div>
      <div className="presets-section">
        <div className="section-heading"><button className="heading-button" onClick={()=>setPresetOpen(v=>!v)} aria-expanded={presetOpen}><h2>预设库</h2><Icon name="chevron" className={presetOpen?'':'folded'} size={12}/></button>
          <IconButton label="将当前调整保存为预设" icon="plus" size={15} disabled={!state?.document || busy} onClick={savePreset}/></div>
        {presetOpen && <>
          <div className="mini-tabs" aria-label="预设分类"><button className={!personal?'active':''} onClick={()=>setPersonal(false)}>内置 {state?.presets.length || 0}</button><button className={personal?'active':''} onClick={()=>setPersonal(true)}>我的 {state?.userPresets.length || 0}</button></div>
          <PresetSwatches photo={swatchPhoto} ready={swatchesReady} controls={state?.controls||[]} doc={state?.document??null}
            recipes={presets} chosen={chosen} pick={togglePreset} busy={busy} personal={personal}/>
        </>}
      </div>
    </div>
  </aside>;
}
type PhotoMetrics={columns:number;step:number;gap:number;size:number;inset:number};
type PhotoAnchor={scope:string;selected:string|null;metrics:PhotoMetrics;first:number;atEnd:boolean};

// Keep a track at the full scroll extent and mount only the rows near it.
// Sizes come from the actual card and CSS grid after layout, including media queries.
function usePhotoWindow(photos:Photo[],selected:string|null,horizontal:boolean,enabled:boolean,scope='',searching=false) {
  const viewport=useRef<HTMLDivElement>(null);
  const items=useRef<HTMLDivElement>(null);
  const [metrics,setMetrics]=useState<PhotoMetrics>({columns:1,step:0,gap:0,size:0,inset:0});
  const [line,setLine]=useState(0);
  const anchor=useRef<PhotoAnchor|null>(null);
  const focusTarget=useRef<{index:number;lastButton:boolean}|null>(null);
  const active=enabled&&photos.length>0;
  useLayoutEffect(()=>{
    const el=viewport.current,group=items.current;
    if(!active||!el||!group){anchor.current=null;return;}
    const measure=()=>{
      const tile=group.firstElementChild;
      if(!tile)return;
      const style=getComputedStyle(group),box=tile.getBoundingClientRect(),outer=getComputedStyle(el);
      const gap=parseFloat(horizontal?style.columnGap:style.rowGap)||0;
      const next={columns:horizontal?1:style.gridTemplateColumns.split(' ').length,
        step:(horizontal?box.width:box.height)+gap,gap,size:horizontal?el.clientWidth:el.clientHeight,
        inset:parseFloat(horizontal?outer.paddingLeft:outer.paddingTop)||0};
      if(next.step>gap&&next.size>0)setMetrics(old=>Object.keys(next).every(k=>old[k as keyof PhotoMetrics]===next[k as keyof PhotoMetrics])?old:next);
    };
    const observer=new ResizeObserver(measure);
    observer.observe(el);measure();
    return ()=>observer.disconnect();
  },[active,horizontal]);

  const selectedIndex=useMemo(()=>photos.findIndex(p=>p.id===selected),[photos,selected]);
  const totalLines=Math.ceil(photos.length/metrics.columns);
  const visibleLines=metrics.step?Math.ceil(metrics.size/metrics.step)+1:0;
  const overscan=horizontal?4:2;
  const currentLine=Math.max(0,Math.min(line,totalLines-1));
  const firstLine=Math.max(0,currentLine-overscan);
  const first=metrics.step?firstLine*metrics.columns:0;
  const end=Math.min(photos.length,metrics.step?(currentLine+visibleLines+overscan)*metrics.columns:horizontal?16:32);
  const length=metrics.step?Math.max(0,totalLines*metrics.step-metrics.gap):undefined;
  const offset=metrics.step?firstLine*metrics.step:0;
  function syncPosition(){
    const el=viewport.current;
    if(!el||!metrics.step)return;
    const pos=horizontal?el.scrollLeft:el.scrollTop;
    const index=Math.max(0,Math.floor((pos-metrics.inset)/metrics.step));
    setLine(old=>old===index?old:index);
    if(anchor.current){
      anchor.current.first=index*metrics.columns;
      anchor.current.atEnd=pos+metrics.size>=(horizontal?el.scrollWidth:el.scrollHeight)-2;
    }
  }
  function reveal(index:number,align:'nearest'|'start'|'end'='nearest'){
    const el=viewport.current;
    if(!el||!metrics.step)return;
    const start=metrics.inset+Math.floor(Math.max(index,0)/metrics.columns)*metrics.step;
    const end=start+metrics.step-metrics.gap;
    const pos=horizontal?el.scrollLeft:el.scrollTop;
    let next=pos;
    if(align==='end')next=horizontal?el.scrollWidth:el.scrollHeight;
    else if(align==='start'||start<pos)next=start-metrics.inset;
    else if(end>pos+metrics.size)next=end-metrics.size+1;
    if(horizontal)el.scrollLeft=next;else el.scrollTop=next;
    syncPosition();
  }
  useLayoutEffect(()=>{
    if(!active||!metrics.step)return;
    const old=anchor.current;
    anchor.current={scope,selected,metrics,first:old?.first??0,atEnd:old?.atEnd??false};
    if(!old||old.scope!==scope||old.selected!==selected)reveal(searching||selectedIndex<0?0:selectedIndex,searching||selectedIndex<0?'start':'nearest');
    else if(old.metrics!==metrics)reveal(old.atEnd?photos.length-1:old.first,old.atEnd?'end':'start');
    else syncPosition();
  },[active,metrics,scope,selected,selectedIndex,photos.length,searching]);
  function finishFocus(){
    const target=focusTarget.current;
    if(!target)return;
    const tile=items.current?.querySelector<HTMLElement>('[data-photo-index="'+target.index+'"]');
    if(!tile)return;
    const buttons=tile.querySelectorAll<HTMLButtonElement>('button');
    const button=tile instanceof HTMLButtonElement?tile:buttons[target.lastButton?buttons.length-1:0];
    if(button&&!button.disabled){button.focus({preventScroll:true});focusTarget.current=null;}
  }
  useLayoutEffect(finishFocus,[first,end,metrics]);
  function onKeyDown(e:React.KeyboardEvent<HTMLDivElement>){
    if(e.altKey||e.ctrlKey||e.metaKey||!active)return;
    const button=e.target as HTMLElement;
    const tile=button.closest<HTMLElement>('[data-photo-index]');
    if(!tile)return;
    const index=Number(tile.dataset.photoIndex);
    let next=index,lastButton=false;
    switch(e.key){
      case 'Home':next=0;break;
      case 'End':next=photos.length-1;break;
      case 'ArrowUp':if(horizontal)return;next-=metrics.columns;break;
      case 'ArrowDown':if(horizontal)return;next+=metrics.columns;break;
      case 'ArrowLeft':if(horizontal)return;next--;break;
      case 'ArrowRight':if(horizontal)return;next++;break;
      case 'PageUp':next-=Math.max(1,visibleLines-2)*metrics.columns;break;
      case 'PageDown':next+=Math.max(1,visibleLines-2)*metrics.columns;break;
      case 'Tab':{
        const buttons=tile.querySelectorAll('button');
        if(e.shiftKey&&index===first&&first>0&&(horizontal||button===buttons[0])){next--;lastButton=true;}
        else if(!e.shiftKey&&index===end-1&&end<photos.length&&(horizontal||button===buttons[buttons.length-1]))next++;
        else return;
        break;
      }
      default:return;
    }
    e.preventDefault();e.stopPropagation();
    next=Math.max(0,Math.min(photos.length-1,next));
    focusTarget.current={index:next,lastButton};reveal(next);finishFocus();
  }
  return {viewport,items,first,end,length,offset,onScroll:syncPosition,onKeyDown};
}

export function LibraryView({photos, selected, filter, query, setQuery, select, favorite, files, busy, sample, project, manageProjects, remove,review,relink,exposure,criteria,changeCriteria,tools}: {
  photos: Photo[]; selected: string | null; filter: LibraryFilter; query: string; setQuery: (s: string) => void;
  select: (p: Photo) => void; favorite: (p: Photo)=>void; files: () => void; busy: boolean; sample: () => void; project?:Project; manageProjects:()=>void; remove?:(p:Photo)=>void;
  review?:()=>void; relink?:()=>void;exposure:ReturnType<typeof useGridExposure>;
  criteria:LibraryCriteria;changeCriteria:(criteria:LibraryCriteria)=>void;tools?:React.ReactNode;
}) {
  const title = filter.kind === 'all' ? '全部照片' : filter.kind === 'favorite' ? '收藏夹' : filter.kind==='project' ? project?.name || '项目' : baseName(filter.folder || '');
  const narrowed=criteria.dates!=='any'||criteria.edits!=='any';
  const view=usePhotoWindow(photos,selected,false,true,JSON.stringify([filter.kind,filter.projectId,filter.folder,query,criteria,exposure.scope]),!!query||narrowed);
  const flags=useGridReview(photos,view.first,view.end,!!review,exposure.epoch);
  return <section className="library-view">
    <div className="library-toolbar"><div><h1 translate={filter.kind==='project'||filter.kind==='folder'?'no':undefined}>{title}</h1><span>{photos.length} 张照片</span></div>
      {relink&&<button className="secondary small" disabled={busy} onClick={relink}>找回原片</button>}
      {review&&<button className="secondary small" disabled={busy||!exposure.canAnalyze} onClick={review} title="并排挑片与基准校色；图库中按 P 保留、X 淘汰、U 清标">整组审片与校色</button>}
      {review&&<GridExposureSelect scope={exposure.scope} choose={exposure.setScope}/>}
      <label className="inline-search library-search"><Icon name="search" size={14}/><input value={query} onChange={e=>setQuery(e.target.value)} placeholder="搜索名称、相机或说明" aria-label="搜索照片"/></label></div>
    <div className="library-secondary"><LibraryCriteriaControls value={criteria} change={changeCriteria}/>{tools}</div>
    {(flags.error||exposure.error)&&<p className="field-error" role="alert">{flags.error||exposure.error}</p>}
    {photos.length ? <div className="photo-grid" ref={view.viewport} onScroll={view.onScroll} onKeyDown={e=>{
      const tile=(e.target as HTMLElement).closest<HTMLElement>('[data-photo-index]');
      if(!busy&&flags.keyDown(e,tile?photos[Number(tile.dataset.photoIndex)]?.id:undefined))return;
      view.onKeyDown(e);
    }}>
      <div className="photo-grid-track" style={{height:view.length}}><div className={'photo-grid-items'+(view.length===undefined?'':' positioned')} ref={view.items} style={{top:view.offset}}>
      {photos.slice(view.first,view.end).map((p,i)=><article className={'grid-photo ' + (selected===p.id?'current':'')} key={p.id} data-photo-index={view.first+i}>
      <button disabled={busy} onClick={()=>select(p)} title={'编辑 ' + p.name}><Thumb photo={p}/><span translate="no" className="grid-photo-title">{p.name}</span><small>{formatOf(p)} <i/> {p.width} × {p.height}</small></button>
      {flags.marks[p.id]&&flags.marks[p.id]!=='none'&&<span className={'grid-review-mark set-mark '+flags.marks[p.id]}>{markLabels[flags.marks[p.id]]}</span>}
      <GridExposureLabel photo={p} value={flags.exposures[p.id]}/>
      <button className={'grid-fav ' + (p.favorite?'on':'')} onClick={()=>favorite(p)} disabled={busy} aria-label={(p.favorite?'取消收藏 ':'收藏 ')+p.name}><Icon name="star" size={15}/></button>
      {remove&&<button className="grid-remove" onClick={()=>remove(p)} disabled={busy} title={'从图库移除 '+p.name} aria-label={'从图库移除 '+p.name}><Icon name="trash" size={14}/></button>}
    </article>)}</div></div></div> : narrowed?<div className="gallery-empty"><h2>没有符合日期和修图状态的照片</h2></div>:exposure.scope!=='all'?<div className="gallery-empty">
      <h2>{exposure.loading?'正在查看曝光结果…':'此范围内没有对应的曝光提示'}</h2>
      {!exposure.loading&&<><p>在整组审片中分析原片后，可按曝光结果筛选。</p>
        <button className="text-button" onClick={()=>exposure.setScope('all')}>查看全部曝光</button></>}
    </div>:<div className="gallery-empty"><Icon name={filter.kind==='favorite'?'star':'photos'} size={33}/><h2>{query?'没有匹配的照片':filter.kind==='favorite'?'还没有收藏照片':filter.kind==='project'?'项目中还没有照片':'图库为空'}</h2>
      {query ? <button className="text-button" onClick={()=>setQuery('')}>清除搜索</button> : filter.kind==='favorite' ? <p>点击照片旁的星标即可收藏。</p> : filter.kind==='project'?<button className="secondary" onClick={manageProjects}>管理项目照片</button>:<div className="button-line"><button className="primary" onClick={files} disabled={busy}>导入照片</button><button className="secondary" onClick={sample} disabled={busy}>打开示例</button></div>}
    </div>}
  </section>;
}
export function Filmstrip({photos, selected, choose, files, busy, open, toggle}: {
  photos: Photo[]; selected: string | null; choose: (p: Photo)=>void; files: () => void; busy: boolean; open: boolean; toggle: () => void;
}) {
  const view=usePhotoWindow(photos,selected,true,open);
  return <section className={'filmstrip ' + (open?'':'collapsed')} aria-label="胶片栏">
    <div className="film-head"><button onClick={toggle} aria-expanded={open}><Icon name="film" size={14}/><b>胶片栏</b><Icon name="chevron" className={open?'':'folded'} size={12}/></button><span>{photos.length} 张照片</span></div>
    {open && <div className="film-row" ref={view.viewport} onScroll={view.onScroll} onKeyDown={view.onKeyDown}>
      {!!photos.length&&<div className="film-track" style={{width:view.length}}><div className={'film-items'+(view.length===undefined?'':' positioned')} ref={view.items} style={{left:view.offset}}>
        {photos.slice(view.first,view.end).map((p,i)=><button key={p.id} className={'film-image ' + (selected===p.id?'current':'')} data-photo-index={view.first+i} title={p.name} aria-label={'切换到 '+p.name} aria-current={selected===p.id} onClick={()=>choose(p)} disabled={busy}>
          <Thumb photo={p}/><span className="film-index">{String(view.first+i+1).padStart(2,'0')}</span>{p.favorite&&<Icon name="star" className="film-star" size={10}/>}
        </button>)}</div></div>}
      <button className="film-add" title="添加照片" aria-label="添加照片" onClick={files} disabled={busy}><Icon name="plus" size={17}/></button></div>}
  </section>;
}
