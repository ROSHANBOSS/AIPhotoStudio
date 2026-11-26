import {useRef,useState} from 'react';
import {invoke,messageOf} from './model';

export type WriteDraft=(id:string,kind:string,data:unknown|null)=>void;
export function draftLabel(kind:string){
  if(kind==='privacy')return '隐私遮挡';
  if(kind==='crop')return '裁切';
  return kind==='brush-structure'?'修复笔迹':'手绘笔迹'+(kind==='brush-0'?'':' · 蒙版 '+kind.slice(6));
}
export function useEditingDrafts(report:(message:string,error:boolean)=>void){
  const pending=useRef(new Set<string>()),loaded=useRef(new Set<string>()),enabled=useRef(false);
  const [recovered,setRecovered]=useState<string[]>([]);
  const [,refresh]=useState(0);
  function begin(index?:string[]){
    enabled.current=Array.isArray(index);
    pending.current=new Set(index??[]);loaded.current.clear();setRecovered(index??[]);
  }
  const acknowledge=(key:string)=>setRecovered(list=>list.filter(item=>item!==key));
  const write:WriteDraft=(id,kind,data)=>{
    if(!enabled.current)return;
    const key=id+'/'+kind;
    if(data===null){pending.current.delete(key);acknowledge(key);}else pending.current.add(key);
    loaded.current.add(key);refresh(n=>n+1);
    // Post each completed canvas gesture in order. The native handler commits synchronously.
    void invoke('draftWrite',{id,kind,content:data===null?'':JSON.stringify(data)}).catch(e=>report('草稿写入图库失败：'+messageOf(e),true));
  };
  async function load(id:string|null,restore:(kind:string,data:unknown)=>void){
    if(!id)return;
    for(const key of pending.current){
      if(!key.startsWith(id+'/')||loaded.current.has(key))continue;
      const kind=key.slice(id.length+1),raw=await invoke<string|null>('draftRead',{id,kind});
      if(raw)restore(kind,JSON.parse(raw));
      else {pending.current.delete(key);acknowledge(key);}
      loaded.current.add(key);
    }
  }
  function privacy(ids?:string[]){
    if(ids)return ids.find(id=>pending.current.has(id+'/privacy'));
    for(const key of pending.current)if(key.endsWith('/privacy'))return key.slice(0,-8);
  }
  return {begin,load,write,privacy,recovered,acknowledge,has:(id:string)=>Array.from(pending.current).some(key=>key.startsWith(id+'/'))};
}
