import {Photo,invoke} from './model';
import {detectPortraits} from './portrait-detector';
import {detectMask} from './mask-detector';

export type MatchRole='skin'|'background'|'hair';
export type FaceEffect={x:number;y:number;rx:number;ry:number;cuts:number[][]};
type Ellipse={x:number;y:number;rx:number;ry:number};
export type HairField={left:number;top:number;width:number;height:number;bytes:string;stamp:string};
export type MatchArea=Ellipse&{maskId?:string;effect?:FaceEffect;opposite?:Ellipse;hair?:HairField};
type Candidate=MatchArea&{side?:string;color:number[];score:number};
export type RegionChoices=Partial<Record<MatchRole,MatchArea>>;
export type AreaSelection={revision:number;digest:string;regions:RegionChoices;person?:number;side?:string;
  backgroundColor?:number[];suggested?:boolean;skipped?:MatchRole[];omitted?:boolean};
export type Suggestion={regions:RegionChoices;person?:number;side?:string;backgroundColor?:number[]};
export type Guide={image:string;digest:string;savedFaces:boolean;savedBodies:boolean;stored:{id:string;name:string;area:MatchArea}[]};
export type MatchPerson={number:number;box:{x:number;y:number;w:number;h:number};cheeks:Candidate[];uncertain:boolean;
  effect?:FaceEffect;overlay?:string;hair?:MatchArea};
export type Suggestions={people:MatchPerson[];backgrounds:Candidate[];savedFaces:boolean;savedBodies:number};
export type PreparedSuggestions=Suggestions&{recheck:(guide:Guide,person:MatchPerson)=>Promise<MatchPerson|null>};

// A fresh guide reads the file identity before this small session cache can be reused.
// The cache retains geometry and segmentation evidence, never photographs or identity matches.
const recent=new Map<string,Promise<PreparedSuggestions>>();
type Evidence={id:string;revision:number;pickedDigest:string;width:number;height:number;meshes:string[];facesChecked:boolean;purpose?:string};

function withLocalRecheck(found:Suggestions,evidence:Evidence,hair:boolean):PreparedSuggestions{
  const prior=new Map<number,Promise<MatchPerson|null>>();
  const prepared:PreparedSuggestions={...found,recheck};
  function recheck(guide:Guide,person:MatchPerson):Promise<MatchPerson|null>{
    if(guide.digest!==evidence.pickedDigest||person.uncertain||(!hair&&person.cheeks.length))return Promise.resolve(null);
    const cached=prior.get(person.number);if(cached)return cached;
    const attempt=(async()=>{
      const b=person.box,w=evidence.width,h=evidence.height;
      const faceSize=Math.max(b.w*w,b.h*h);
      // Each crop gives the local segmenter a different amount of head and shoulder context.
      // Both must support the same skin pixels in normalized coordinates.
      const masks=await Promise.all((hair?[2.2,3.2]:[2.1,3.4]).map(scale=>{
        const sw=Math.min(1,faceSize*scale/w),sh=Math.min(1,faceSize*scale/h);
        const x=Math.max(0,Math.min(1-sw,b.x+b.w/2-sw/2));
        const y=Math.max(0,Math.min(1-sh,b.y+b.h*(hair?.30:scale<3?.62:.85)-sh/2));
        return detectMask(guide.image,hair?'hair':'skin',{x,y,w:sw,h:sh}).catch(()=>null);
      }));
      if(!masks[0]||!masks[1])return null;
      const a=atob(masks[0]),b2=atob(masks[1]),bytes=new Uint8Array(a.length);
      for(let i=0;i<a.length;i++)bytes[i]=Math.min(a.charCodeAt(i),b2.charCodeAt(i));
      let packed='';
      for(let i=0;i<bytes.length;i+=4096)packed+=String.fromCharCode(...bytes.subarray(i,i+4096));
      const result=await invoke<Suggestions>('setMatchSuggest',{...evidence,focusNumber:person.number,[hair?'hair':'skin']:btoa(packed)});
      const recovered=result.people.find(p=>p.number===person.number&&!p.uncertain&&(hair?p.hair:p.cheeks.length))??null;
      if(recovered)prepared.people=prepared.people.map(p=>p.number===recovered.number?recovered:p);
      return recovered;
    })().catch(err=>{prior.delete(person.number);throw err;});
    prior.set(person.number,attempt);return attempt;
  }
  return prepared;
}
export function prepareSuggestions(photo:Photo,guide:Guide,hair=false){
  const key=[photo.id,photo.revision??0,guide.digest,hair?'hair':'scene'].join('/');
  const prior=recent.get(key);
  if(prior)return prior;
  const request=(async()=>{
    const img=new Image();img.src=guide.image;await img.decode();
    const [faceResult,skin,background]=await Promise.all([
      guide.savedFaces?Promise.resolve(null):detectPortraits(img).catch(()=>null),
      hair?Promise.resolve(null):detectMask(guide.image,'skin').catch(()=>null),
      hair?Promise.resolve(null):detectMask(guide.image,'backdrop').catch(()=>null)
    ]);
    if(hair&&!guide.savedFaces&&!faceResult)throw new Error('本机人脸定位未完成，请重新打开此图。');
    const evidence={id:photo.id,revision:photo.revision??0,pickedDigest:guide.digest,width:img.naturalWidth,
      height:img.naturalHeight,meshes:faceResult?.meshes??[],facesChecked:!!faceResult,...(hair?{purpose:'hair'}:{})};
    const found=await invoke<Suggestions>('setMatchSuggest',{...evidence,skin,background});
    return withLocalRecheck(found,evidence,hair);
  })().catch(error=>{recent.delete(key);throw error;});
  if(recent.size>=24)recent.delete(recent.keys().next().value!);
  recent.set(key,request);return request;
}

const ellipse=(a:Candidate):MatchArea=>({x:a.x,y:a.y,rx:a.rx,ry:a.ry});
function matchingBackground(options:Candidate[],reference?:AreaSelection){
  if(!reference)return options[0];
  const region=reference.regions.background;
  if(!region)return undefined;
  let best:Candidate|undefined,bestScore=Infinity;
  for(const candidate of options){
    const distance=Math.hypot(candidate.x-region.x,candidate.y-region.y);
    const color=reference.backgroundColor?Math.sqrt(candidate.color.reduce((v,c,i)=>v+(c-reference.backgroundColor![i])**2,0)/3):0;
    if(distance>.23||color>.28)continue;
    const score=distance*2.6+color*1.8+candidate.score*.18;
    if(score<bestScore){bestScore=score;best=candidate;}
  }
  return best;
}
export function propose(options:Suggestions,person:MatchPerson|undefined,reference?:AreaSelection):Suggestion{
  const cheek=person?.cheeks.find(c=>c.side===reference?.side)??person?.cheeks[0];
  const opposite=cheek&&(!reference||reference.side===cheek.side&&reference.regions.skin?.opposite)
    ?person?.cheeks.find(c=>c.side!==cheek.side):undefined;
  const bg=matchingBackground(options.backgrounds,reference);
  const regions:RegionChoices={};
  if(cheek)regions.skin={...ellipse(cheek),...(reference&&person?.effect?{effect:person.effect}:{}),
    ...(opposite?{opposite:ellipse(opposite)}:{})};
  if(bg)regions.background=ellipse(bg);
  return {person:person?.number,side:cheek?.side,regions,backgroundColor:bg?.color};
}
