import React, {useState} from 'react';
import {Photo, invoke, messageOf} from './model';
import {Thumb} from './preview';

type Result = {status:'measured'|'transparent'|'untagged'|'unsupported'|'camera'; samples?:number; outside?:number; overlay?:string};
function summary(result:Result) {
  switch(result.status) {
    case 'untagged': return '原片没有色彩配置，编辑时按 sRGB 解释。';
    case 'unsupported': return '此配置或像素格式暂不支持原片色域检查。';
    case 'camera': return 'RAW 使用相机颜色矩阵，目前只能检查内嵌 RGB 配置的照片。';
    case 'transparent': return '抽到的位置均为全透明，无法估计画面颜色。';
    default: {
      const samples=result.samples!, outside=result.outside!;
      const percent=outside/samples*100;
      const share=percent<.1?'不足 0.1%':percent.toFixed(1)+'%';
      return outside
        ? `抽到的 ${samples.toLocaleString('zh-CN')} 个可见点中，${outside.toLocaleString('zh-CN')} 个（${share}）超出 sRGB。这些颜色进入当前编辑空间时会受限，另存为 Adobe RGB 也无法恢复。`
        : `抽到的 ${samples.toLocaleString('zh-CN')} 个可见点均在 sRGB 范围内。少量局部颜色可能没有被抽到。`;
    }
  }
}

export function InputGamut({photo}:{photo:Photo}) {
  const [pending,setPending]=useState(false);
  const [result,setResult]=useState<Result>();
  const [error,setError]=useState('');
  async function inspect() {
    setPending(true); setError('');
    try { setResult(await invoke<Result>('inputGamut',{id:photo.id})); }
    catch(e) { setError(messageOf(e)); }
    finally { setPending(false); }
  }
  return <><dt>编辑色域</dt><dd>sRGB · 16 位</dd><dt>原片色域</dt><dd>
    {result ? <span role="status" data-gamut-state={result.status}>{summary(result)}</span> :
      <button className="secondary" disabled={pending} onClick={inspect}>{pending?'检查中…':'检查原片色域'}</button>}
    {result?.overlay&&<details className="gamut-map"><summary>查看原片中的位置</summary>
      <div style={{width:Math.min(235,180*photo.width/photo.height),aspectRatio:photo.width+'/'+photo.height}}>
        <Thumb photo={photo}/><img src={result.overlay} alt="超出 sRGB 的抽样位置"/>
      </div><small>紫色标出超范围样点的大致位置。</small>
    </details>}
    {error&&<span className="field-error" role="alert">{error}</span>}
  </dd></>;
}
