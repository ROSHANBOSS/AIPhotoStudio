type Plane={width:number;height:number;getAsFloat32Array():Float32Array};
type Segmenter={segment(image:HTMLCanvasElement,consume:(r:{confidenceMasks?:Plane[]})=>void):void};
type Vision={
  FilesetResolver:{forVisionTasks(path:string):Promise<unknown>};
  ImageSegmenter:{createFromOptions(fileset:unknown,options:object):Promise<Segmenter>};
};
export type Category='hair'|'clothes'|'skin'|'backdrop';
export const categoryName:Record<Category,string>={hair:'头发',clothes:'衣服',skin:'人物肤色',backdrop:'背景'};
export type MaskBox={x:number;y:number;w:number;h:number};
const side=256;
let detector:Promise<Segmenter>|undefined;
function getDetector(){
  if(!detector)detector=(async()=>{
    const base=new URL('./BodyVision/',document.baseURI);
    const vision:Vision=await import(new URL('vision_bundle.mjs',base).href);
    const files=await vision.FilesetResolver.forVisionTasks(new URL('wasm',base).href);
    return vision.ImageSegmenter.createFromOptions(files,{baseOptions:{modelAssetPath:new URL('selfie_multiclass_256x256.tflite',base).href,delegate:'CPU'},
      runningMode:'IMAGE',outputConfidenceMasks:true,outputCategoryMask:false});
  })().catch(error=>{detector=undefined;throw error;});
  return detector;
}

export async function detectMask(url:string,category:Category,box?:MaskBox|null):Promise<string>{
  const image=new Image();image.src=url;
  await image.decode();
  const model=await getDetector(),canvas=document.createElement('canvas');
  // Four-pixel row alignment also supports the runtime's unpadded float mask reader.
  const left=box?Math.max(0,box.x-box.w*.16-.01):0,top=box?Math.max(0,box.y-box.h*.14-.01):0;
  const right=box?Math.min(1,box.x+box.w*1.16+.01):1,bottom=box?Math.min(1,box.y+box.h*1.14+.01):1;
  const sourceW=(right-left)*image.naturalWidth,sourceH=(bottom-top)*image.naturalHeight;
  canvas.width=Math.max(4,Math.round(sourceW/4)*4);canvas.height=Math.max(1,Math.round(sourceH));
  const context=canvas.getContext('2d');
  if(!context)throw new Error('无法读取当前照片的像素。');
  context.drawImage(image,left*image.naturalWidth,top*image.naturalHeight,sourceW,sourceH,0,0,canvas.width,canvas.height);
  const output=new Uint8Array(side*side);
  model.segment(canvas,result=>{
    const planes=result.confidenceMasks;
    if(planes?.length!==6)throw new Error('模型没有返回完整的六类人像选区。');
    const plane=planes[{hair:1,clothes:4,skin:2,backdrop:0}[category]],probability=plane.getAsFloat32Array();
    const face=category==='skin'?planes[3].getAsFloat32Array():null;
    if(probability.length!==plane.width*plane.height)throw new Error('识别结果与照片尺寸不匹配。');
    if(face&&face.length!==probability.length)throw new Error('脸部与身体肤色区域尺寸不一致。');
    const sample=(i:number)=>probability[i]+(face?face[i]:0);
    for(let y=0;y<side;y++)for(let x=0;x<side;x++){
      const uPhoto=(x+.5)/side,vPhoto=(y+.5)/side;
      if(uPhoto<left||uPhoto>right||vPhoto<top||vPhoto>bottom)continue;
      const u=Math.min(plane.width-1,Math.max(0,(uPhoto-left)/(right-left)*plane.width-.5));
      const v=Math.min(plane.height-1,Math.max(0,(vPhoto-top)/(bottom-top)*plane.height-.5));
      const a=Math.floor(u),b=Math.floor(v),a2=Math.min(plane.width-1,a+1),b2=Math.min(plane.height-1,b+1);
      const fx=u-a,fy=v-b;
      const p=(sample(b*plane.width+a)*(1-fx)+sample(b*plane.width+a2)*fx)*(1-fy)+
        (sample(b2*plane.width+a)*(1-fx)+sample(b2*plane.width+a2)*fx)*fy;
      if(!Number.isFinite(p))throw new Error('本次识别没有返回有效的概率。');
      output[y*side+x]=Math.round(255*Math.max(0,Math.min(1,(p-.25)/.6)));
    }
  });
  if(output.reduce((n,b)=>n+Number(b>=128),0)<24||output.reduce((n,b)=>n+Number(b<32),0)<24)
    throw new Error('未找到可靠的'+categoryName[category]+'区域，可用画笔手动选择。');
  let bytes='';
  for(let i=0;i<output.length;i+=4096)bytes+=String.fromCharCode(...output.subarray(i,i+4096));
  return btoa(bytes);
}
