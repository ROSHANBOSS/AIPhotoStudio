import React from 'react';

export type PaperView='canvas'|'checker'|'white'|'black';
export const mayHaveAlpha=(name:string)=>/\.(png|tiff?|webp|gif|bmp|heic|heif|hif|avif)$/i.test(name);

// The export pipeline composites in linear sRGB; use the same transfer for the solid-colour proofs.
export function MatteFilter({id,color}:{id:string;color:string}){
  return <svg width="0" height="0" className="matte-filter" focusable="false" aria-hidden="true"><defs>
    <filter id={id} x="0%" y="0%" width="100%" height="100%" colorInterpolationFilters="linearRGB">
      <feFlood floodColor={color} result="paper"/><feComposite in="SourceGraphic" in2="paper" operator="over"/>
    </filter></defs></svg>;
}

export function TransparencyControl({view,change}:{view:PaperView;change:(v:PaperView)=>void}){
  return <label className="paper-control">透明底<select aria-label="透明底预览" value={view} onChange={e=>change(e.target.value as PaperView)}>
    <option value="canvas">画布</option><option value="checker">棋盘格</option><option value="white">白底</option><option value="black">黑底</option>
  </select><MatteFilter id="proof-paper-white" color="#fff"/><MatteFilter id="proof-paper-black" color="#000"/></label>;
}
