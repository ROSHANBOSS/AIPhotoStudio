import React,{useEffect,useRef,useState} from 'react';
import {Photo,invoke,messageOf} from './model';
import {AreaSelection,Guide,MatchArea,MatchPerson,MatchRole,PreparedSuggestions,Suggestion,prepareSuggestions,propose} from './set-match-suggestions';
import {FaceThumbnail,SampleDetail,SamplePoint} from './set-match-detail';

export const regionNames={skin:'肤色',background:'背景',hair:'头发'};
type Point={x:number;y:number};
const bounded=(n:number)=>Math.max(0,Math.min(1,n));

export function MatchAreaPicker({photo,label,role,selection,reference,target=false,disabled,change,suggest,omit}:{
  photo:Photo;label:string;role:MatchRole;selection?:AreaSelection;reference?:AreaSelection;target?:boolean;disabled:boolean;
  change:(role:MatchRole,area:MatchArea|null,digest:string)=>void;suggest:(value:Suggestion,digest:string,backgroundOnly:boolean)=>void;
  omit?:(digest:string)=>void}){
  const [guide,setGuide]=useState<Guide|null>(null),[error,setError]=useState('');
  const [options,setOptions]=useState<PreparedSuggestions|null>(null),[hintError,setHintError]=useState('');
  const [draft,setDraft]=useState<MatchArea|null>(null),[cursor,setCursor]=useState<Point|null>(null);
  const [zoom,setZoom]=useState(false),[center,setCenter]=useState<(SamplePoint&{box?:MatchPerson['box']})|null>(null),[loaded,setLoaded]=useState<HTMLImageElement|null>(null);
  const [checking,setChecking]=useState<number|null>(null),[checked,setChecked]=useState<Record<number,{message:string;done:boolean}>>({});
  const checkRun=useRef(0);
  const anchor=useRef<{point:Point;pointer:number}|null>(null),image=useRef<HTMLImageElement>(null);
  useEffect(()=>{
    let current=true;setGuide(null);setError('');setOptions(null);setHintError('');anchor.current=null;setDraft(null);setCursor(null);
    setZoom(false);setCenter(null);setLoaded(null);setChecking(null);setChecked({});
    void invoke<Guide>('setMatchAreaInput',{id:photo.id,revision:photo.revision??0,suggest:true}).then(async result=>{
      if(!current)return;setGuide(result);
      try{const found=await prepareSuggestions(photo,result);if(current)setOptions(found);}
      catch(err){if(current)setHintError(messageOf(err));}
    })
      .catch(err=>{if(current)setError(messageOf(err));});
    return ()=>{current=false;checkRun.current++;};
  },[photo.id,photo.revision]);
  const valid=guide&&selection?.digest===guide.digest&&selection.revision===(photo.revision??0)?selection:undefined;
  const areas=valid?.regions??{};
  const fromEvent=(e:React.PointerEvent<HTMLElement>):Point=>{
    const box=image.current!.getBoundingClientRect();
    return {x:bounded((e.clientX-box.left)/box.width),y:bounded((e.clientY-box.top)/box.height)};
  };
  function ellipse(a:Point,b:Point):MatchArea{
    const size=image.current!.getBoundingClientRect();
    if(Math.hypot((b.x-a.x)*size.width,(b.y-a.y)*size.height)<5){
      const radius=Math.min(size.width,size.height)*(role==='skin'?.06:.12);
      return {x:b.x,y:b.y,rx:Math.max(.01,radius/size.width),ry:Math.max(.01,radius/size.height)};
    }
    return {x:(a.x+b.x)/2,y:(a.y+b.y)/2,rx:Math.max(.004,Math.abs(a.x-b.x)/2),ry:Math.max(.004,Math.abs(a.y-b.y)/2)};
  }
  function accept(area:MatchArea){if(guide&&!disabled)change(role,area,guide.digest);}
  function choose(person?:MatchPerson){
    if(!guide||!options||disabled)return;
    suggest(propose(options,person,reference),guide.digest,!person);
  }
  const bg=options&&(!target||reference?.regions.background)?propose(options,undefined,reference).regions.background:null;
  const selectedFace=options?.people.find(p=>p.number===valid?.person);
  function focus(p:SamplePoint){
    const local=role==='skin'?options?.people.find(({box:b})=>p.x>=b.x&&p.x<=b.x+b.w&&p.y>=b.y&&p.y<=b.y+b.h):undefined;
    setCenter({x:p.x,y:p.y,box:local?.box});
  }
  async function recheck(person:MatchPerson){
    if(!options||!guide||checking!=null||disabled)return;
    const seq=++checkRun.current;setChecking(person.number);
    try{
      const found=await options.recheck(guide,person);
      if(checkRun.current!==seq)return;
      if(found)setOptions(old=>old&&({...old,people:old.people.map(p=>p.number===person.number?found:p)}));
      setChecked(old=>({...old,[person.number]:{message:found?'局部复查找到面颊，点该头像可选用。':'局部复查仍未找到稳定的面颊，可放大手圈。',done:true}}));
    }catch(err){if(checkRun.current===seq)setChecked(old=>({...old,[person.number]:{message:messageOf(err),done:false}}));}
    finally{if(checkRun.current===seq)setChecking(null);}
  }
  const effect=areas.skin?.effect;
  const shown={...areas,...(draft?{[role]:draft}:{})};
  return <section className="set-area-picker" aria-label={label+'取样'}>
    <h3 title={photo.path}>{label} · {photo.name}</h3>
    <div className="set-area-stage">{guide?<button type="button" className="set-area-image" disabled={disabled} aria-label={label+'取样图'}
      title={zoom?'点选需要放大的部位。':'单击或拖出椭圆。键盘方向键移动取样点，回车确认。'}
      onPointerDown={e=>{if(e.button!==0||!image.current?.naturalWidth)return;e.currentTarget.setPointerCapture(e.pointerId);const p=fromEvent(e);anchor.current={point:p,pointer:e.pointerId};if(!zoom)setDraft(ellipse(p,p));setCursor(null);}}
      onPointerMove={e=>{if(!zoom&&anchor.current?.pointer===e.pointerId)setDraft(ellipse(anchor.current.point,fromEvent(e)));}}
      onPointerUp={e=>{if(anchor.current?.pointer!==e.pointerId)return;const p=fromEvent(e),start=anchor.current.point;anchor.current=null;setDraft(null);if(zoom)focus(p);else accept(ellipse(start,p));}}
      onPointerCancel={()=>{anchor.current=null;setDraft(null);}}
      onKeyDown={e=>{
        if(!image.current?.naturalWidth)return;
        const step=e.shiftKey?.1:.02,p=cursor||areas[role]||{x:.5,y:.5};
        const offset:Record<string,Point>={ArrowLeft:{x:-step,y:0},ArrowRight:{x:step,y:0},ArrowUp:{x:0,y:-step},ArrowDown:{x:0,y:step}};
        if(offset[e.key]){e.preventDefault();setCursor({x:bounded(p.x+offset[e.key].x),y:bounded(p.y+offset[e.key].y)});}
        if(e.key==='Enter'||e.key===' '){e.preventDefault();if(zoom)focus(p);else accept(ellipse(p,p));}
      }}>
      <img ref={image} src={guide.image} onLoad={e=>setLoaded(e.currentTarget)} alt={label+'已保存的调色，原片坐标'} draggable={false}/>
      {effect&&selectedFace?.overlay&&<span aria-label="初选面部范围" style={{position:'absolute',pointerEvents:'none',
        left:(effect.x-effect.rx)*100+'%',top:(effect.y-effect.ry)*100+'%',width:effect.rx*200+'%',height:effect.ry*200+'%',
        backgroundImage:'url('+selectedFace.overlay+')',backgroundSize:'100% 100%'}}/>}
      {options?.people.map(p=><span key={p.number} aria-hidden="true" style={{position:'absolute',pointerEvents:'none',
        left:Math.max(0,p.box.x)*100+'%',top:Math.max(0,p.box.y-.018)*100+'%',transform:'translate(-35%,-55%)',
        padding:'0 3px',borderRadius:3,font:'bold 11px/14px sans-serif',color:p.number===valid?.person?'#242020':'white',
        background:p.number===valid?.person?'#ffc197':'#292a2be6'}}>{p.number}</span>)}
      {(['skin','background'] as const).map(k=>{const a=shown[k];return a&&<span key={k} className={'set-area-ring '+k}
        style={{left:100*(a.x-a.rx)+'%',top:100*(a.y-a.ry)+'%',width:200*a.rx+'%',height:200*a.ry+'%'}}/>;})}
      {cursor&&<span className="set-area-cursor" style={{left:100*cursor.x+'%',top:100*cursor.y+'%'}}>+</span>}
    </button>:<span role={error?'alert':'status'}>{error||'正在读取取样图…'}</span>}</div>
    <div className="set-area-tags">{(['skin','background'] as const).map(k=>areas[k]?<button type="button" key={k} className={k} disabled={disabled}
      aria-label={'清除'+label+regionNames[k]+'选区'} onClick={()=>guide&&change(k,null,guide.digest)}>{regionNames[k]} ×</button>:
      target&&valid?.suggested&&reference?.regions[k]&&!valid.skipped?.includes(k)?
        <button key={k} disabled={disabled} onClick={()=>guide&&change(k,null,guide.digest)}>此张跳过{regionNames[k]}</button>:
        <span key={k}>{regionNames[k]}{valid?.skipped?.includes(k)?'已跳过':'未选'}</span>)}
      {loaded&&<button type="button" aria-label={label+'放大取样'} aria-pressed={zoom} disabled={disabled} onClick={()=>{
        if(!zoom){const a=areas[role],b=selectedFace?.box,p=a??(b?{x:b.x+b.w/2,y:b.y+b.h/2}:null);if(p)focus(p);else setCenter(null);}
        setZoom(!zoom);
      }}>{zoom?'收起放大图':'放大取样'}</button>}</div>
    {zoom&&loaded&&<><div className="set-area-tags">{center?'点全图可换位置，在放大图中点或拖圈取'+regionNames[role]+'。':'先在上图点一下要放大的部位。'}</div>
      {center&&<SampleDetail photo={loaded} center={center} box={center.box} area={areas[role]} role={role}
        label={label} disabled={disabled} choose={accept}/>}</>}
    {guide&&(options?<div>
      {!!options.people.length&&<div className="set-area-tags" style={{flexWrap:'wrap'}}><span>选人物</span>{options.people.map(p=>
        <button type="button" key={p.number} aria-label={label+'人物 '+p.number} aria-pressed={p.number===valid?.person}
          disabled={disabled||(target&&!reference?.regions.skin)} onClick={()=>choose(p)}
          title={p.uncertain?'定位重叠，可放大手圈':p.cheeks.length?'选择这位人物':'此人面颊需手圈'}
          style={{position:'relative',padding:0,overflow:'hidden',outline:p.number===valid?.person?'2px solid #ffc197':undefined}}>
          <FaceThumbnail photo={loaded} box={p.box}/><span aria-hidden="true" style={{position:'absolute',bottom:0,right:0,padding:'0 3px',
            background:'#232526db',color:'white',fontSize:10,lineHeight:'13px'}}>{p.number}</span></button>)}</div>}
      <div className="set-area-tags" style={{flexWrap:'wrap'}}>
        {bg&&<button type="button" disabled={disabled} aria-label={label+'推荐背景'} onClick={()=>choose()}>推荐背景</button>}
        <span>{options.savedFaces?'已存人脸':'本机定位人脸'} {options.people.length} 处{options.savedBodies?' · 已存人体 '+options.savedBodies+' 人':''}</span>
        {omit&&reference?.regions.skin&&<button type="button" disabled={disabled} onClick={()=>omit(guide.digest)}>
          {valid?.omitted?'恢复此张取样':'跳过此张人物追色'}</button>}
      </div>
      {selectedFace&&!selectedFace.cheeks.length&&!areas.skin&&<div className="set-area-warning">{selectedFace.uncertain?'此处人脸定位重叠':'此人面颊取样不可靠'}，可手圈肤色。</div>}
      {selectedFace&&!areas.skin&&!selectedFace.uncertain&&!selectedFace.cheeks.length&&!checked[selectedFace.number]?.done&&
        <button type="button" className="text-button" disabled={disabled||checking!=null} onClick={()=>void recheck(selectedFace)}>
          {checking===selectedFace.number?'正在复查局部肤色…':'局部复查面颊'}</button>}
      {selectedFace&&!areas.skin&&checked[selectedFace.number]&&<div className="set-area-tags" role="status">{checked[selectedFace.number].message}</div>}
      {!bg&&(!target||reference?.regions.background)&&<div className="set-area-warning">{target?'未找到与基准相近的安全背景。':'未找到避开人体和曝光异常的背景。'}可手圈背景。</div>}
      {target&&reference?.side&&valid?.side&&reference.side!==valid.side&&<div className="set-area-warning">只能取到另一侧面颊，请比较两边的光线。</div>}
      {!options.people.length&&<div className="set-area-warning">未定位完整人脸，可手圈肤色。</div>}
    </div>:<div className="set-area-tags" role="status">{hintError?'本机推荐不可用，可手圈。'+hintError:'正在本机准备推荐，可先手圈。'}</div>)}
    {!!guide?.stored?.length&&<label className="set-area-reuse">沿用椭圆 <select aria-label={'沿用'+label+'已存椭圆'} disabled={disabled} value={areas[role]?.maskId||''}
      onChange={e=>{const found=guide.stored.find(a=>a.id===e.target.value);if(found)accept(found.area);}}>
      <option value="" disabled>为{regionNames[role]}选择已有蒙版</option>{guide.stored.map(m=><option key={m.id} value={m.id}>{m.name}</option>)}</select></label>}
  </section>;
}
