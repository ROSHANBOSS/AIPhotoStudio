import {Values} from './model';
import {pageKeys,pageLayout} from './frame-page';

export const frameKeys=['frame_turn','frame_mirror','frame_angle','frame_ratio','frame_trim_x','frame_trim_y','frame_pan_x','frame_pan_y',...pageKeys] as const;
export const frameRatios=['自由','16:9','9:16','4:5','1:1'];
export function frameValues(values?:Values|null):Values {
  return Object.fromEntries(frameKeys.map(key=>[key,values?.[key]??0]));
}
export function frameChanges(values:Values,base?:Values|null) {
  return Object.fromEntries(frameKeys.filter(key=>values[key]!== (base?.[key]??0)).map(key=>[key,values[key]]));
}

// The image plane uses normalized edge coordinates. Keep rounding and exact
// quadrant rotations in sync with the native sampler in FrameGeometry.Resolve.
export function frameLayout(sourceWidth:number,sourceHeight:number,values?:Values|null) {
  const p=frameValues(values);
  const page=pageKeys.some(key=>p[key]!==0)?pageLayout(sourceWidth,sourceHeight,p):null;
  const w=Math.max(1,page?.width??sourceWidth),h=Math.max(1,page?.height??sourceHeight);
  const planar=(x:number,y:number)=>page?.toPage(x,y)??{x,y};
  const turn=p.frame_turn,angle=p.frame_angle,ratio=p.frame_ratio;
  const rad=angle*Math.PI/180,u=Math.cos(rad),v=Math.sin(rad);
  const c=[u,-v,-u,v][turn],s=[v,u,-v,-u][turn],ac=Math.abs(c),as=Math.abs(s);
  const rw=[turn%2===0?w:h,16,9,4,1][ratio],rh=[turn%2===0?h:w,9,16,5,1][ratio];
  const fit=Math.min(w/(ac*rw+as*rh),h/(as*rw+ac*rh));
  const sx=1-p.frame_trim_x/100,sy=ratio===0?1-p.frame_trim_y/100:sx;
  const units=ratio>0&&fit*sx>=1?Math.floor(fit*sx+1e-9):0;
  const width=units?rw*units:Math.max(1,Math.floor(rw*fit*sx+1e-9));
  const height=units?rh*units:Math.max(1,Math.floor(rh*fit*sy+1e-9));
  const bw=ac*width+as*height,bh=as*width+ac*height;
  const travelX=Math.max(0,w-bw),travelY=Math.max(0,h-bh);
  const left=travelX*(p.frame_pan_x+100)/200,top=travelY*(p.frame_pan_y+100)/200;
  const cx=(angle===0?Math.floor(left+.5):left)+bw/2,cy=(angle===0?Math.floor(top+.5):top)+bh/2;
  return {width,height,planeW:w,planeH:h,toPlane:planar,active:frameKeys.some(key=>p[key]!==0),travelX,travelY,cx,cy,maxWidth:rw*fit,maxHeight:rh*fit,
    toSource:(x:number,y:number)=>{
      if(p.frame_mirror)x=1-x;
      const u=(cx+c*(x-.5)*width+s*(y-.5)*height)/w,v=(cy-s*(x-.5)*width+c*(y-.5)*height)/h;
      return page?.toSource(u,v)??{x:u,y:v};
    },
    toOutput:(x:number,y:number)=>{
      const q=planar(x,y);
      const outX=.5+(c*(q.x*w-cx)-s*(q.y*h-cy))/width;
      return {x:p.frame_mirror?1-outX:outX,y:.5+(s*(q.x*w-cx)+c*(q.y*h-cy))/height};
    },
  };
}
