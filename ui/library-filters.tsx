import React from 'react';
import type {Photo} from './model';

export type LibraryCriteria={dates:'any'|'known'|'unknown';from:string;to:string;edits:'any'|'edited'|'untouched'};
export const allCriteria:LibraryCriteria={dates:'any',from:'',to:'',edits:'any'};

export function captureDay(photo:Photo) {
  const m=/^(\d{4})[:-](\d{2})[:-](\d{2})(?:$|[ T])/.exec(String(photo.dateTaken??'').trim());
  if(!m)return '';
  const iso=m.slice(1).join('-'),date=new Date(iso+'T12:00:00Z');
  return !Number.isNaN(date.valueOf())&&date.toISOString().slice(0,10)===iso?iso:'';
}
export function matchesCriteria(photo:Photo,criteria:LibraryCriteria) {
  if(criteria.edits!=='any'&&photo.hasEdits!==(criteria.edits==='edited'))return false;
  if(criteria.dates==='any')return true;
  const day=photo.libraryDate||captureDay(photo);
  if(criteria.dates==='unknown')return !day;
  const start=day.length===4?day+'-01-01':day.length===7?day+'-01':day;
  let last=day;
  if(day.length===4)last=day+'-12-31';
  if(day.length===7){
    const end=new Date(start+'T12:00:00Z');
    end.setUTCMonth(end.getUTCMonth()+1,0);
    last=end.toISOString().slice(0,10);
  }
  return !!day&&(!criteria.from||last>=criteria.from)&&(!criteria.to||start<=criteria.to);
}
export function LibraryCriteriaControls({value,change}:{value:LibraryCriteria;change:(v:LibraryCriteria)=>void}) {
  return <div className="library-criteria"><label>拍摄日期 <select aria-label="拍摄日期" value={value.dates} onChange={e=>change({...value,dates:e.target.value as LibraryCriteria['dates']})}>
    <option value="any">全部日期</option><option value="known">按日期范围</option><option value="unknown">缺少拍摄日期</option></select></label>
    {value.dates==='known'&&<><input aria-label="拍摄开始日期" type="date" max={value.to||undefined} value={value.from} onChange={e=>change({...value,from:e.target.value})}/>
    <span>至</span><input aria-label="拍摄结束日期" type="date" min={value.from||undefined} value={value.to} onChange={e=>change({...value,to:e.target.value})}/></>}
    <label title="按当前保存的调色、构图和手绘蒙版判断。单独识别人脸、收藏或锁定参数不会计为已修。">修图状态 <select aria-label="修图状态" value={value.edits} onChange={e=>change({...value,edits:e.target.value as LibraryCriteria['edits']})}>
      <option value="any">全部状态</option><option value="edited">已修</option><option value="untouched">未修</option></select></label>
    {(value.edits!=='any'||value.dates!=='any')&&<button className="text-button" onClick={()=>change(allCriteria)}>清除日期和状态</button>}
    {value.dates==='known'&&value.to&&value.from>value.to&&<span role="alert">结束日期应在开始日期之后或同一天。</span>}
  </div>;
}
