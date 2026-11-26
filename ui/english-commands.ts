// The offline planner accepts the same bounded parameter operations in either interface language.
const prompts:Record<string,string>={
  'lower the highlights':'压低高光','brighten exposure slightly':'稍微提亮曝光','apply the airy japanese look':'改成日系空气感',
  'make it warmer':'稍微调暖色温','make it cooler':'稍微调冷色温','make it brighter':'稍微提亮曝光','make it darker':'稍微压低曝光',
  'lift the shadows':'提亮阴影','reduce contrast':'降低对比度','make it black and white':'改成黑白风格',
  'halve the last adjustment':'把刚才这组调色减半','halve the last adjustments':'把刚才这组调色减半','halve the last edit':'把刚才这组调色减半',
};
const parameters:Record<string,string>={exposure:'曝光',temperature:'色温',warmth:'色温',tint:'色调',highlights:'高光',shadows:'阴影',
  contrast:'对比度',whites:'白色色阶',blacks:'黑色色阶',saturation:'饱和度',vibrance:'自然饱和度',clarity:'清晰度',sharpness:'锐化',dehaze:'去雾'};
export function offlineInstruction(input:string):string{
  const chunks=input.trim().replace(/[.!]$/,'').toLowerCase().split(/\s*(?:,|;|\band\b)\s*/);
  const result:string[]=[];
  for(const chunk of chunks){
    if(prompts[chunk]){result.push(prompts[chunk]);continue;}
    const m=/^(set|increase|raise|brighten|boost|decrease|lower|reduce)\s+(?:the\s+)?([a-z]+)\s+(?:(to|by)\s+)?([+-]?(?:\d+(?:\.\d+)?|\.\d+))(?:\s*(?:ev|stops?|%|degrees?))?$/.exec(chunk);
    if(!m||!parameters[m[2]])return input;
    if(m[1]==='set'&&m[3]==='by')return input;
    const operation=m[1]==='set'||m[3]==='to'?'设为':['decrease','lower','reduce'].includes(m[1])?'降低':'增加';
    result.push(parameters[m[2]]+operation+m[4]);
  }
  return result.join('，');
}
