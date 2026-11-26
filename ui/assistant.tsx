import {localize} from './locale';
import React, {useEffect, useRef, useState} from 'react';
import {Control, PhotoDocument, Plan, Values, displayValue, groupControls, groupName} from './model';
import {Icon} from './icons';
import {IconButton} from './widgets';

export type ChatItem = {id: string; who: 'user' | 'assistant'; text: string};
export type StagedPlan = Plan & {groups:string[][]; selected:Values};
type ReviewProps = {
  controls:Control[]; doc:PhotoDocument|null; busy:boolean; previewReady:boolean;
  toggleGroup:(keys:string[])=>void; accept:()=>void; cancel:()=>void;
};

export function ProposalReview({plan,controls,doc,busy,previewReady,toggleGroup,accept,cancel,body=false}:ReviewProps & {plan:StagedPlan;body?:boolean}) {
  const count=plan.groups.filter(keys=>Object.hasOwn(plan.selected,keys[0])).length;
  return <section className={'proposal'+(body?' body-confirm':'')}>
    <div className="proposal-head"><Icon name="eye" size={15}/><b>{body?'美型预览':plan.origin==='reference'?'参考图仿色预览':'提案预览'}</b>
      <span aria-live="polite">已选 {count} / {plan.groups.length} 项</span></div>
    <strong>{plan.label}</strong>{plan.message&&<p>{plan.message}</p>}
    <div className="proposal-values">{plan.groups.map(keys=>{
      const items=keys.map(key=>({key,control:controls.find(c=>c.key===key)}));
      const checked=Object.hasOwn(plan.selected,keys[0]);
      return <label key={keys[0]} className={'proposal-choice'+(checked?'':' excluded')}>
        <input type="checkbox" disabled={busy} checked={checked} onChange={()=>toggleGroup(keys)}
          aria-label={items.map(({key,control})=>control?.label||key).join('、')}/>
        <span className="proposal-rows">{keys.length>1&&<small className="proposal-linked">一起应用</small>}
          {items.map(({key,control})=><span className="proposal-value" key={key}><span>{control?.label||key}</span>
            <small>{displayValue(doc?.parameters[key]??control?.initial??0,control)}</small><Icon name="right" size={10}/>
            <b>{displayValue(plan.values[key],control)}</b></span>)}</span>
      </label>;
    })}</div>
    <div className="button-line"><button className="primary small" disabled={busy||!count||!previewReady} onClick={accept}>
      <Icon name="check" size={13}/>{body?'应用美型':'应用提案'}</button><button className="secondary small" disabled={busy} onClick={cancel}>取消</button></div>
  </section>;
}

type Props = {
  chat: ChatItem[]; pending: StagedPlan | null; generating: boolean;
  provider: string; configure?: () => void; send: (text: string, scope: string) => void;
  presetName:string;setPresetName:(name:string)=>void;duplicatePreset:boolean;confirmPreset:()=>void;
} & ReviewProps;
export function AiPanel({controls, doc, chat, pending, generating, busy, provider, configure, send, accept, cancel,previewReady,toggleGroup,presetName,setPresetName,duplicatePreset,confirmPreset}: Props) {
  const [draft, setDraft] = useState('');
  const [scope, setScope] = useState('all');
  const bottom = useRef<HTMLDivElement>(null);
  const textInput = useRef<HTMLTextAreaElement>(null);
  const local = !provider || /本地指令|本地规则|offline|local rules/i.test(provider);
  const canSend = !!doc && !!draft.trim() && !busy && !generating && !pending;
  const submit = () => { if (!canSend) return; send(draft.trim(), scope); setDraft(''); };
  useEffect(()=>{ bottom.current?.scrollIntoView({block:'nearest'}); },[chat, pending?.requestId, generating]);
  useEffect(()=>{ setDraft(''); setScope('all'); },[doc?.photoId]);
  return <div className="ai-panel">
    <div className="ai-top"><div className="ai-symbol"><Icon name="sparkle" size={19}/></div><div><b>修图助手</b><span><i className="live-dot"/>{provider || '本地指令'}</span></div>
      {configure&&<IconButton icon="settings" label="配置 AI 服务" onClick={configure} disabled={busy}/>}</div>
    <div className="chat-scroll">
      {!chat.length && <div className="ai-intro">
        <h3>想怎样调整这张照片？</h3><p>{local?'本地指令可调整光影、色彩和预设。':'根据当前编辑参数生成调整提案。'}确认后会写入编辑历史。</p>
        <div className="prompts">{['压低高光','稍微提亮曝光','改成日系空气感'].map(p=><button key={p} disabled={!doc || generating || busy} onClick={()=>{setDraft(localize(p)); textInput.current?.focus();}}>{p}<Icon name="right" size={11}/></button>)}</div>
      </div>}
      {chat.map(item=><div key={item.id} className={'chat-item '+item.who}><span>{item.who==='user'?'你':'助手'}</span><p translate={item.who==='user'?'no':undefined}>{item.text}</p></div>)}
      {generating&&<p className="ai-loading" role="status">正在生成参数提案…</p>}
      {pending?.action==='savePreset'&&<section className="proposal preset-proposal">
        <div className="proposal-head"><Icon name="save" size={15}/><b>保存预设确认</b></div>
        {pending.message&&<p>{pending.message}</p>}
        <form onSubmit={e=>{e.preventDefault();if(!busy&&presetName.trim()&&!duplicatePreset)confirmPreset();}}>
          <label className="field-label" htmlFor="ai-preset-name">预设名称</label>
          <input className="field" id="ai-preset-name" aria-label="AI 预设名称" value={presetName} maxLength={80} disabled={busy} autoComplete="off" onChange={e=>setPresetName(e.target.value)}/>
          {duplicatePreset&&<p className="field-error">已有同名预设，请换一个名称。</p>}
          <div className="button-line"><button className="primary small" type="submit" disabled={busy||!presetName.trim()||duplicatePreset}><Icon name="save" size={13}/>确认保存预设</button>
            <button className="secondary small" type="button" disabled={busy} onClick={cancel}>取消保存</button></div>
        </form>
      </section>}
      {pending && pending.action!=='savePreset' && <ProposalReview plan={pending} controls={controls} doc={doc} busy={busy}
        previewReady={previewReady} toggleGroup={toggleGroup} accept={accept} cancel={cancel}/>}
      <div ref={bottom}/>
    </div>
    <div className="ai-compose">
      <label className="scope-row"><span>调整范围</span><select aria-label="AI 调整范围" value={scope} onChange={e=>setScope(e.target.value)} disabled={generating || !doc}>
        <option value="all">全部参数</option>{groupControls(controls).map(([g])=><option key={g} value={g}>{groupName(g)}</option>)}</select></label>
      <div className="compose-box"><textarea ref={textInput} aria-label="修图指令" placeholder={doc?'描述你想要的调整…':'先导入一张照片'} maxLength={3000} disabled={!doc || busy} value={draft} onChange={e=>setDraft(e.target.value)}
        onKeyDown={e=>{if(e.key==='Enter'&&(e.ctrlKey||e.metaKey)){e.preventDefault(); submit();}}}/>
        <div><small>{local?'本地规则':'服务 · '+provider}</small><button className="send-button" aria-label="发送修图指令" title="发送修图指令（Ctrl+Enter）" disabled={!canSend} onClick={submit}><Icon name="send" size={14}/></button></div>
      </div>
    </div>
  </div>;
}
