import React,{useRef} from 'react';
import {Photo,Values,clamp} from './model';
import {frameChanges,frameKeys,frameLayout,frameRatios,frameValues} from './framing';
import {Icon} from './icons';
import {ControlVisit,useVisitPanel} from './adjusted';
import {FrameMarks,FrameTool} from './frame-marks';
import {pageKeys,pageLayout,pageRatios} from './frame-page';

type Props={
  photo:Photo; values:Values; saved:Values; locks:string[]; preview:boolean; busy:boolean; visit?:ControlVisit; tool:FrameTool; choose:(tool:FrameTool)=>void;incomplete:boolean;
  change:(v:Values)=>void; toggle:()=>void; apply:()=>void; cancel:()=>void; info:(key:string)=>void;
};
export function FramingPanel({photo,values,saved,locks,preview,busy,visit,tool,choose,incomplete,change,toggle,apply,cancel,info}:Props){
  const panel=useVisitPanel<HTMLElement>(visit);
  const layout=frameLayout(photo.width,photo.height,values);
  const fieldMenu=(key:string,label:string)=><button className="frame-field-menu" aria-label={label+'参数选项'} disabled={busy}
    title={label+'的锁定与历史'} onClick={()=>info(key)}><Icon size={13} name={locks.includes(key)?'lock':'dots'}/></button>;
  const slider=(key:string,label:string,min:number,max:number,suffix:string,remaining=false)=> {
    const value=remaining?100-values[key]:values[key];
    return <div className="frame-slider" data-parameter={key}><label><span>{label} <b>{value.toFixed(1).replace(/\.0$/,'')}{suffix}</b></span>
      <input aria-label={label} type="range" min={min} max={max} step=".1" value={value} disabled={busy||locks.includes(key)}
        onChange={e=>change({...values,[key]:remaining?100-Number(e.target.value):Number(e.target.value)})}/></label>{fieldMenu(key,label)}</div>;
  };
  return <section ref={panel} className="frame-panel"><h2>裁切与校正</h2>
    <div className="frame-size" aria-live="polite">{layout.width} × {layout.height} 像素</div>
    <div className="frame-methods" role="group" aria-label="取景工具">
      {(['crop','line','page'] as const).map((mode,i)=><button key={mode} aria-pressed={tool===mode&&!preview} disabled={busy||mode==='line'&&locks.includes('frame_angle')}
        onClick={()=>choose(mode)}>{['取景框','画线拉正','四角拉正'][i]}</button>)}
    </div>
    {tool==='page'&&<><div className="frame-field" data-parameter="frame_doc_ratio"><label>纸面比例<select aria-label="纸面比例" value={values.frame_doc_ratio}
      disabled={busy||locks.includes('frame_doc_ratio')} onChange={e=>change({...values,frame_doc_ratio:Number(e.target.value)})}>
      {pageRatios.map((ratio,i)=><option key={ratio} value={i}>{ratio}</option>)}</select></label>{fieldMenu('frame_doc_ratio','纸面比例')}</div>
      <details><summary>角点位置与锁</summary>{pageKeys.slice(0,8).map((key,i)=>{
        const name=['左上','右上','右下','左下'][Math.floor(i/2)]+(i%2?'垂直内移':'水平内移');
        return <div className="frame-corner-field" key={key} data-parameter={key}><span>{name}</span><b>{values[key]}%</b>{fieldMenu(key,name)}</div>;
      })}</details></>}
    <div className="frame-field" data-parameter="frame_ratio"><label>裁切比例<select aria-label="裁切比例" value={values.frame_ratio} disabled={busy||locks.includes('frame_ratio')}
      onChange={e=>change({...values,frame_ratio:Number(e.target.value)})}>{frameRatios.map((name,i)=><option key={name} value={i}>{name}</option>)}</select></label>{fieldMenu('frame_ratio','比例')}</div>
    <div className="frame-turn" data-parameter="frame_turn"><button className="secondary small" disabled={busy||locks.includes('frame_turn')} onClick={()=>change({...values,frame_turn:(values.frame_turn+3)%4})}>向左转 90°</button>
      <button className="secondary small" disabled={busy||locks.includes('frame_turn')} onClick={()=>change({...values,frame_turn:(values.frame_turn+1)%4})}>向右转 90°</button>{fieldMenu('frame_turn','旋转')}</div>
    <div className="frame-field" data-parameter="frame_mirror"><button className="secondary small" aria-pressed={!!values.frame_mirror}
      disabled={busy||locks.includes('frame_mirror')} onClick={()=>change({...values,frame_mirror:values.frame_mirror?0:1})}>水平镜像</button>{fieldMenu('frame_mirror','水平镜像')}</div>
    {slider('frame_angle','拉直角度',-45,45,'°')}
    {slider('frame_trim_x',values.frame_ratio?'裁切范围':'保留宽度',1,100,'%',true)}
    {(values.frame_ratio===0||visit?.key==='frame_trim_y')&&slider('frame_trim_y','保留高度',1,100,'%',true)}
    {values.frame_ratio!==0&&visit?.key==='frame_trim_y'&&<p>当前按比例联动裁切。自由比例使用这里的保留高度。</p>}
    {slider('frame_pan_x','原图水平位置',-100,100,'%')}
    {slider('frame_pan_y','原图垂直位置',-100,100,'%')}
    <p>{preview?'正在显示裁切成品。':tool==='page'?'按顺时针依次点纸张四角。知道纸张规格时，可指定比例。':tool==='line'?'沿一条应当水平或垂直的边拖动，程序会调整角度并自动裁边。':'拖动四角改变裁切范围。拖动框内可以移动取景。'}</p>
    <div className="frame-actions"><button className="secondary small" disabled={busy||incomplete} onClick={toggle}>{preview?'返回取景':'预览成品'}</button>
      <button className="text-button" disabled={busy} onClick={()=>{
        const reset=frameValues();for(const k of frameKeys)if(locks.includes(k))reset[k]=values[k];
        if(!pageLayout(photo.width,photo.height,reset))for(const k of pageKeys)if(k!=='frame_doc_ratio')reset[k]=values[k];
        change(reset);choose('crop');
      }}>重置构图</button></div>
    <div className="frame-actions"><button className="secondary small" disabled={busy} onClick={cancel}>取消</button>
      <button className="primary small" disabled={busy||incomplete||!Object.keys(frameChanges(values,saved)).length} onClick={apply}>应用构图</button></div>
  </section>;
}

const corners=[[0,0],[1,0],[1,1],[0,1]] as const;
type Drag={start:{x:number;y:number};corner:number;base:Values};
const tenth=(n:number)=>Math.round(n*10)/10;
export function FramingOverlay({photo,values,change,locks,busy,tool,onIncomplete}:{photo:Photo;values:Values;change:(v:Values)=>void;locks:string[];busy:boolean;tool:FrameTool;onIncomplete:(pending:boolean)=>void}){
  const drag=useRef<Drag|null>(null);
  if(tool!=='crop')return <FrameMarks key={photo.id+tool} {...{photo,values,change,locks,busy,tool,onIncomplete}}/>;
  const w=photo.width,h=photo.height;
  const layout=frameLayout(w,h,values);
  const points=corners.map(([x,y])=>layout.toSource(x,y));
  const xy=(x:number,y:number)=>{const p=layout.toSource(x,y);return p.x*w+' '+p.y*h;};
  const path='M'+points.map(p=>p.x*w+' '+p.y*h).join('L')+'Z';
  const pointer=(e:React.PointerEvent<HTMLDivElement>)=>{const r=e.currentTarget.getBoundingClientRect();return{x:(e.clientX-r.left)/r.width,y:(e.clientY-r.top)/r.height};};
  const finish=(e:React.PointerEvent<HTMLDivElement>)=>{if(!drag.current)return;e.stopPropagation();drag.current=null;};
  return <div className="frame-overlay" role="group" aria-label="构图取景框"
    onPointerDown={e=>{
      e.stopPropagation();if(busy||e.button!==0)return;
      const p=pointer(e),handle=(e.target as Element).getAttribute('data-corner'),out=layout.toOutput(p.x,p.y);
      if(handle==null&&(out.x<0||out.x>1||out.y<0||out.y>1))return;
      drag.current={start:p,base:{...values},corner:handle==null?-1:Number(handle)};
      e.currentTarget.setPointerCapture(e.pointerId);e.preventDefault();
    }}
    onPointerMove={e=>{
      const g=drag.current;if(!g||busy)return;e.stopPropagation();
      const p=pointer(e),a=frameLayout(w,h,g.base),next={...g.base};
      const first=a.toPlane(g.start.x,g.start.y),cur=a.toPlane(p.x,p.y);
      let cx=a.cx+(cur.x-first.x)*a.planeW,cy=a.cy+(cur.y-first.y)*a.planeH;
      if(g.corner>=0){
        const [hx,hy]=corners[g.corner],opx=1-hx,opy=1-hy,start=a.toOutput(g.start.x,g.start.y),current=a.toOutput(p.x,p.y);
        const fw=Math.max(.001,1+(current.x-start.x)*(hx?1:-1))*a.width/a.maxWidth;
        const fh=Math.max(.001,1+(current.y-start.y)*(hy?1:-1))*a.height/a.maxHeight;
        const fraction=g.base.frame_ratio?(fw*a.maxWidth*a.maxWidth+fh*a.maxHeight*a.maxHeight)/(a.maxWidth*a.maxWidth+a.maxHeight*a.maxHeight):fw;
        if(!locks.includes('frame_trim_x'))next.frame_trim_x=tenth(clamp(100*(1-fraction),0,99));
        if(!g.base.frame_ratio&&!locks.includes('frame_trim_y'))next.frame_trim_y=tenth(clamp(100*(1-fh),0,99));
        const resized=frameLayout(w,h,next),anchor=a.toSource(opx,opy),after=resized.toSource(opx,opy);
        const fixed=a.toPlane(anchor.x,anchor.y),moved=resized.toPlane(after.x,after.y);
        cx=resized.cx+(fixed.x-moved.x)*a.planeW;cy=resized.cy+(fixed.y-moved.y)*a.planeH;
      }
      const updated=frameLayout(w,h,next);
      if(updated.travelX>0&&!locks.includes('frame_pan_x'))next.frame_pan_x=tenth(clamp((cx-a.planeW/2)*200/updated.travelX,-100,100));
      if(updated.travelY>0&&!locks.includes('frame_pan_y'))next.frame_pan_y=tenth(clamp((cy-a.planeH/2)*200/updated.travelY,-100,100));
      change(next);
    }} onPointerUp={finish} onPointerCancel={finish} onDoubleClick={e=>e.stopPropagation()}>
    <svg viewBox={'0 0 '+w+' '+h} preserveAspectRatio="none" aria-hidden="true">
      <path d={'M0 0H'+w+'V'+h+'H0Z'+path} fill="rgba(0,0,0,.60)" fillRule="evenodd"/>
      <path d={path} fill="transparent" cursor="move" stroke="white" strokeWidth="1.5" vectorEffect="non-scaling-stroke"/>
      {[1/3,2/3].map(mark=><path key={mark} d={'M'+xy(mark,0)+'L'+xy(mark,1)+'M'+xy(0,mark)+'L'+xy(1,mark)}
        stroke="rgba(255,255,255,.6)" fill="none" strokeWidth="1" vectorEffect="non-scaling-stroke" pointerEvents="none"/>)}
    </svg>
    {points.map((p,i)=><i key={i} data-corner={i} style={{left:p.x*100+'%',top:p.y*100+'%',cursor:i%2?'nesw-resize':'nwse-resize'}}/>)}
  </div>;
}
