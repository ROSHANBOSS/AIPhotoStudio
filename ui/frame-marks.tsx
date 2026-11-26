import React,{useEffect,useRef,useState} from 'react';
import {clamp,Photo,Values} from './model';
import {frameLayout} from './framing';
import {cornerValues,FramePoint,pageCorners,pageKeys,pageLayout} from './frame-page';

export type FrameTool='crop'|'line'|'page';
const cornerNames=['左上角','右上角','右下角','左下角'];
const unit=[[0,0],[1,0],[1,1],[0,1]];
type Props={photo:Photo;values:Values;locks:string[];busy:boolean;tool:'line'|'page';change:(v:Values)=>void;onIncomplete:(pending:boolean)=>void};

export function FrameMarks({photo,values,locks,busy,tool,change,onIncomplete}:Props) {
  const [line,setLine]=useState<FramePoint[]>([]);
  const [picks,setPicks]=useState<FramePoint[]|null>(()=>pageKeys.slice(0,8).some(k=>values[k])?null:[]);
  const [ghost,setGhost]=useState<FramePoint[]|null>(null);
  const [message,setMessage]=useState('');
  const incomplete=tool==='page'&&!!picks?.length;
  useEffect(()=>{onIncomplete(incomplete);return ()=>onIncomplete(false);},[incomplete,onIncomplete]);
  const drag=useRef<{pointer:number;start:FramePoint;index:number}|null>(null);
  const w=photo.width,h=photo.height,layout=frameLayout(w,h,values);
  const saved=pageCorners(values);
  const points=tool==='line'?line:ghost??picks??saved;
  const crop=unit.map(([x,y])=>layout.toSource(x,y));
  const coords=(p:FramePoint[])=>p.map(q=>q.x*w+','+q.y*h).join(' ');
  const position=(e:React.PointerEvent<HTMLDivElement>):FramePoint=>{
    const r=e.currentTarget.getBoundingClientRect();return {x:clamp((e.clientX-r.left)/r.width,0,1),y:clamp((e.clientY-r.top)/r.height,0,1)};
  };
  const accept=(corners:FramePoint[])=>{
    const next=cornerValues(values,corners,locks);
    if(!pageLayout(w,h,next)){setMessage('角点交叉或太接近。按左上、右上、右下、左下围住纸面。');return false;}
    change(next);setPicks(null);setMessage('可拖动角点微调，或点「预览成品」检查。');return true;
  };
  const finish=(e:React.PointerEvent<HTMLDivElement>)=>{
    const gesture=drag.current;
    if(!gesture||gesture.pointer!==e.pointerId)return;
    e.stopPropagation();drag.current=null;setGhost(null);
    if(busy)return;
    const end=position(e);
    if(tool==='page'){
      if(picks){const next=[...picks,end];if(next.length===4)accept(next);else setPicks(next);}
      else if(gesture.index>=0)accept(saved.map((p,i)=>i===gesture.index?end:p));
      return;
    }
    if(locks.includes('frame_angle'))return;
    const r=e.currentTarget.getBoundingClientRect(),start=gesture.start;
    if(Math.hypot((end.x-start.x)*r.width,(end.y-start.y)*r.height)<12){setLine([]);setMessage('请沿直边拖出稍长的线。');return;}
    const p=layout.toPlane(start.x,start.y),q=layout.toPlane(end.x,end.y);
    if([p.x,p.y,q.x,q.y].some(n=>!Number.isFinite(n)||n<-.001||n>1.001)){setLine([]);setMessage('请在选定的纸面内画线。');return;}
    const slope=Math.atan2((q.y-p.y)*layout.planeH,(q.x-p.x)*layout.planeW)*180/Math.PI+90*values.frame_turn;
    const axis=Math.round(slope/90),angle=Math.round((90*axis-slope)*10)/10;
    change({...values,frame_angle:angle});setLine([start,end]);
    setMessage('已对齐'+(Math.abs(axis)%2?'垂直':'水平')+'方向，可预览自动裁边。');
  };
  return <div className="frame-overlay frame-marks" role="group" aria-label={tool==='line'?'画线拉正取景区':'纸面四角取景区'}
    onPointerDown={e=>{
      e.stopPropagation();if(busy||e.button!==0||tool==='line'&&locks.includes('frame_angle'))return;
      const p=position(e),handle=(e.target as Element).closest('[data-page-handle]')?.getAttribute('data-page-handle');
      if(tool==='page'&&!picks&&handle==null)return;
      drag.current={start:p,index:handle==null?-1:Number(handle),pointer:e.pointerId};
      if(tool==='line')setLine([p,p]);
      setMessage('');e.currentTarget.setPointerCapture(e.pointerId);e.preventDefault();
    }} onPointerMove={e=>{
      const g=drag.current;if(!g||g.pointer!==e.pointerId||busy)return;
      e.stopPropagation();const p=position(e);
      if(tool==='line')setLine([g.start,p]);else if(g.index>=0)setGhost(saved.map((v,i)=>i===g.index?p:v));
    }} onPointerUp={finish} onPointerCancel={e=>{e.stopPropagation();drag.current=null;setGhost(null);if(tool==='line')setLine([]);}}
    onDoubleClick={e=>e.stopPropagation()}>
    <svg viewBox={'0 0 '+w+' '+h} preserveAspectRatio="none" aria-hidden="true">
      <polygon className="frame-outlined-crop" points={coords(crop)}/>
      {tool==='page'&&points.length===4?<polygon className="frame-paper-guide" points={coords(points)}/>:<polyline points={coords(points)}/>}
    </svg>
    {points.map((p,i)=><span key={i} className="frame-point" data-page-handle={tool==='page'&&picks===null?i:undefined}
      style={{left:p.x*100+'%',top:p.y*100+'%'}}>{tool==='page'?i+1:''}</span>)}
    <div className="frame-mark-note" role="status">{message||(tool==='line'?'沿海平线或门框拖一条线':picks?'点纸面的'+cornerNames[picks.length]:'拖动角点对准纸面')}
      {tool==='page'&&(picks===null||picks.length>0)&&<button className="text-button" onPointerDown={e=>e.stopPropagation()}
        onClick={()=>{setPicks([]);setMessage('');setGhost(null);}}>重新点选四角</button>}</div>
  </div>;
}
