import type {Values} from './model';

export type FramePoint={x:number;y:number};
export const pageKeys=['frame_doc_tlx','frame_doc_tly','frame_doc_trx','frame_doc_try','frame_doc_brx','frame_doc_bry','frame_doc_blx','frame_doc_bly','frame_doc_ratio'] as const;
export const pageRatios=['估计纸面比例','A4 竖向','A4 横向','正方形','4:3 横向','3:4 竖向'];
const ratios=[0,210/297,297/210,1,4/3,3/4];
const unit=[{x:0,y:0},{x:1,y:0},{x:1,y:1},{x:0,y:1}];

export function pageCorners(values:Values):FramePoint[] {
  return unit.map(({x,y},i)=>({x:x+(x?-1:1)*(values[pageKeys[i*2]]??0)/100,
    y:y+(y?-1:1)*(values[pageKeys[i*2+1]]??0)/100}));
}
export function cornerValues(values:Values,points:FramePoint[],locks:string[]) {
  const next={...values};
  points.forEach((p,i)=>{
    for(const [offset,axis] of (['x','y'] as const).entries()){
      const key=pageKeys[2*i+offset];
      if(!locks.includes(key))next[key]=Math.round(Math.abs(p[axis]-unit[i][axis])*10000)/100;
    }
  });
  return next;
}
export function pageLayout(w:number,h:number,values:Values) {
  const p=pageCorners(values),r=values.frame_doc_ratio??0;
  for(let i=0;i<4;i++){
    const a=p[i],b=p[(i+1)%4],c=p[(i+2)%4];
    if((b.x-a.x)*(c.y-b.y)-(b.y-a.y)*(c.x-b.x)<=.0001)return null;
  }
  const [p0,p1,p2,p3]=p;
  const dx1=p1.x-p2.x,dy1=p1.y-p2.y,dx2=p3.x-p2.x,dy2=p3.y-p2.y;
  const dx3=p0.x-p1.x+p2.x-p3.x,dy3=p0.y-p1.y+p2.y-p3.y,det=dx1*dy2-dx2*dy1;
  const g=(dx3*dy2-dx2*dy3)/det,k=(dx1*dy3-dx3*dy1)/det;
  const denominators=[1,1+g,1+k,1+g+k];
  if(Math.min(...denominators)<.002*Math.max(...denominators))return null;
  const a=(1+g)*p1.x-p0.x,b=(1+k)*p3.x-p0.x,d=(1+g)*p1.y-p0.y,e=(1+k)*p3.y-p0.y;
  const length=(i:number,j:number)=>Math.hypot((p[i].x-p[j].x)*w,(p[i].y-p[j].y)*h);
  let width=(length(0,1)+length(3,2))/2,height=(length(0,3)+length(1,2))/2;
  if(ratios[r]){width=Math.min(width,height*ratios[r]);height=width/ratios[r];}
  return {width:Math.max(1,Math.floor(width+.5)),height:Math.max(1,Math.floor(height+.5)),
    toSource:(u:number,v:number)=>({x:(a*u+b*v+p0.x)/(g*u+k*v+1),y:(d*u+e*v+p0.y)/(g*u+k*v+1)}),
    toPage:(x:number,y:number)=>{
      const ax=a-g*x,bx=b-k*x,ay=d-g*y,by=e-k*y,den=ax*by-ay*bx;
      return {x:((x-p0.x)*by-bx*(y-p0.y))/den,y:(ax*(y-p0.y)-(x-p0.x)*ay)/den};
    }};
}
