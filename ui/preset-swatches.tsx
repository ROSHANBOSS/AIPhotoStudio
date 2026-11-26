import {localize} from './locale';
import React, {useEffect, useLayoutEffect, useMemo, useRef, useState} from 'react';
import {Control, Photo, PhotoDocument, Preview, Recipe, invoke, recipeValues, sameRecipe} from './model';
import {Icon} from './icons';

type Props = {
  recipes:Recipe[]; chosen:Recipe[]; pick:(recipe:Recipe)=>void; personal:boolean; busy:boolean;
  photo:Photo|null; doc:PhotoDocument|null; controls:Control[]; ready:boolean;
};

// Only one small render may run across an unmount, photo switch or category change.
let rendering:Promise<Preview>|undefined;

export function PresetSwatches({recipes,chosen,pick,personal,busy,photo,doc,controls,ready}:Props) {
  const list=useRef<HTMLDivElement>(null);
  const [visible,setVisible]=useState<number[]>([]);
  const [held,setHeld]=useState(false);
  const [,refresh]=useState(0);
  const catalog=useMemo(()=>JSON.stringify(recipes.map(p=>[p.name,p.values])),[recipes]);
  const scope=useMemo(()=>JSON.stringify([photo?.id,photo?.path,photo?.selectedRawFrame,doc?.revision,catalog]),
    [photo?.id,photo?.path,photo?.selectedRawFrame,doc?.revision,catalog]);
  const images=useRef({scope,urls:new Map<number,string>()});
  if(images.current.scope!==scope)images.current={scope,urls:new Map()};
  useLayoutEffect(()=>{
    const group=list.current;
    if(!group)return;
    const items=new Set<number>();
    const observer=new IntersectionObserver(entries=>{
      for(const entry of entries){
        const i=Number((entry.target as HTMLElement).dataset.swatch);
        if(entry.isIntersecting)items.add(i);else items.delete(i);
      }
      setVisible(Array.from(items).sort((a,b)=>a-b));
    },{root:group.closest('.side-scroll')});
    setVisible([]);
    group.querySelectorAll('[data-swatch]').forEach(item=>observer.observe(item));
    return ()=>observer.disconnect();
  },[catalog]);
  useEffect(()=>{
    const down=(e:PointerEvent)=>{if(e.target instanceof Element&&e.target.closest('input[type="range"]'))setHeld(true);};
    const up=()=>setHeld(false);
    document.addEventListener('pointerdown',down,true);
    window.addEventListener('pointerup',up,true);window.addEventListener('pointercancel',up,true);window.addEventListener('blur',up);
    return ()=>{document.removeEventListener('pointerdown',down,true);window.removeEventListener('pointerup',up,true);
      window.removeEventListener('pointercancel',up,true);window.removeEventListener('blur',up);};
  },[]);
  useEffect(()=>{
    if(!ready||held||!doc||!photo||doc.photoId!==photo.id||!visible.length)return;
    const cache=images.current;
    let stopped=false, idle=0, working=false;
    async function run(){
      if(stopped||document.hidden||working)return;
      working=true;
      while(rendering&&!stopped){try{await rendering;}catch{}}
      if(stopped)return;
      const index=visible.find(i=>!cache.urls.has(i)&&recipes[i]);
      if(index===undefined){working=false;return;}
      const request=invoke<Preview>('preview',{id:doc!.photoId,revision:doc!.revision,maxEdge:112,profile:'presetSwatch',
        values:recipeValues([recipes[index]],controls,doc!,100,false)});
      rendering=request;
      try{
        const frame=await request;
        if(!frame?.image?.startsWith('data:image/'))throw Error('预设小样不可用');
        if(images.current===cache){
          if(cache.urls.size>=48)for(const previous of cache.urls.keys())if(!visible.includes(previous)){cache.urls.delete(previous);break;}
          cache.urls.set(index,frame.image);
          refresh(n=>n+1);
        }
      }catch{if(!stopped)cache.urls.set(index,'');}
      finally{if(rendering===request)rendering=undefined;working=false;}
      if(!stopped)idle=window.requestIdleCallback(()=>void run(),{timeout:300});
    }
    const start=()=>{if(!document.hidden&&!stopped)idle=window.requestIdleCallback(()=>void run(),{timeout:300});};
    const timer=window.setTimeout(start,160);
    const visibility=()=>{if(!document.hidden)start();};
    document.addEventListener('visibilitychange',visibility);
    return ()=>{stopped=true;window.clearTimeout(timer);window.cancelIdleCallback(idle);
      document.removeEventListener('visibilitychange',visibility);};
  },[scope,ready,held,visible]);
  return <div className="presets-list" ref={list}>{recipes.map((preset,i)=>{
    const picked=chosen.some(p=>sameRecipe(p,preset)),url=images.current.urls.get(i);
    return <button key={preset.name+'-'+i} data-swatch={i} title={(personal?preset.name:localize(preset.name))+' · '+localize('单独预览 100%')}
      aria-pressed={picked} className={'preset-entry '+(picked?'selected':'')} disabled={!doc||busy} onClick={()=>pick(preset)}>
      {photo?<span className="preset-swatch">{url?<img src={url} alt="" draggable={false}/>:<Icon name="photo" size={15}/>}</span>:
        <Icon name={personal?'save':'stack'} size={15}/>}<span translate={personal?'no':undefined}>{preset.name}</span>{picked&&<span className="preset-dot"/>}
    </button>;
  })}{!recipes.length&&<p className="side-note">{personal?'保存当前调整后可在这里复用。':'暂无预设'}</p>}</div>;
}
