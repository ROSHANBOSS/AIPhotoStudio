type Input = HTMLImageElement | HTMLCanvasElement;
type Point = {x:number;y:number};
type Detector = {detect(image:Input):{faceLandmarks:Point[][]}};
type Vision = {
  FilesetResolver:{forVisionTasks(path:string):Promise<unknown>};
  FaceLandmarker:{createFromOptions(files:unknown, options:object):Promise<Detector>};
};
type Candidate = {points:Point[];cx:number;cy:number;width:number;score:number;views:Set<string>;coarse:boolean};
const maxFaces=16;
let loaded:Promise<Detector>|undefined;

async function detector() {
  if(!loaded)loaded=(async()=>{
    const base=new URL('./BodyVision/',document.baseURI);
    const vision:Vision=await import(/* @vite-ignore */ new URL('vision_bundle.mjs',base).href);
    const files=await vision.FilesetResolver.forVisionTasks(new URL('wasm',base).href);
    return vision.FaceLandmarker.createFromOptions(files,{
      baseOptions:{modelAssetPath:new URL('face_landmarker.task',base).href,delegate:'CPU'},
      runningMode:'IMAGE',numFaces:maxFaces+1,minFaceDetectionConfidence:.55,minFacePresenceConfidence:.62,
      outputFaceBlendshapes:false,outputFacialTransformationMatrixes:false
    });
  })().catch(error=>{loaded=undefined;throw error;});
  return loaded;
}

function encode(points:Point[]) {
  const bytes=new Uint8Array(478*4),view=new DataView(bytes.buffer);
  for(let i=0;i<points.length;i++){
    view.setUint16(i*4,Math.round(points[i].x*65535),true);
    view.setUint16(i*4+2,Math.round(points[i].y*65535),true);
  }
  return btoa(String.fromCharCode(...bytes));
}

async function observations(image:Input) {
  const model=await detector();
  const width=image instanceof HTMLImageElement?image.naturalWidth:image.width;
  const height=image instanceof HTMLImageElement?image.naturalHeight:image.height;
  const candidates:Candidate[]=[];
  function pass(input:Input,x:number,y:number,w:number,h:number,whole:boolean,coarse:boolean) {
    const view=[x,y,w,h].join('/');
    for(const mesh of model.detect(input).faceLandmarks) {
      if(mesh.length!==478||mesh.some(p=>!Number.isFinite(p.x)||!Number.isFinite(p.y)))continue;
      // Crops whose edge crosses the face can invent the missing outline. Use a different overlapping crop.
      const rim=[10,152,234,454,33,263,61,291];
      if(!whole&&rim.some(i=>mesh[i].x<.02||mesh[i].y<.02||mesh[i].x>.98||mesh[i].y>.98))continue;
      const points=mesh.map(p=>({x:(x+p.x*w)/width,y:(y+p.y*h)/height}));
      if(points.some(p=>p.x<0||p.y<0||p.x>1||p.y>1))continue;
      const left=points[33],right=points[263],span=Math.hypot((right.x-left.x)*width,(right.y-left.y)*height);
      if(span<11)continue;
      const length=(a:number,b:number)=>Math.hypot((points[a].x-points[b].x)*width,(points[a].y-points[b].y)*height);
      const breadth=length(234,454),tall=length(10,152);
      const cross=((right.x-left.x)*(points[152].y-points[10].y)-(right.y-left.y)*(points[152].x-points[10].x))*width*height;
      if(Math.min(length(33,133),length(263,362))<Math.max(2.25,.102*span)||span<.365*breadth||span>1.115*breadth||
        tall<.775*span||tall>3.89*span||Math.abs(cross)<.485*span*tall||length(78,308)<.172*span||
        length(1,152)<.182*tall||length(168,1)<.077*tall)continue;
      const cx=(left.x+right.x)*width/2,cy=(left.y+right.y)*height/2;
      const entry={points,cx,cy,width:span,score:span/Math.max(w,h),views:new Set([view]),coarse};
      const old=candidates.find(p=>Math.abs(Math.log(p.width/span))<.55&&Math.hypot(p.cx-cx,p.cy-cy)<.36*Math.min(p.width,span));
      if(!old)candidates.push(entry);
      else {
        old.views.add(view);
        if(old.score<entry.score && (!old.coarse || coarse)) {
          old.points=points;old.cx=cx;old.cy=cy;old.width=span;old.score=entry.score;
        }
        old.coarse ||= coarse;
      }
    }
  }
  pass(image,0,0,width,height,true,true);
  const canvas=document.createElement('canvas');
  const ctx=canvas.getContext('2d')!;
  const shortEdge=Math.min(width,height);
  async function grid(side:number,coarse:boolean) {
    if(side<96)return;
    canvas.width=canvas.height=Math.min(640,side);
    const columns=Math.min(14,Math.ceil((width-side)/(.58*side))+1);
    const rows=Math.min(14,Math.ceil((height-side)/(.58*side))+1);
    for(let row=0;row<rows;row++){
      await new Promise<void>(resolve=>setTimeout(resolve,0));
      for(let column=0;column<columns;column++){
        const x=Math.round(column*(width-side)/Math.max(1,columns-1)),y=Math.round(row*(height-side)/Math.max(1,rows-1));
        ctx.drawImage(image,x,y,side,side,0,0,canvas.width,canvas.height);
        pass(canvas,x,y,side,side,false,coarse);
      }
    }
  }
  await grid(Math.min(shortEdge,Math.round(Math.max(width,height)*.55)),true);
  if(!candidates.length || candidates.length>1 || candidates[0].width<shortEdge*.11) {
    // A distant face occupies only a few input pixels in the whole-frame graph.
    // Read overlapping tiles at two scales from the original detector input.
    await grid(Math.round(shortEdge*.44),false);
    await grid(Math.round(shortEdge*.27),false);
    for(const face of candidates.slice()){
      if(face.coarse || face.views.size>=2)continue;
      const side=Math.min(shortEdge,Math.max(112,Math.round(face.width*5)));
      const x=Math.round(Math.min(width-side,Math.max(0,face.cx-side/2)));
      const y=Math.round(Math.min(height-side,Math.max(0,face.cy-side*.42)));
      canvas.width=canvas.height=Math.min(640,side);
      ctx.drawImage(image,x,y,side,side,0,0,canvas.width,canvas.height);
      pass(canvas,x,y,side,side,false,false);
    }
  }
  const confirmed=candidates.filter(face=>face.coarse || face.views.size>=2).sort((a,b)=>a.cx-b.cx||a.cy-b.cy);
  return {width,height,faces:confirmed};
}

export async function findPortraitAnchors(image:Input) {
  const {faces,width,height}=await observations(image);
  return faces.map(face=>{
    const p=face.points;
    const distance=(a:number,b:number)=>Math.hypot((p[a].x-p[b].x)*width,(p[a].y-p[b].y)*height);
    return {x:face.cx/width,y:face.cy/height,span:face.width/width,noseX:p[1].x,noseY:p[1].y,
      faceWidth:distance(234,454)/width,faceHeight:distance(10,152)/height};
  });
}

export async function detectPortraits(image:Input) {
  const {width,height,faces}=await observations(image);
  if(faces.length>maxFaces)throw new Error('识别到超过 '+maxFaces+' 张脸，请使用包含目标人物的裁切副本。');
  return {width,height,meshes:faces.map(face=>encode(face.points))};
}
