// For each clothing or background patch, store its nine possible local displacements.
// The head separates the two background strips; neighbors never jump across it.
function neighbors(offset:number,columns:number,split:boolean){
  return Array.from({length:columns*6},(_,n)=>{
    const row=Math.floor(n/columns),col=n%columns;
    return Array.from({length:9},(_,k)=>{
      const y=row+Math.floor(k/3)-1,x=col+k%3-1;
      if(y<0||y>=6||x<0||x>=columns||split&&((x<4)!==(col<4)))return -1;
      return 4*(offset+y*columns+x);
    });
  });
}
const clothing=neighbors(0,9,false),background=neighbors(54,8,true);
const light=(a:Uint8Array,i:number)=>(54*a[i]+183*a[i+1]+19*a[i+2])/256;
function sameColor(a:Uint8Array,i:number,b:Uint8Array,j:number){
  const dr=a[i]-b[j],dg=a[i+1]-b[j+1],db=a[i+2]-b[j+2];
  return Math.abs(dr-dg)<18&&Math.abs(db-dg)<18&&Math.abs(dr*54+dg*183+db*19)<36*256;
}
function visiblePatches(a:Uint8Array,b:Uint8Array,grid:number[][],minimum:number){
  const available:number[][]=[];
  let lowA=255,highA=0,lowB=255,highB=0,direct=0;
  for(const neighbors of grid){
    const i=neighbors[4];
    if(!a[i+3]||!b[i+3])continue;
    const left=light(a,i),right=light(b,i);
    lowA=Math.min(lowA,left);highA=Math.max(highA,left);lowB=Math.min(lowB,right);highB=Math.max(highB,right);
    available.push(neighbors);if(sameColor(a,i,b,i))direct++;
  }
  if(available.length<minimum||Math.min(highA-lowA,highB-lowB)<32)return null;
  return {available,direct:direct/available.length};
}
function localSupport(a:Uint8Array,b:Uint8Array,grid:number[][],fraction:number,coherent:number){
  const offsets=new Uint8Array(9);
  let matched=0;
  for(const neighbors of grid){
    const i=neighbors[4];
    let found=false;
    for(let k=0;k<neighbors.length;k++){
      const j=neighbors[k];
      if(j>=0&&b[j+3]&&sameColor(a,i,b,j)){found=true;offsets[k]++;}
    }
    if(found)matched++;
  }
  return matched>=fraction*grid.length&&Math.max(...offsets)>=coherent*grid.length;
}
export function matchingComposition(a:Uint8Array,b:Uint8Array){
  const garment=visiblePatches(a,b,clothing,18);
  if(!garment)return false;
  const scene=visiblePatches(a,b,background,20);
  if(!scene)return false;
  if(garment.direct>=.78&&scene.direct>=.52)return true;
  // A hand or a turned shoulder can move a single patch. Require reciprocal agreement
  // in both regions, plus one coherent offset explaining at least half the surrounding scene.
  return localSupport(a,b,garment.available,.75,0)&&localSupport(b,a,garment.available,.75,0)&&
    localSupport(a,b,scene.available,.85,.5)&&localSupport(b,a,scene.available,.85,.5);
}
