import {useState} from 'react';
import {frameChanges, frameKeys, frameValues} from './framing';
import type {PhotoDocument, Values} from './model';
import type {WriteDraft} from './editing-drafts';

type Draft = {base: Values; values: Values; rawFrame: number};

function compatible(stored: Draft, doc: PhotoDocument) {
  return stored.rawFrame === (doc.parameters.raw_frame ?? 0) &&
    frameKeys.every(key => stored.base[key] === (doc.parameters[key] ?? 0) &&
      (stored.values[key] === stored.base[key] || !doc.locks.includes(key)));
}

export function useFrameDraft(doc: PhotoDocument|null,write:WriteDraft) {
  const [drafts, setDrafts] = useState<Map<string, Draft>>(() => new Map());
  const [open, setOpen] = useState(false);
  const stored = doc && drafts.get(doc.photoId);
  const draft = doc && stored && compatible(stored, doc) ? stored : undefined;
  const values = draft?.values ?? frameValues(doc?.parameters);

  function discard(id: string) {
    write(id,'crop',null);
    setDrafts(previous => {
      if (!previous.has(id)) return previous;
      const next = new Map(previous);
      next.delete(id);
      return next;
    });
  }
  function change(values: Values) {
    if (!doc) return;
    const base = draft?.base ?? frameValues(doc.parameters);
    if (!Object.keys(frameChanges(values, base)).length) { discard(doc.photoId); return; }
    const value={base,values,rawFrame:doc.parameters.raw_frame??0};
    write(doc.photoId,'crop',value);
    setDrafts(previous => new Map(previous).set(doc.photoId,value));
  }
  function reconcile(next: PhotoDocument|null) {
    if (!next) return false;
    const pending = drafts.get(next.photoId);
    if(pending&&!compatible(pending,next))write(next.photoId,'crop',null);
    setDrafts(previous => {
      const stored = previous.get(next.photoId);
      if (!stored || compatible(stored, next)) return previous;
      const remaining = new Map(previous);
      remaining.delete(next.photoId);
      return remaining;
    });
    return !!pending && !compatible(pending, next) && Object.keys(frameChanges(pending.values, next.parameters)).length > 0;
  }
  function restore(next:PhotoDocument,data:unknown){
    const saved=data as Draft;
    if(saved?.base&&saved.values&&compatible(saved,next))setDrafts(existing=>new Map(existing).set(next.photoId,saved));
    else discard(next.photoId);
  }
  return {open, setOpen, values, hasDraft: !!draft, change, discard, reconcile,restore};
}
