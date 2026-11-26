import { findPortraitAnchors } from './portrait-detector';

type PosePoint = { x: number; y: number; visibility: number };
type MediaPipeResult = {
  landmarks: PosePoint[][];
  segmentationMasks?: { width: number; height: number; getAsFloat32Array(): Float32Array }[];
};
type Landmarker = { detect(image: HTMLImageElement | HTMLCanvasElement, consume: (result: MediaPipeResult) => void): void };
type VisionModule = {
  FilesetResolver: { forVisionTasks(path: string): Promise<unknown> };
  PoseLandmarker: { createFromOptions(fileset: unknown, options: object): Promise<Landmarker> };
};
export type BodySubject = { points: PosePoint[]; mask: string };
export type BodyResult = BodySubject & { people: number; subjects: BodySubject[]; unlocatedFaces: number[] };
type Input = HTMLImageElement | HTMLCanvasElement;
type Box = { x: number; y: number; width: number; height: number };
type Position = { x: number; y: number };
type Layout = 'plain' | 'square' | 'upper';
type View = { name: string; region: Box; edge: number; layout: Layout; whole: boolean };
const maskSide = 128, maxPeople = 16;

let instance: Promise<Landmarker> | undefined;
async function getDetector(): Promise<Landmarker> {
  if (!instance) {
    instance = (async () => {
      const base = new URL('./BodyVision/', document.baseURI);
      const runtime = new URL('vision_bundle.mjs', base).href;
      const vision: VisionModule = await import(/* @vite-ignore */ runtime);
      const fileset = await vision.FilesetResolver.forVisionTasks(new URL('wasm', base).href);
      return vision.PoseLandmarker.createFromOptions(fileset, {
        baseOptions: { modelAssetPath: new URL('pose_landmarker_lite.task', base).href, delegate: 'CPU' },
        runningMode: 'IMAGE', numPoses: maxPeople + 1, outputSegmentationMasks: true,
        minPoseDetectionConfidence: .5, minPosePresenceConfidence: .55, minTrackingConfidence: .5
      });
    })().catch(error => { instance = undefined; throw error; });
  }
  return instance;
}

type Framing = { canvas: HTMLCanvasElement; x: number; y: number; width: number; height: number };

function alignedCanvas(image: Input, view: View): Framing {
  const { region, edge, layout } = view, square = layout !== 'plain';
  // A centred fit supports full bodies. The upper-body fallback leaves context below a cropped torso.
  const maxScale = view.whole || !square ? 1 : 4;
  const scale = layout === 'upper' ? Math.min(maxScale, edge * .78 / region.width, edge * .62 / region.height) :
    Math.min(maxScale, edge * (square ? .84 : 1) / Math.max(region.width, region.height));
  const width = Math.max(1, Math.round(region.width * scale)), height = Math.max(1, Math.round(region.height * scale));
  const canvas = document.createElement('canvas');
  // ImageFrame uses 16-byte row alignment; a single-channel float mask needs four pixels per block.
  canvas.width = square ? Math.ceil(edge / 4) * 4 : Math.ceil(width / 4) * 4;
  canvas.height = square ? canvas.width : height;
  const x = Math.floor((canvas.width - width) / 2);
  const y = layout === 'upper' ? Math.floor(edge * .05) : Math.floor((canvas.height - height) / 2);
  const context = canvas.getContext('2d');
  if (!context) throw new Error('无法准备人体识别图像。');
  context.fillStyle = square ? '#eee' : '#fff'; context.fillRect(0, 0, canvas.width, canvas.height);
  context.drawImage(image, region.x, region.y, region.width, region.height, x, y, width, height);
  return { canvas, x, y, width, height };
}

// Map each 128-grid cell from the original photo into this view's mask. Cells straddling a crop
// boundary receive only their covered fraction. Padded canvas pixels never enter the returned mask.
function sampleAxis(photo: number, start: number, span: number, offset: number, drawn: number, canvas: number, pixels: number) {
  return Array.from({length:maskSide}, (_, i) => {
    const a = Math.max(start, i * photo / maskSide), b = Math.min(start + span, (i + 1) * photo / maskSide);
    const coverage = Math.max(0, (b-a)*maskSide/photo);
    const lo = Math.max(0, Math.min(pixels-1, Math.floor((offset + drawn*(a-start)/span)*pixels/canvas)));
    const hi = Math.min(pixels, Math.max(lo+1, Math.floor((offset + drawn*(b-start)/span)*pixels/canvas)));
    return { lo, hi, coverage };
  });
}

function sampleMask(source: Float32Array, width: number, height: number, frame: Framing, photo: Box, region: Box): Uint8Array {
  if (width < 1 || height < 1 || source.length !== width * height) throw new Error('模型未返回有效的人体分割。');
  const xs = sampleAxis(photo.width, region.x, region.width, frame.x, frame.width, frame.canvas.width, width);
  const ys = sampleAxis(photo.height, region.y, region.height, frame.y, frame.height, frame.canvas.height, height);
  const bytes = new Uint8Array(maskSide*maskSide);
  for (let y = 0; y < maskSide; y++) {
    const row = ys[y];
    if (!row.coverage) continue;
    for (let x = 0; x < maskSide; x++) {
      const col = xs[x];
      if (!col.coverage) continue;
      let sum = 0;
      for (let v = row.lo; v < row.hi; v++)
        for (let u = col.lo; u < col.hi; u++) sum += source[v * width + u];
      if (!Number.isFinite(sum)) throw new Error('模型返回了无法解释的分割概率。');
      const mean = sum/((row.hi-row.lo)*(col.hi-col.lo));
      bytes[y*maskSide+x] = Math.round(255 * Math.max(0, Math.min(1, mean * row.coverage * col.coverage)));
    }
  }
  return bytes;
}

function alpha(mask: Uint8Array, p: Position): number {
  if (p.x < 0 || p.x >= 1 || p.y < 0 || p.y >= 1) return 0;
  return mask[Math.floor(p.y*maskSide)*maskSide+Math.floor(p.x*maskSide)];
}

const middle = (a: Position, b: Position): Position => ({x:(a.x+b.x)/2, y:(a.y+b.y)/2});
const distance = (a: Position, b: Position, photo: Box) => Math.hypot((a.x-b.x)*photo.width,(a.y-b.y)*photo.height);

type Observation = {
  points: PosePoint[]; mask: Uint8Array; view: View; area: number; bounds: Box;
  shoulders: Position; span: number; faceVisible: boolean; clipped: boolean; upperQuality: number;
};

function observe(points: PosePoint[], mask: Uint8Array, view: View, photo: Box): Observation | undefined {
  const supports = (i: number, visibility = .55) => points[i].visibility >= visibility && alpha(mask,points[i]) >= 76;
  const shoulders = middle(points[11],points[12]), span = distance(points[11],points[12],photo);
  const faceVisible = [0,2,5,7,8].filter(i=>supports(i)).length >= 3;
  if (!supports(11) || !supports(12) || span < Math.max(5,Math.min(photo.width,photo.height)*.013)) return;
  if (!faceVisible && !(supports(23) && supports(24) && (supports(13) || supports(14)))) return;
  let left=maskSide, right=-1, top=maskSide, bottom=-1, area=0;
  for(let i=0;i<mask.length;i++) if(mask[i]>=128) {
    const x=i%maskSide, y=Math.floor(i/maskSide);
    left=Math.min(left,x); right=Math.max(right,x); top=Math.min(top,y); bottom=Math.max(bottom,y); area++;
  }
  if(area<18 || right-left<2 || bottom-top<2) return;
  const bounds={x:left/maskSide, y:top/maskSide, width:(right-left+1)/maskSide, height:(bottom-top+1)/maskSide};
  const r=view.region;
  const clipped = !view.whole && ((r.x>0 && left<=Math.ceil(r.x/photo.width*maskSide)) ||
    (r.y>0 && top<=Math.ceil(r.y/photo.height*maskSide)) ||
    (r.x+r.width<photo.width && right>=Math.floor((r.x+r.width)/photo.width*maskSide)-1) ||
    (r.y+r.height<photo.height && bottom>=Math.floor((r.y+r.height)/photo.height*maskSide)-1));
  const upperQuality=[0,11,12,13,14,15,16,23,24].reduce((sum,i)=>
    sum+points[i].visibility*Math.min(1,alpha(mask,points[i])/128),0)/9;
  return {points,mask,view,area,bounds,shoulders,span,faceVisible,clipped,upperQuality};
}

function readView(detector: Landmarker, image: Input, view: View, photo: Box): Observation[] {
  const frame = alignedCanvas(image,view), r = view.region;
  const output: Observation[] = [];
  detector.detect(frame.canvas, result => {
    const masks=result.segmentationMasks;
    if(result.landmarks.length && masks?.length!==result.landmarks.length)
      throw new Error('人体模型返回的人物与分割数量不匹配。');
    result.landmarks.forEach((landmarks,index)=>{
      if(landmarks.length!==33) throw new Error('人体模型未返回完整的关键点。');
      const points=landmarks.map(({x,y,visibility})=>{
        if(![x,y,visibility].every(Number.isFinite)) throw new Error('人体模型未返回有效的坐标或可见度。');
        return { x:(r.x + (x*frame.canvas.width-frame.x)*r.width/frame.width)/photo.width,
          y:(r.y + (y*frame.canvas.height-frame.y)*r.height/frame.height)/photo.height, visibility };
      });
      const plane=masks![index];
      const evidence=observe(points,sampleMask(plane.getAsFloat32Array(),plane.width,plane.height,frame,photo,r),view,photo);
      if(evidence) output.push(evidence);
    });
  });
  return output;
}

function distalSupport(p: PosePoint[], mask: Uint8Array, width: number, height: number): number {
  const length = (a: number,b: number) => Math.hypot((p[a].x-p[b].x)*width,(p[a].y-p[b].y)*height);
  const trunk = (length(11,23)+length(12,24))*.5, breadth=length(11,12), diagonal=Math.hypot(width,height);
  if (trunk < 1) return 0;
  let support=1;
  for(let side=0;side<2;side++){
    const hip=p[23+side], ankle=p[27+side], leg=length(23+side,27+side);
    if(leg<1) return 0;
    const dx=(ankle.x-hip.x)*width/leg, dy=(ankle.y-hip.y)*height/leg;
    const counts=new Float64Array(256);
    let total=0;
    for(let y=0;y<128;y++) for(let x=0;x<128;x++){
      const value=mask[y*128+x];
      if(value<145) continue;
      const vx=((x+.5)/128-hip.x)*width, vy=((y+.5)/128-hip.y)*height;
      const along=vx*dx+vy*dy, across=Math.abs(vx*dy-vy*dx);
      if(along<trunk*.20 || across>Math.max(breadth*.36,trunk*.28)) continue;
      counts[Math.min(255,Math.floor(255*along/diagonal))]+=value;
      total+=value;
    }
    if(total===0) return 0;
    let sum=0, bin=0;
    for(;bin<255;bin++){sum+=counts[bin];if(sum>=total*.982)break;}
    const beyond=(bin*diagonal/255-leg)/trunk;
    support=Math.min(support,Math.max(0,Math.min(1,(.60-beyond)/.34)));
  }
  return support;
}

function fit(result: Observation, width: number, height: number, evidence: Uint8Array): { rank: number; robust: boolean } {
  const p = result.points, ids = [11,12,13,14,23,24,25,26,27,28];
  let supported = 0, valid = true, minimum = 1, sum = 0;
  for (const i of ids) {
    const q = p[i];
    if (q.x <= .005 || q.x >= .995 || q.y <= .005 || q.y >= .995 || q.visibility < (i === 13 || i === 14 ? .44 : .53)) valid = false;
    if (alpha(result.mask,q) >= 82) supported++;
    minimum = Math.min(minimum,q.visibility); sum += q.visibility;
  }
  const distance = (a: number, b: number) => Math.hypot((p[a].x-p[b].x)*width,(p[a].y-p[b].y)*height);
  const leftLower=distance(25,27), rightLower=distance(26,28), leftUpper=distance(23,25), rightUpper=distance(24,26);
  const proportion=Math.min(1,leftLower/Math.max(1,leftUpper*.70),rightLower/Math.max(1,rightUpper*.70),
    leftLower/Math.max(1,rightLower*.72),rightLower/Math.max(1,leftLower*.72));
  if(!valid || supported<8) return {rank:result.upperQuality,robust:false};
  const distal=distalSupport(p,evidence,width,height);
  const rank = 2 + .30*minimum + .12*sum/ids.length + .19*proportion + .34*distal + .05*supported/ids.length;
  return { rank, robust: valid && supported >= 8 && minimum >= .80 && proportion >= .90 && distal >= .85 };
}

type Group = Observation[];
type Review = { view: string; rank: number; ankleY: number[]; minimumAnkleVisibility: number; selected: boolean };

function overlap(a: Observation, b: Observation) {
  let intersection=0;
  for(let i=0;i<a.mask.length;i++) if(a.mask[i]>=128 && b.mask[i]>=128) intersection++;
  return {coverage:intersection/a.area, iou:intersection/Math.max(1,a.area+b.area-intersection)};
}

function samePerson(a: Observation, b: Observation, photo: Box): boolean {
  const scale=Math.min(a.span,b.span);
  if(scale < .46*Math.max(a.span,b.span) || distance(a.shoulders,b.shoulders,photo)>.65*scale) return false;
  if(a.faceVisible && b.faceVisible && distance(a.points[0],b.points[0],photo)>.62*scale) return false;
  return overlap(a,b).iou>.23;
}

function addObservations(groups: Group[], observations: Observation[], photo: Box) {
  for(const candidate of observations) {
    // Match measured anatomical positions as well as the actual foreground. A crop of a sleeve
    // can contain a confident but differently scaled skeleton completely inside the first person's mask.
    const group=groups.find(rows=>samePerson(rows[0],candidate,photo));
    if(group) group.push(candidate);
    else groups.push([candidate]);
  }
}

function bestPose(group: Group, photo: Box) {
  const intact=group.filter(o=>!o.clipped);
  const full=intact.filter(o=>o.view.whole && o.view.layout!=='upper');
  const pool=full.length?full:intact.length?intact:group;
  const evidence=new Uint8Array(maskSide*maskSide);
  for(const o of pool) for(let i=0;i<evidence.length;i++) evidence[i]=Math.max(evidence[i],o.mask[i]);
  const scores=pool.map(o=>({o,...fit(o,photo.width,photo.height,evidence)}));
  const best=scores.reduce((a,b)=>a.rank>=b.rank?a:b);
  return {best,scores};
}

function containedFragment(candidate: Observation, verified: Observation[]): boolean {
  return verified.some(main=>{
    const common=overlap(candidate,main);
    if(common.coverage<.82 || common.iou>.7 || candidate.area>main.area*.74) return false;
    const visible=[0,2,5,7,8,11,12].filter(i=>candidate.points[i].visibility>=.6 && alpha(candidate.mask,candidate.points[i])>=128);
    return visible.length>0 && visible.every(i=>alpha(main.mask,candidate.points[i])>=95);
  });
}

function regionFromFractions(left: number, top: number, right: number, bottom: number, photo: Box): Box {
  const x=Math.max(0,Math.min(photo.width-1,Math.floor(left*photo.width)));
  const y=Math.max(0,Math.min(photo.height-1,Math.floor(top*photo.height)));
  return {x,y,width:Math.max(1,Math.min(photo.width,Math.ceil(right*photo.width))-x),
    height:Math.max(1,Math.min(photo.height,Math.ceil(bottom*photo.height))-y)};
}

function confirmationView(o: Observation, photo: Box): Box {
  const b=o.bounds;
  // The first crop may cut a true arm/leg. Enlarge its own bounds in the original photo before saving any mask.
  const dx=Math.max(2/maskSide,b.width*.32),dy=Math.max(2/maskSide,b.height*.28);
  return regionFromFractions(b.x-dx,b.y-dy,b.x+b.width+dx,b.y+b.height+dy,photo);
}

function separateViews(group: Group) { return new Set(group.map(o=>o.view)).size>=2; }

type FaceAnchor = Awaited<ReturnType<typeof findPortraitAnchors>>[number];

function faceError(o:Observation, f:FaceAnchor) {
  const p=o.points[0];
  return Math.hypot((p.x-f.noseX)/f.faceWidth,(p.y-f.noseY)/f.faceHeight);
}

function sampledAlpha(mask:Uint8Array,p:Position) {
  const x=Math.max(0,Math.min(maskSide-1,p.x*maskSide-.5)),y=Math.max(0,Math.min(maskSide-1,p.y*maskSide-.5));
  const x0=Math.floor(x),y0=Math.floor(y),x1=Math.min(maskSide-1,x0+1),y1=Math.min(maskSide-1,y0+1);
  const fx=x-x0,fy=y-y0;
  return (1-fy)*((1-fx)*mask[y0*maskSide+x0]+fx*mask[y0*maskSide+x1])+
    fy*((1-fx)*mask[y1*maskSide+x0]+fx*mask[y1*maskSide+x1]);
}

// A face only specifies where to search. A new subject needs a pose and a foreground
// mask in two separately framed crops. Overlapping heads may belong to different rows.
function recoverUpperBody(detector:Landmarker, image:Input, photo:Box, face:FaceAnchor, others:FaceAnchor[], known:Observation[]) {
  const fw=face.faceWidth*photo.width, fh=face.faceHeight*photo.height;
  const sizes=[[2.4,1.4,.8],[1.55,1.65,.78],[1.9,1.1,1.1],[2,2.4,1]];
  const candidates:Observation[]=[];
  for(const [halfWidth,below,above] of sizes) {
    const region=regionFromFractions(face.noseX-halfWidth*face.faceWidth,face.noseY-above*face.faceHeight,
      face.noseX+halfWidth*face.faceWidth,face.noseY+below*face.faceHeight,photo);
    for(const layout of ['plain','upper','square'] as const) {
      const view={name:'face-guided',region,edge:540,layout,whole:false};
      for(const o of readView(detector,image,view,photo)) {
        const shoulderDrop=(o.shoulders.y-face.noseY)*photo.height/fh;
        if(o.faceVisible && faceError(o,face)<.53 && o.span>=.94*fw && o.span<=3.8*fw &&
          shoulderDrop>=.34 && shoulderDrop<=2.2 && Math.abs(o.shoulders.x-face.noseX)<1.2*face.faceWidth &&
          alpha(o.mask,{x:face.noseX,y:face.noseY})>=180 &&
          [11,12].every(i=>o.points[i].visibility>=.77)) candidates.push(o);
      }
    }
  }
  let best:{pose:Observation;second:Observation;mask:Uint8Array;rank:number}|undefined;
  for(let i=0;i<candidates.length;i++) for(let j=0;j<i;j++) {
    const a=candidates[i],b=candidates[j];
    const ar=a.view.region,br=b.view.region;
    if(ar.x===br.x && ar.y===br.y && ar.width===br.width && ar.height===br.height) continue;
    const disagreement=Math.max(...[11,12].map(k=>distance(a.points[k],b.points[k],photo)))/fw;
    if(disagreement>.52 || overlap(a,b).iou<.32) continue;
    const mask=a.mask.map((value,k)=>Math.min(value,b.mask[k]));
    const area=mask.reduce((n,v)=>n+Number(v>=153),0);
    if(area<60)continue;
    const foreign=others.reduce((sum,f)=>sum+alpha(mask,{x:f.noseX,y:f.noseY})/255,0);
    const safe=protectFaces({...a,mask},others).mask;
    const pairRank=2-disagreement-.65*(faceError(a,face)+faceError(b,face))-.75*foreign+
      .18*Math.min(2,Math.min(a.span,b.span)/fw)+.12*Math.min(1,area/(maskSide*maskSide*face.faceWidth*face.faceHeight*4));
    for(const pose of [a,b]) {
      if([11,12].some(k=>sampledAlpha(mask,pose.points[k])<76) || sampledAlpha(mask,pose.shoulders)<76)continue;
      const support=[11,12].map(k=>{
        const q=pose.points[k],other=known.reduce((max,o)=>Math.max(max,sampledAlpha(o.mask,q)),0);
        return Math.min(1,sampledAlpha(safe,q)/128)*(1-other/255);
      });
      const rank=pairRank+.80*Math.min(...support)+.15*(support[0]+support[1])-.20*faceError(pose,face);
      if(!best || rank>best.rank)best={pose,second:pose===a?b:a,mask,rank};
    }
  }
  if(!best)return;
  const {pose,second,mask}=best;
  const points=pose.points.map((p,i)=>{
    // The guiding fields end near the chest. Lower landmarks are outside the verified input.
    // Keep an elbow/wrist only when the other pose independently sees it inside the retained silhouette.
    if(i>=23 || (i>=13 && (second.points[i].visibility<.65 || distance(p,second.points[i],photo)>.45*fw ||
      alpha(mask,p)<128 || alpha(mask,second.points[i])<128))) return {...p,visibility:0};
    return p;
  });
  return {...pose,points,mask};
}

function protectFaces(pose:Observation, others:FaceAnchor[]) {
  // This exclusion can only remove a different person's face from a merged model silhouette.
  // The selected person's foreground and all the remaining coverage still come from the pose graph.
  const mask=pose.mask.slice();
  for(const f of others) {
    const cx=(f.x+f.noseX)*.5,cy=f.noseY-f.faceHeight*.10;
    const rx=.60*f.faceWidth,ry=.55*f.faceHeight;
    const left=Math.max(0,Math.floor((cx-rx)*maskSide)),right=Math.min(maskSide,Math.ceil((cx+rx)*maskSide));
    const top=Math.max(0,Math.floor((cy-ry)*maskSide)),bottom=Math.min(maskSide,Math.ceil((cy+ry)*maskSide));
    for(let y=top;y<bottom;y++) for(let x=left;x<right;x++) {
      if((((x+.5)/maskSide-cx)/rx)**2+(((y+.5)/maskSide-cy)/ry)**2<=1)mask[y*maskSide+x]=0;
    }
  }
  return {...pose,mask};
}

async function refineCrowd(detector:Landmarker,image:Input,photo:Box,selected:Observation[],fine:boolean) {
  const nearby=selected.some((a,i)=>selected.slice(i+1).some(b=>
    distance(a.points[0],b.points[0],photo)<.31*Math.min(photo.width,photo.height)));
  if(selected.length>=maxPeople || (!fine && !nearby) || photo.width>photo.height*3 || !hasImageDetail(image))return [];
  const faces=await findPortraitAnchors(image);
  if(faces.length>maxPeople)throw new Error('照片中识别到超过 '+maxPeople+' 个人物。请选用人数较少的裁切副本继续美型。');
  const owners=new Map<Observation,FaceAnchor>(),matched=new Set<FaceAnchor>();
  const matches=selected.flatMap(o=>faces.map(f=>({o,f,error:faceError(o,f)}))).sort((a,b)=>a.error-b.error);
  for(const {o,f,error} of matches)if(error<.78 && !owners.has(o) && !matched.has(f) && o.faceVisible) {
    owners.set(o,f);matched.add(f);
  }
  // Some whole-photo views place the same person's shoulders at two heights. Only merge
  // when a second real face is absent and the two poses share the measured foreground.
  for(let i=selected.length-1;i>=0;i--) {
    const o=selected[i];
    if(owners.has(o) || !o.faceVisible)continue;
    const repeated=[...owners].some(([other,f])=>faceError(o,f)<.88 && overlap(o,other).iou>.48 &&
      distance(o.shoulders,other.shoulders,photo)<1.15*f.faceWidth*photo.width);
    if(repeated)selected.splice(i,1);
  }
  const missing=faces.filter(f=>!matched.has(f));
  if(!missing.length)return [];
  const unlocated:number[]=[];
  for(const f of missing) {
    const pose=recoverUpperBody(detector,image,photo,f,faces.filter(other=>other!==f),selected);
    if(pose){selected.push(pose);owners.set(pose,f);}
    else unlocated.push(faces.indexOf(f)+1);
  }
  for(let i=selected.length-1;i>=0;i--) {
    const o=selected[i],own=owners.get(o);
    if(!own)continue;
    const safe=protectFaces(o,faces.filter(f=>f!==own));
    if(safe.mask.reduce((n,v)=>n+Number(v>=153),0)>=52)selected[i]=safe;
    else {selected.splice(i,1);unlocated.push(faces.indexOf(own)+1);}
  }
  return unlocated.sort((a,b)=>a-b);
}

function hasImageDetail(image: Input): boolean {
  const canvas=document.createElement('canvas');
  canvas.width=32;canvas.height=32;
  const ctx=canvas.getContext('2d');
  if(!ctx) return true;
  ctx.drawImage(image,0,0,32,32);
  const pixels=ctx.getImageData(0,0,32,32).data;
  let difference=0;
  for(let i=4;i<pixels.length;i+=4) difference+=Math.abs(pixels[i]-pixels[i-4])+
    Math.abs(pixels[i+1]-pixels[i-3])+Math.abs(pixels[i+2]-pixels[i-2]);
  return difference>32*32*3;
}

export async function detectBody(image: Input, review?: (rows: Review[]) => void, fine=false): Promise<BodyResult> {
  const detector=await getDetector();
  const width=image instanceof HTMLImageElement?image.naturalWidth:image.width;
  const height=image instanceof HTMLImageElement?image.naturalHeight:image.height;
  if(!width || !height) throw new Error('人体识别图像尺寸无效。');
  const photo={x:0,y:0,width,height}, groups: Group[]=[];
  const seen=new Set<string>();
  const run=(name: string, region: Box, edge: number, layout: Layout)=>{
    const key=[region.x,region.y,region.width,region.height,edge,layout].join(',');
    if(seen.has(key)) return;
    seen.add(key);
    const whole=region.width===width && region.height===height;
    addObservations(groups,readView(detector,image,{name,region,edge,layout,whole},photo),photo);
  };
  run('full900',photo,900,'plain');
  run('square768',photo,768,'square');
  // The unpadded graph sometimes shifts a visible ankle onto the shin. Additional full-photo fits
  // use the original single-person ranking separately for each person's foreground.
  const extra: [number,Layout][]=[[640,'square'],[768,'plain'],[640,'plain'],[960,'plain']];
  for(const [edge,layout] of extra) {
    if(!groups.some(group=>group.some(o=>[25,26,27,28].some(i=>o.points[i].visibility>.5 && alpha(o.mask,o.points[i])>=82))
      && !bestPose(group,photo).best.robust)) break;
    run(layout+edge,photo,edge,layout);
  }
  run('upper768',photo,768,'upper');

  const regional=(name:string,l:number,t:number,r:number,b:number)=>run(name,regionFromFractions(l,t,r,b,photo),800,'plain');
  regional('left',0,0,.57,1); regional('right',.43,0,1,1);
  regional('top',0,0,1,.59); regional('bottom',0,.41,1,1);
  // A broad group shot can shrink people below the whole-image detector's input resolution.
  // Overlapping strips let their heads and torsos be sampled at comparable scale.
  const columns=Math.min(2*maxPeople,Math.ceil(width/(height*.54)));
  if(columns>=3 || groups.length>=2) {
    const count=Math.max(3,columns), step=1/count;
    for(let i=0;i<count;i++) regional('column'+i,i*step-step*.24,0,(i+1)*step+step*.24,1);
    // A person straddling adjacent narrow windows can be too incomplete in both. Stagger the
    // second pass in panoramas once the first pass actually finds a crowd.
    if(width>height*3 && groups.length>=4)
      for(let i=0;i<count-1;i++) regional('stagger'+i,(i+.24)*step,0,(i+1.76)*step,1);
  }
  // With several rows of smaller people, even a full-height strip can be too distant. Search
  // overlapping local fields when the coarse passes are empty or their subjects occupy only part of the height.
  const smallSubjects=groups.filter(group=>group[0].bounds.height<.56 && group[0].bounds.width<.31);
  if(width/height<3 && (!groups.length || smallSubjects.length>=2) && hasImageDetail(image)) {
    const estimateRows=(samples: Group[], fallback: number)=>{
      const heights=samples.map(g=>g[0].bounds.height).sort((a,b)=>a-b);
      return heights.length?Math.min(5,Math.max(2,Math.round(1/heights[Math.floor(heights.length/2)]))):fallback;
    };
    const searchGrid=(rows:number)=>{
      const cols=Math.min(8,Math.max(2,Math.round(width/(height/rows*.64))));
      for(let y=0;y<rows;y++) for(let x=0;x<cols;x++)
        regional('tile'+rows+'-'+x+'-'+y,(x-.21)/cols,(y-.18)/rows,(x+1.21)/cols,(y+1.18)/rows);
    };
    const rows=estimateRows(smallSubjects,3);
    searchGrid(rows);
    const measured=estimateRows(groups.filter(g=>g.some(o=>!o.clipped)),rows===3?4:3);
    if(groups.length<=2 && measured!==rows) searchGrid(measured);
  }

  const verified=groups.filter(group=>separateViews(group) && group.some(o=>o.view.whole));
  const reliable=verified.map(group=>bestPose(group,photo).best.o);
  // A single detection in a fragment is a search hint. Re-detect around that person's silhouette
  // with different context; global count alone and one-off ROI landmark guesses cannot add a person.
  for(const group of groups.slice()) {
    const o=bestPose(group,photo).best.o;
    if((separateViews(group) && !o.clipped) || containedFragment(o,reliable)) continue;
    const region=confirmationView(o,photo);
    run('confirm',region,704,'plain');
    if(!separateViews(group) || group.every(row=>row.clipped)) run('confirm-square',region,704,'square');
    if(!separateViews(group) || group.every(row=>row.clipped)) run('confirm-upper',region,832,'upper');
  }

  const qualified=groups.filter(group=>separateViews(group) && group.some(o=>!o.clipped))
    .map(group=>({group,...bestPose(group,photo)}));
  qualified.sort((a,b)=>Number(b.group.some(o=>o.view.whole))-Number(a.group.some(o=>o.view.whole))
    || b.best.o.area-a.best.o.area);
  const selected: Observation[]=[];
  for(const row of qualified) {
    if(containedFragment(row.best.o,selected)) continue;
    selected.push(row.best.o);
    if(review) review(row.scores.map(({o,rank})=>({view:o.view.name,rank,
      ankleY:[o.points[27].y,o.points[28].y],minimumAnkleVisibility:Math.min(o.points[27].visibility,o.points[28].visibility),
      selected:o===row.best.o})));
  }
  const unlocatedFaces=await refineCrowd(detector,image,photo,selected,fine);
  if(selected.length>maxPeople) throw new Error('照片中识别到超过 '+maxPeople+' 个人物。请选用人数较少的裁切副本继续美型。');
  selected.sort((a,b)=>a.shoulders.x-b.shoulders.x || a.shoulders.y-b.shoulders.y);
  const subjects=selected.map(o=>({points:o.points,mask:btoa(String.fromCharCode(...o.mask))}));
  return {people:subjects.length,subjects,unlocatedFaces,...(subjects.length===1?subjects[0]:{points:[],mask:''})};
}

export async function decodeBodyInput(url: string): Promise<HTMLImageElement> {
  if (!/^data:image\/(png|jpeg|webp);base64,/i.test(url)) throw new Error('原片预览格式异常。');
  const image = new Image();
  image.src = url;
  await image.decode();
  return image;
}
