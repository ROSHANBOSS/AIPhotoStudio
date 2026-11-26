import type {BrushStroke, NormalizedPoint} from './model';
import {gradientSampler} from './gradient';
import type {GradientMask} from './gradient';
import {savedEdge} from './brush-edge';
import type {EdgeTest} from './brush-edge';

// Coordinates and falloff match the lattice in native/LocalEdits.Grid.
export type MaskCoverage = {
  imageWidth:number; imageHeight:number; step:number; width:number; height:number;
  paint:Float32Array; protect:Float32Array; strokes:BrushStroke[]; hasPaint:boolean; prediction?:string|null;guard?:string|null;gradient?:GradientMask|null;
};

export const isPaint=(kind:BrushStroke['kind'])=>kind==='paint'||kind==='maskPaint';
export const isErase=(kind:BrushStroke['kind'])=>kind==='erase'||kind==='maskErase';
export const isMaskStroke=(kind:BrushStroke['kind'])=>isPaint(kind)||isErase(kind)||kind==='protect';

// Store a stroke's maximum weight so doubling back in one gesture does not add opacity.
export function addMaskSegment(grid:MaskCoverage, weights:Float32Array, stroke:BrushStroke, start:NormalizedPoint, end:NormalizedPoint,edge=savedEdge(stroke)) {
  const {imageWidth,imageHeight,width,height,step}=grid;
  const x0=start.x*(imageWidth-1)/step, y0=start.y*(imageHeight-1)/step;
  const x1=end.x*(imageWidth-1)/step, y1=end.y*(imageHeight-1)/step;
  const dx=x1-x0, dy=y1-y0, length=dx*dx+dy*dy;
  const radius=stroke.radius*Math.min(imageWidth,imageHeight)/step, r2=radius*radius;
  const left=Math.max(0,Math.floor(Math.min(x0,x1)-radius)), right=Math.min(width-1,Math.ceil(Math.max(x0,x1)+radius));
  const top=Math.max(0,Math.floor(Math.min(y0,y1)-radius)), bottom=Math.min(height-1,Math.ceil(Math.max(y0,y1)+radius));
  if(!r2||stroke.strength===0)return;
  for(let y=top;y<=bottom;y++)for(let x=left;x<=right;x++){
    const index=y*width+x, px=x-x0, py=y-y0;
    if(weights[index]>=stroke.strength)continue;
    const t=length?Math.max(0,Math.min(1,(px*dx+py*dy)/length)):0;
    const ax=px-t*dx, ay=py-t*dy, distance=ax*ax+ay*ay;
    if(distance>=r2)continue;
    if(edge&&!edge((x*step+.5)/imageWidth,(y*step+.5)/imageHeight))continue;
    const fall=Math.min(1,(1-Math.sqrt(distance/r2))*3);
    weights[index]=Math.max(weights[index],fall*fall*(3-2*fall)*stroke.strength);
  }
}

export function brushCoverage(imageWidth:number,imageHeight:number,strokes:BrushStroke[],previous?:MaskCoverage|null,prediction?:string|null,guard?:string|null,gradient?:GradientMask|null):MaskCoverage {
  const step=Math.max(.5,Math.min(imageWidth,imageHeight)/220,Math.max(imageWidth,imageHeight)/1000);
  const width=Math.ceil(imageWidth/step)+2, height=Math.ceil(imageHeight/step)+2;
  // Painting appends strokes. Reuse the previous fields even if the last gesture erased pixels.
  const base=previous && previous.imageWidth===imageWidth && previous.imageHeight===imageHeight && previous.prediction===prediction && previous.guard===guard && previous.gradient===gradient &&
    previous.strokes.length<=strokes.length && previous.strokes.every((s,i)=>s===strokes[i])?previous:null;
  if(base?.strokes===strokes)return base;
  const paint=base?base.paint.slice():new Float32Array(width*height);
  const protect=base?base.protect.slice():new Float32Array(width*height);
  const grid={imageWidth,imageHeight,width,height,step,paint,protect,strokes,hasPaint:false,prediction,guard,gradient};
  function loadPlane(data:string,field:Float32Array,invert:boolean){
    const alpha=atob(data),side=256;
    for(let y=0;y<height;y++)for(let x=0;x<width;x++){
      const u=Math.min(side-1,Math.max(0,(x*step+.5)/imageWidth*side-.5)),v=Math.min(side-1,Math.max(0,(y*step+.5)/imageHeight*side-.5));
      const a=Math.floor(u),b=Math.floor(v),a2=Math.min(side-1,a+1),b2=Math.min(side-1,b+1),fx=u-a,fy=v-b;
      const value=((alpha.charCodeAt(b*side+a)*(1-fx)+alpha.charCodeAt(b*side+a2)*fx)*(1-fy)+
        (alpha.charCodeAt(b2*side+a)*(1-fx)+alpha.charCodeAt(b2*side+a2)*fx)*fy)/255;
      field[y*width+x]=invert?1-value:value;
    }
  }
  if(!base&&prediction)loadPlane(prediction,paint,false);
  if(!base&&gradient){
    const sample=gradientSampler(gradient,imageWidth,imageHeight);
    for(let y=0;y<height;y++)for(let x=0;x<width;x++)paint[y*width+x]=sample(x*step,y*step);
  }
  if(!base&&guard)loadPlane(guard,protect,true);
  const weights=new Float32Array(width*height);
  for(let n=base?.strokes.length||0;n<strokes.length;n++){
    const stroke=strokes[n];
    if(!isMaskStroke(stroke.kind)||!stroke.strength)continue;
    weights.fill(0);
    stroke.points.forEach((point,i)=>addMaskSegment(grid,weights,stroke,stroke.points[Math.max(0,i-1)],point));
    const field=stroke.kind==='protect'?protect:paint;
    for(let i=0;i<field.length;i++){
      if(isErase(stroke.kind))field[i]*=1-weights[i];
      else field[i]+=weights[i]*(1-field[i]);
    }
  }
  grid.hasPaint=paint.some((alpha,i)=>alpha*(1-protect[i])>.001);
  return grid;
}

export type MaskTrace={kind:BrushStroke['kind'];weights:Float32Array;edge?:EdgeTest};
export type MaskDisplay='pink'|'cyan'|'bw';

export function displayMask(context:CanvasRenderingContext2D, bitmap:HTMLCanvasElement, grid:MaskCoverage, showPaint:boolean, showProtect:boolean, trace?:MaskTrace, mode:MaskDisplay='pink') {
  if(mode==='bw'){
    context.fillStyle='black';
    context.fillRect(0,0,context.canvas.width,context.canvas.height);
  }
  if(!grid.prediction&&!grid.gradient&&!grid.strokes.length&&!trace)return;
  if(bitmap.width!==grid.width||bitmap.height!==grid.height){bitmap.width=grid.width;bitmap.height=grid.height;}
  const raster=bitmap.getContext('2d');
  if(!raster)return;
  const image=raster.createImageData(grid.width,grid.height);
  const [red,green,blue]=mode==='cyan'?[26,219,242]:[255,55,126];
  for(let i=0;i<grid.paint.length;i++){
    let p=grid.paint[i],q=grid.protect[i];
    if(trace){
      const a=trace.weights[i];
      if(isErase(trace.kind))p*=1-a;
      else if(isPaint(trace.kind))p+=a*(1-p);
      else q+=a*(1-q);
    }
    const weight=showPaint?p*(1-q):0,index=i*4;
    if(mode==='bw'){
      const gray=Math.round(255*weight);
      image.data[index]=gray;image.data[index+1]=gray;image.data[index+2]=gray;image.data[index+3]=255;
      continue;
    }
    const paint=weight*.54, mint=showProtect?q*.48:0;
    const alpha=mint+paint*(1-mint);
    if(!alpha)continue;
    const mix=mint/alpha;
    image.data[index]=red+(97-red)*mix;
    image.data[index+1]=green+(209-green)*mix;
    image.data[index+2]=blue+(190-blue)*mix;
    image.data[index+3]=Math.round(255*alpha);
  }
  raster.putImageData(image,0,0);
  // Canvas texel centres correspond to grid nodes at 0, step, 2*step … image pixels.
  const sx=context.canvas.width/grid.imageWidth,sy=context.canvas.height/grid.imageHeight;
  context.drawImage(bitmap,(.5-grid.step/2)*sx,(.5-grid.step/2)*sy,grid.width*grid.step*sx,grid.height*grid.step*sy);
}
