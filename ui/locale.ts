import React from 'react';
import * as common from './i18n-common';
import * as library from './i18n-library';
import * as editing from './i18n-editing';
import * as native from './i18n-native';

export type Language='zh-CN'|'en';
export type LanguagePreference={language:Language|null;askOnStartup:boolean};
let current:Language='zh-CN';
const dictionaries=[native,library,editing,common];
const messages:Record<string,string>=Object.assign(Object.create(null),...dictionaries.map(d=>d.strings));
const rules=dictionaries.flatMap(d=>d.patterns);
const cache=new Map<string,string>();
const chinese=/[\u3400-\u9fff]/;
export function language(){return current;}
export function applyLanguage(next:Language){
  current=next;document.documentElement.lang=next;
  document.title=next==='en'?'AIPhotoStudio · Photo editor':'AIPhotoStudio';
}
function translated(source:string):string{
  if(Object.hasOwn(messages,source))return messages[source];
  const trimmed=source.trim();
  if(trimmed!==source&&trimmed)return source.replace(trimmed,translated(trimmed));
  const proposal=/^(已应用|已取消)「(.+)」。$/.exec(source);
  if(proposal)return (proposal[1]==='已应用'?'Applied':'Cancelled')+' “'+translated(proposal[2])+'”.';
  const adjustment=/^调整(.+)$/.exec(source);
  if(adjustment&&Object.hasOwn(messages,adjustment[1]))return 'Adjust '+messages[adjustment[1]];
  for(const [pattern,convert] of rules){
    const match=pattern.exec(source);
    if(match)return convert(...match.slice(1));
  }
  for(const sep of [' · ','（','、']){
    const index=source.indexOf(sep);
    if(index>0&&source.length>index+sep.length){
      const pieces=source.split(sep);
      if(sep==='（'&&source.endsWith('）'))return translated(pieces[0])+' ('+translated(pieces.slice(1).join(sep).slice(0,-1))+')';
      if(sep!=='（')return pieces.map(translated).join(sep==='、'?', ':sep);
    }
  }
  return source;
}
export function localize(text:string):string{
  if(current!=='en'||!chinese.test(text))return text;
  if(cache.has(text))return cache.get(text)!;
  const result=translated(text);
  if(cache.size>=4096)cache.clear();
  cache.set(text,result);return result;
}
function localChildren(input:any[]):any[]{
  const result:any[]=[];
  for(let i=0;i<input.length;i++){
    const value=input[i];
    if(Array.isArray(value)){result.push(localChildren(value));continue;}
    if(typeof value!=='string'&&typeof value!=='number'){result.push(value);continue;}
    let end=i+1;
    while(end<input.length&&(typeof input[end]==='string'||typeof input[end]==='number'))end++;
    if(end>i+1){
      const phrase=input.slice(i,end).join(''),english=localize(phrase);
      if(english!==phrase){result.push(english);i=end-1;continue;}
    }
    result.push(typeof value==='string'?localize(value):value);
  }
  return result;
}
// Translate only rendered text. Component props, event payloads and native control keys stay in their source language.
export function studioElement(type:any,props:any,...children:any[]):React.ReactElement{
  if(current!=='en'||(typeof type!=='string'&&type!==React.Fragment)||props?.translate==='no')
    return React.createElement(type,props,...children);
  let shown=props;
  for(const key of ['aria-label','aria-valuetext','aria-description','title','placeholder','alt']){
    if(typeof props?.[key]!=='string')continue;
    const value=localize(props[key]);
    if(value!==props[key]){if(shown===props)shown={...props};shown[key]=value;}
  }
  if(type==='option'&&props?.value===undefined&&children.length===1&&typeof children[0]==='string'){
    shown={...shown,value:children[0]};
  }
  return React.createElement(type,shown,...localChildren(children));
}
