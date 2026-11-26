import {Photo,folderOf,invoke} from './model';
import {matchingComposition} from './set-review-composition';

export type Mark='none'|'keep'|'reject';
export type Verdict='auto'|'flag'|'clear';
export type EyeManual=Verdict|'unknown';
export type EyeState='unknown'|'suspect'|'clear';
export type ReviewFace={key:string;centers:[number,number,number,number];eyes:EyeState[];closerEye:number;minEyePixels:number;colorCover?:boolean;manual?:EyeManual};
export type Quality={blur:'unknown'|'face'|'image'|'clear';softFaces:number[];over:boolean;under:boolean};
export type Evidence={version?:number;source:string;frame:number;signature:string;composition?:string;blur:Quality['blur'];
  faces?:ReviewFace[]|null;quality?:Quality[]|null;brightFraction?:number;darkFraction?:number;medianLuma?:number;
  eyes:'unknown'|'suspect'|'clear';detection:string;faceCount:number;softFaces:number[];narrowEyes:number[];uncertainEyes?:number[]};
export type GridExposure={frame:number;over:boolean;under:boolean};
export type ReviewRow={id:string;mark:Mark;blur:Verdict;eyes:Verdict;evidence?:Evidence|null;exposure?:GridExposure|null};
export type ReviewRows=Record<string,ReviewRow>;
export const emptyRow=(id:string):ReviewRow=>({id,mark:'none',blur:'auto',eyes:'auto'});
export const markLabels:Record<Mark,string>={none:'未标记',keep:'保留',reject:'淘汰'};
export const keyMarks:Readonly<Record<string,Mark>>={p:'keep',x:'reject',u:'none'};
export const markEvent='aiphoto-review-mark';
export const exposureEvent='aiphoto-review-exposure';

// Slots refer only to one native fitting session. A 10,000-photo selection occupies 1,250 bytes.
export function packSelection(slots:number[]){
  let last=-1;for(const slot of slots)last=Math.max(last,slot);
  const bytes=new Uint8Array(Math.ceil((last+1)/8));
  for(const slot of slots)bytes[slot>>3]|=1<<(slot%8);
  return btoa(Array.from(bytes,n=>String.fromCharCode(n)).join(''));
}

export async function writeReview(id:string,field:'mark'|'blur'|'eyes',value:Mark|Verdict){
  await invoke('setReviewField',{id,field,value});
  if(field==='mark')window.dispatchEvent(new CustomEvent(markEvent,{detail:{id,mark:value}}));
}
export function evidenceFor(photo:Photo,row?:ReviewRow){
  return row?.evidence?.frame===Number(photo.selectedRawFrame||0)?row.evidence:null;
}
export function qualityFor(e:Evidence|null|undefined,level:number):Quality|undefined{
  return e?.quality?.[level]||(e&&level===1?{blur:e.blur,softFaces:e.softFaces,over:false,under:false}:undefined);
}
export function faceState(face:ReviewFace,level:number):EyeState{
  return !face.manual||face.manual==='auto'?face.eyes[level]:face.manual==='flag'?'suspect':face.manual;
}
export function eyesFor(e:Evidence|null|undefined,level:number):{state:EyeState;suspect:number[];unknown:number[]}{
  if(!e?.faces){
    return e&&level===1?{state:e.eyes,suspect:e.narrowEyes||[],unknown:e.uncertainEyes||[]}:{state:'unknown',suspect:[],unknown:[]};
  }
  const suspect:number[]=[],unknown:number[]=[];
  e.faces.forEach((face,i)=>{
    const state=faceState(face,level);
    if(state==='suspect')suspect.push(i+1);
    if(state==='unknown')unknown.push(i+1);
  });
  return {state:suspect.length?'suspect':!e.faces.length||unknown.length?'unknown':'clear',suspect,unknown};
}
export function reviewMatches(scope:string,photo:Photo,row:ReviewRow|undefined,level:number){
  const e=evidenceFor(photo,row);
  const quality=qualityFor(e,level);
  if(scope==='over'||scope==='under')return !!quality?.[scope];
  const eyes=eyesFor(e,level);
  const flaggedEyes=row?.eyes==='flag'||row?.eyes!=='clear'&&eyes.state==='suspect';
  if(scope==='eyes')return flaggedEyes;
  if(scope==='unknownEyes')return !!e&&row?.eyes!=='flag'&&row?.eyes!=='clear'&&eyes.unknown.length>0;
  return !!(flaggedEyes||row?.blur==='flag'||row?.blur!=='clear'&&(quality?.blur==='face'||quality?.blur==='image')||quality?.over||quality?.under);
}
function captureTime(p:Photo){
  const date=String(p.dateTaken||'').trim().replace(/^(\d{4}):(\d\d):(\d\d)/,'$1-$2-$3');
  const time=date?Date.parse(date):NaN;
  return Number.isFinite(time)?time:undefined;
}

export function nearbyCaptures(anchor:Photo,photos:Photo[],groups:Photo[][]){
  const stamp=captureTime(anchor);
  if(stamp===undefined || !anchor.cameraModel)return [];
  const grouped=new Set(groups.find(g=>g.some(p=>p.id===anchor.id))?.map(p=>p.id)||[anchor.id]);
  const folder=folderOf(anchor.path).toLowerCase();
  return photos.flatMap(photo=>{
    if(grouped.has(photo.id)||photo.cameraModel!==anchor.cameraModel||folderOf(photo.path).toLowerCase()!==folder)return [];
    const time=captureTime(photo);
    if(time===undefined||Math.abs(stamp-time)>60000)return [];
    return [{photo,seconds:Math.round(Math.abs(time-stamp)/1000)}];
  }).sort((a,b)=>a.seconds-b.seconds).slice(0,12);
}
type Frame={photo:Photo;index:number;e:Evidence;time?:number;rgb:Uint8Array;luma:number[];mean:number;sd:number;composition?:Uint8Array};
function frame(photo:Photo,index:number,e:Evidence):Frame{
  const rgb=Uint8Array.from(atob(e.signature),c=>c.charCodeAt(0));
  const luma:number[]=[];
  for(let i=0;i<rgb.length;i+=3)luma.push((54*rgb[i]+183*rgb[i+1]+19*rgb[i+2])/256);
  const mean=luma.reduce((n,x)=>n+x,0)/luma.length;
  const sd=Math.sqrt(luma.reduce((n,x)=>n+(x-mean)**2,0)/luma.length);
  const composition=e.composition?.length===544?Uint8Array.from(atob(e.composition),c=>c.charCodeAt(0)):undefined;
  return {photo,index,e,time:captureTime(photo),rgb,luma,mean,sd,composition};
}
function reframed(a:Frame,b:Frame){
  if(!a.composition||!b.composition)return false;
  if(a.time!==undefined&&b.time!==undefined&&Math.abs(a.time-b.time)>12000)return false;
  if((a.time===undefined||!a.photo.cameraModel)&&Math.abs(a.index-b.index)>2)return false;
  return matchingComposition(a.composition,b.composition);
}
function similarity(a:Frame,b:Frame):'fixed'|'reframed'|undefined{
  if(a.e.source===b.e.source&&a.e.frame===b.e.frame)return 'fixed';
  if(Math.abs(Math.log((a.photo.width/a.photo.height)/(b.photo.width/b.photo.height)))>.07||
     Math.min(a.sd,b.sd)<7||Math.abs(a.mean-b.mean)>55)return;
  let correlation=0,color=0;
  for(let i=0;i<a.luma.length;i++){
    correlation+=(a.luma[i]-a.mean)*(b.luma[i]-b.mean);
    const j=i*3,rg=a.rgb[j]-a.rgb[j+1]-b.rgb[j]+b.rgb[j+1],bg=a.rgb[j+2]-a.rgb[j+1]-b.rgb[j+2]+b.rgb[j+1];
    color+=rg*rg+bg*bg;
  }
  const closeCapture=a.time!==undefined&&b.time!==undefined&&Math.abs(a.time-b.time)<=12000;
  if(correlation/(a.luma.length*a.sd*b.sd)>(closeCapture ? .86 : .94)&&Math.sqrt(color/(2*a.luma.length))<(closeCapture?28:19))return 'fixed';
  if(reframed(a,b))return 'reframed';
}

export function burstGroups(photos:Photo[],rows:ReviewRows):Photo[][]{
  const buckets=new Map<string,Frame[]>();
  photos.forEach((p,index)=>{
    const e=evidenceFor(p,rows[p.id]);
    if(!e || e.signature.length!==400)return;
    const f=frame(p,index,e);
    const bucket=JSON.stringify([folderOf(p.path).toLowerCase(),p.cameraModel||'',f.time!==undefined]);
    if(!buckets.has(bucket))buckets.set(bucket,[]);
    buckets.get(bucket)!.push(f);
  });
  const groups:Frame[][]=[];
  for(const bucket of buckets.values()){
    bucket.sort((a,b)=>(a.time??a.index)-(b.time??b.index)||a.index-b.index);
    const recent:Frame[][]=[];
    for(const f of bucket){
      let group:Frame[]|undefined,ambiguous=false;
      for(const candidate of recent){
        const anchor=candidate[0];
        if(f.time!==undefined?f.time-anchor.time!>30000:f.index-candidate[candidate.length-1].index>4)continue;
        const match=similarity(f,anchor);
        if(match==='fixed'){group=candidate;ambiguous=false;break;}
        if(match==='reframed'){if(group)ambiguous=true;else group=candidate;}
      }
      if(ambiguous)group=undefined;
      if(group)group.push(f);
      else{
        const next=[f]; recent.unshift(next); groups.push(next);
        if(recent.length>8)recent.pop();
      }
    }
  }
  return groups.filter(g=>g.length>1).sort((a,b)=>a[0].index-b[0].index).map(g=>g.map(f=>f.photo));
}
