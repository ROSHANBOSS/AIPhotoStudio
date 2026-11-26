import React,{useEffect,useRef,useState} from 'react';
import {invoke,messageOf} from './model';
import type {NormalizedPoint,Preview} from './model';

export type RangeKind='colorRange'|'luminanceRange';
export type RangeSettings={red:number;green:number;blue:number;tolerance:number;low:number;high:number;feather:number};
export type RangePreview={kind:RangeKind;range:RangeSettings;creating:boolean};
export const isRangeKind=(kind?:string):kind is RangeKind=>kind==='colorRange'||kind==='luminanceRange';
export const rangeLabel=(kind:RangeKind)=>kind==='colorRange'?'颜色范围':'亮度范围';
type RangeReply={range:RangeSettings;alpha:string;covered:number};
type RangeJob=RangePreview&{key:string;pending:boolean;sampled:boolean;alpha?:string;covered?:number;token?:string;reference?:Preview|null;error?:string};

export function useRangeMask(id:string|undefined,revision:number,enabled:boolean){
  const [state,setState]=useState<RangeJob|null>(null);
  const sequence=useRef(0),activeKey=useRef('');
  const key=enabled&&id?id+'@'+revision:'';
  activeKey.current=key;
  const job=state&&state.key===key?state:null;
  function cancel(){sequence.current++;setState(null);}
  useEffect(()=>{cancel();},[key]);
  function preview(next:RangeJob,point?:NormalizedPoint){
    if(!id||!next.token||activeKey.current!==next.key)return;
    const seq=++sequence.current;
    const waiting=!next.sampled&&!point;
    setState({...next,error:undefined,pending:!waiting});
    if(waiting)return;
    void invoke<RangeReply>('maskRangePreview',{id,revision,kind:next.kind,range:next.range,token:next.token,point})
      .then(reply=>{if(sequence.current===seq&&activeKey.current===next.key)setState({...next,...reply,sampled:true,pending:false,error:undefined});})
      .catch(e=>{if(sequence.current===seq&&activeKey.current===next.key)setState({...next,pending:false,error:messageOf(e)});});
  }
  function start(kind:RangeKind,saved?:RangeSettings,point?:NormalizedPoint){
    if(!key||!id)return;
    const seq=++sequence.current;
    const next:RangeJob={key,kind,creating:!saved,pending:true,sampled:!!saved||kind==='luminanceRange',
      range:saved??{red:0,green:0,blue:0,tolerance:12,low:25,high:55,feather:8}};
    setState(next);
    void invoke<{token:string;reference?:Preview|null}>('maskRangeInput',{id,revision,kind}).then(reply=>{
      if(sequence.current===seq&&activeKey.current===key)preview({...next,...reply},point);
    }).catch(e=>{if(sequence.current===seq&&activeKey.current===key)setState({...next,pending:false,error:messageOf(e)});});
  }
  return {job,start,cancel,pick:(p:NormalizedPoint)=>{if(job)preview(job,p);},
    change:(range:RangeSettings)=>{if(job)preview({...job,range});}};
}

export function RangePanel({job,busy,visible,canApply,change,apply,cancel}:{
  job:RangeJob;busy:boolean;visible:boolean;canApply:boolean;change:(r:RangeSettings)=>void;
  apply:()=>void;cancel:()=>void
}){
  const r=job.range,color=job.kind==='colorRange';
  const slider=(title:string,field:'tolerance'|'low'|'high'|'feather',min=0,max=100)=>
    <label>{title} <span>{r[field]}%</span><input type="range" aria-label={title} min={min} max={max} step="1" value={r[field]}
      disabled={busy||!job.token} onChange={e=>change({...r,[field]:Number(e.target.value)})}/></label>;
  return <div className="mask-list gradient-editor"><h3>{rangeLabel(job.kind)}</h3>
    <p>{color?'在原片上点取目标颜色。容差越大，纳入的相近颜色越多。':
      '拖动上下限选择暗部、中间调或亮部。点原片可把这段范围移到取样亮度。'} 羽化控制边缘过渡。</p>
    {color&&job.sampled&&<p><i aria-hidden="true" style={{display:'inline-block',verticalAlign:'middle',width:16,height:16,border:'1px solid #999',
      backgroundColor:'rgb('+[r.red,r.green,r.blue].join(',')+')'}}/>　取样 RGB {r.red} / {r.green} / {r.blue}</p>}
    <div className="brush-settings">{color?slider('颜色容差','tolerance'):<>{slider('亮度下限','low',0,r.high)}{slider('亮度上限','high',r.low)}</>}
      {slider('范围羽化','feather')}</div>
    {job.error?<p role="alert" className="field-error">{job.error}</p>:<p role="status">{!job.token?'正在读取原片…':!job.sampled?'请在照片上点击取样。':
      job.pending?'正在更新范围…':job.covered===0?'当前范围没有覆盖，请重新取样或扩大范围。':visible?'画布显示覆盖，可再次点击取样。':'正在查看当前范围内的成片效果。'}</p>}
    <div className="segment-actions"><button className="primary small" disabled={busy||job.pending||!job.covered||!!job.error||!canApply} onClick={apply}>应用范围</button>
      <button className="secondary small" disabled={busy} onClick={cancel}>取消范围</button></div>
  </div>;
}
