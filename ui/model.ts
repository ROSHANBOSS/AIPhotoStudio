export type Values = Record<string, number>;
export type Photo = {
  id: string; name: string; path: string; width: number; height: number;
  libraryDate?:string; caption?:string;
  favorite: boolean; format?: string; revision?: number; cameraModel?: string;
  [field:string]:unknown;
};
export type Control = {
  key: string; label: string; group: string; min: number; max: number; step: number; initial: number;
};
export type Recipe = {name: string; values: Values};
export type Project = {id: string; name: string; photoIds: string[]};
export type BodyPerson = {index:number; box:{x:number;y:number;w:number;h:number}; available:string[]};
export type PersonLink = {index:number;bodyIndex:number|null;faceIndex:number|null};
export type Edit = {id: string; index?: number; label: string; when: string | number; source?: string; operation?:string; changed?: string[]};
export type PhotoDocument = {
  photoId: string; revision: number; parameters: Values; locks: string[]; history: Edit[];
  cursor: number; canUndo: boolean; canRedo: boolean;
  undoableParams?:string[]; redoableParams?:string[];
};
export type Snapshot = {
  libraryTools?:boolean; peoplePreference?:string|null;
  renderMode?:'cpu'|'auto';
  portraitLandmarks?:boolean; portraitPeople?:BodyPerson[]; portraitGeometry?:string; people?:PersonLink[];
  channel: string; version: string; dataRoot: string; catalog: Photo[]; selectedId: string | null;
  document: PhotoDocument | null; controls: Control[]; presets: Recipe[]; userPresets: Recipe[]; bodyPeople?: BodyPerson[];
  capabilities?: Record<string, unknown>; provider?: {mode?: string; name?: string; configured?: boolean}; projects?: Project[];
};
export type Preview = {image: string; histogram: number[]; width: number; height: number};
export type NormalizedPoint = {x:number; y:number};
export type BrushStroke = {kind:'paint'|'erase'|'maskPaint'|'maskErase'|'protect'|'warp'|'heal'|'sampleRepair'|'redEye';points:NormalizedPoint[];radius:number;strength:number;edge?:string};
export type FaceRect = {x:number;y:number;width:number;height:number};
export type Plan = {
  id:string; revision:number; values:Values; label:string; message:string; requestId:string;
  action?:'savePreset'|'adjust'; suggestedName?:string; scope?:string; origin?:'ai'|'reference'; groups?:string[][];
};
export type ExportMeasure = {width:number;height:number;quality:number;bytes:number;sample?:{width:number;height:number;before:string;after:string}};
export type ExportResult = {count: number; paths: string[]; errors: unknown[];files?:ExportMeasure[];cancelled?:boolean;total?:number};
export type Args = Record<string, unknown>;
type Reply = {requestId: string; ok: boolean; result?: unknown; error?: unknown;catalogSession?:string;catalogOrder?:number};
type Host = {
  postMessage(message: {requestId: string; method: string; args: Args; hasControlDefinitions?: boolean;catalogBase?:string;catalogSync?:boolean}): void;
  addEventListener(type: 'message', callback: (event: {data: Reply | string}) => void): void;
};
type Injected = Host | ((method: string, args: Args) => Promise<unknown>) |
  {invoke(method: string, args: Args): Promise<unknown>};
declare global {
  interface Window {
    chrome?: {webview?: Host};
    __AIPhotoStudioTransport?: Injected;
  }
}

const listeners = new WeakSet<Host>();
const controlDefinitions = new WeakMap<Host,Control[]>();
type WireState=Partial<Snapshot>&{catalogToken?:string;catalogBase?:string;catalogPhoto?:Photo;reuseControlDefinitions?:boolean;
  maskSlot?:number;targetPerson?:number;importErrors?:unknown[]};
type CatalogCache={session:string;order:number;state:WireState;positions?:Map<string,number>};
const catalogs=new WeakMap<Host,CatalogCache>();
type Pending={method:string;session?:string;base?:CatalogCache;resolve(value:unknown):void;reject(reason:Error):void;timer?:number};
const waiting = new Map<string, Pending>();
let sequence = 0;
export function requestId(): string {
  return globalThis.crypto?.randomUUID?.() || 'ui-' + Date.now().toString(36) + '-' + (++sequence).toString(36);
}
export function messageOf(error: unknown): string {
  if (typeof error === 'string') return error;
  if (error && typeof error === 'object' && 'message' in error) return String(error.message);
  if(error && typeof error==='object' && 'error' in error && typeof error.error==='string'){
    const path='path' in error && typeof error.path==='string'?error.path:'';
    return path?baseName(path)+'：'+error.error:error.error;
  }
  return '操作未完成，请重试。';
}

function receiveState(host:Host,entry:Pending,message:Reply,wire:WireState){
  const latest=catalogs.get(host),session=message.catalogSession,order=message.catalogOrder;
  const ordered=typeof session==='string'&&typeof order==='number'&&Number.isSafeInteger(order);
  // Re-read the committed state if the page no longer has the certified base. A
  // confirmed write is never replayed by this recovery, even after a delayed reply.
  const resync=()=>{
    if(entry.method==='init'){entry.reject(new Error('图库的完整响应无效，请重新打开工作区。'));return;}
    void invoke<Snapshot>('init').then(entry.resolve,entry.reject);
  };
  if(ordered&&latest){
    if(session!==latest.session&&entry.session!==latest.session){resync();return;}
    if(session===latest.session&&order<=latest.order){
      // The newer snapshot already includes this operation. A mask slot or person
      // number only has meaning for the original photo and revision.
      const extras:Partial<WireState>={};
      if(wire.document?.photoId===latest.state.document?.photoId&&wire.document?.revision===latest.state.document?.revision){
        if(wire.maskSlot!==undefined)extras.maskSlot=wire.maskSlot;
        if(wire.targetPerson!==undefined)extras.targetPerson=wire.targetPerson;
      }
      if(wire.importErrors)extras.importErrors=wire.importErrors;
      entry.resolve({...latest.state,...extras});return;
    }
  }
  const {catalogBase,catalogPhoto,reuseControlDefinitions,...state}=wire;
  let positions:Map<string,number>|undefined;
  if(catalogBase!==undefined||catalogPhoto!==undefined){
    const base=entry.base,index=catalogPhoto&&base?.positions?.get(catalogPhoto.id);
    if(!ordered||!base?.state.catalog||!catalogPhoto||index===undefined||base.session!==session||
       base.state.catalogToken!==catalogBase||latest?.state.catalogToken!==catalogBase||!state.catalogToken||
       catalogPhoto.id!==state.selectedId||catalogPhoto.id!==state.document?.photoId||catalogPhoto.revision!==state.document?.revision){resync();return;}
    state.catalog=base.state.catalog.slice();
    state.catalog[index]=catalogPhoto;
    state.projects=base.state.projects;state.userPresets=base.state.userPresets;
    positions=base.positions;
  }else if(ordered&&state.catalogToken&&state.catalog){
    positions=new Map(state.catalog.map((p,i)=>[p.id,i]));
  }
  if(Array.isArray(state.controls))controlDefinitions.set(host,state.controls);
  else if(reuseControlDefinitions){
    state.controls=controlDefinitions.get(host);
    if(!state.controls){resync();return;}
  }
  if(ordered&&state.catalog){
    const {maskSlot,targetPerson,importErrors,...snapshot}=state;
    catalogs.set(host,{session,order,state:snapshot,positions});
  }
  entry.resolve(state);
}

export async function invoke<T>(method: string, args: Args = {}): Promise<T> {
  const injected = window.__AIPhotoStudioTransport;
  if (typeof injected === 'function') return await injected(method, args) as T;
  if (injected && 'invoke' in injected) return await injected.invoke(method, args) as T;
  const host = (injected as Host | undefined) || window.chrome?.webview;
  if (!host) throw new Error('请从 AIPhotoStudio 桌面程序打开工作台。');
  if (!listeners.has(host)) {
    host.addEventListener('message', ({data}) => {
      let message: Reply;
      try { message = typeof data === 'string' ? JSON.parse(data) : data; } catch { return; }
      const entry = message && waiting.get(message.requestId);
      if (!entry) return;
      waiting.delete(message.requestId);
      window.clearTimeout(entry.timer);
      if (!message.ok) {entry.reject(new Error(messageOf(message.error)));return;}
      const state=message.result as WireState|null;
      if(state&&(Array.isArray(state.catalog)||state.catalogBase!==undefined||state.catalogPhoto!==undefined))
        receiveState(host,entry,message,state);
      else entry.resolve(message.result);
    });
    listeners.add(host);
  }
  const id = requestId();
  return new Promise<T>((resolve, reject) => {
    const cached=catalogs.get(host);
    const entry:Pending={method,session:cached?.session,base:method==='commit'?cached:undefined,resolve:(v:unknown)=>resolve(v as T),reject};
    if (!['importFiles', 'importFolder', 'export', 'batchExport', 'providerSettings', 'importLut', 'exportRecipe', 'importRecipe', 'matchColor',
      'relinkScan','relinkPick','relinkApply','relinkPrepare','libraryBackup','libraryRestore','setMatchApply','deliveryStart','deliveryResume','deliveryPickLogo'].includes(method)) {
      entry.timer = window.setTimeout(() => {
        waiting.delete(id);
        reject(new Error('本机服务响应超时，请重试。'));
      }, 150000);
    }
    waiting.set(id, entry);
    try { host.postMessage({requestId:id,method,args,hasControlDefinitions:controlDefinitions.has(host),catalogSync:true,
      ...(entry.base?.state.catalogToken?{catalogBase:entry.base.state.catalogToken}:{})}); }
    catch (error) { waiting.delete(id); window.clearTimeout(entry.timer); reject(error); }
  });
}

export const clamp = (n: number, min: number, max: number) => Math.max(min, Math.min(max, n));
export function quantize(value: number, control: Control): number {
  const step = control.step || 1;
  return Number(clamp(control.min + Math.round((value - control.min) / step) * step, control.min, control.max).toFixed(6));
}
export function displayValue(value: number, control?: Control): string {
  if (!Number.isFinite(value)) return '—';
  const step = String(control?.step || 1);
  const places = control ? (step.includes('e-') ? Number(step.split('e-')[1]) : (step.split('.')[1] || '').length) : 2;
  const number = Math.abs(value) < 1e-8 ? 0 : value;
  const actualPlaces=Number.isInteger(number)?0:(String(Number(number.toFixed(4))).split('.')[1]||'').length;
  return (number > 0 && (control?.min ?? 0) < 0 ? '+' : '') + number.toFixed(Math.min(Math.max(places,actualPlaces), 4));
}
export function folderOf(path = ''): string { return path.replace(/[\\/][^\\/]*$/, ''); }
export function baseName(path: string): string { return path.split(/[\\/]/).filter(Boolean).pop() || path; }
export function formatOf(photo: Photo): string { return (photo.format || photo.name.split('.').pop() || '').toUpperCase(); }
export function photoMetadata(photo:Photo):[string,string][] {
  const fields:[string,string[]][]=[
    ['相机品牌',['displayManufacturer','cameraManufacturer','cameraMake','make']],['相机型号',['cameraModel','model']],['镜头',['lensModel','lens']],
    ['拍摄时间',['dateTaken','capturedAt','captureTime','dateTimeOriginal']],['感光度',['iso','isoSpeed']],
    ['曝光时间',['exposureTime','shutterSpeed']],['光圈',['aperture','fNumber']],['焦距',['focalLength']],
    ['色彩配置',['iccProfileName','colorProfile','colorSpace']],['位深',['bitDepth','bitsPerSample']],
    ['RAW 编码',['compressionMode']],['RAW 兼容性',['cameraValidation']],
  ];
  const result:[string,string][]=[];
  for(const [label,names] of fields){
    const value=names.map(n=>photo[n]).find(v=>(typeof v==='number'&&Number.isFinite(v)&&v>0)||(typeof v==='string'&&v.trim()));
    if(value!==undefined)result.push([label,String(value)]);
  }
  return result;
}
export function timeOf(when: string | number): string {
  let value: string | number = when;
  if (typeof when === 'number' && when < 100000000000) value = when * 1000;
  const date = new Date(value);
  return Number.isFinite(date.getTime()) ? date.toLocaleTimeString('zh-CN', {hour: '2-digit', minute: '2-digit', hour12: false}) : '';
}
const groupNames: Record<string, string> = {
  basic: '基础调整', light: '光影', tone: '光影', color: '色彩', colour: '色彩', detail: '细节',
  curve: 'RGB 曲线', curves: 'RGB 曲线', hsl: 'HSL 八色', grading: '色彩分级',
  calibration: '色彩校准', portrait: '人像', mask: '局部蒙版', local: '局部调整', body: '人体美型',
};
export function groupName(group: string): string { return groupNames[group.toLowerCase()] || group; }
const documentGroups=new Set(['局部资源','人像区域','人脸区域','人体资源','人脸关键点','构图']);
export function publicControls(controls: Control[]) {return controls.filter(c=>c.group!=='LUT 资源'&&!isDocumentControl(c));}
export function isDocumentControl(c:Control) {return c.key==='raw_frame'||documentGroups.has(c.group)||/^segment_ref\d+$/.test(c.key);}
export function sameRecipe(a:Recipe,b:Recipe) {
  return a.name===b.name && Object.keys(a.values).length===Object.keys(b.values).length && Object.entries(a.values).every(([key,v])=>b.values[key]===v);
}
const lightKeys=new Set(['exposure','contrast','highlights','shadows','whites','blacks']);
const colorKeys=new Set(['temperature','tint','vibrance','saturation']);
export function manualGroup(c:Control) {
  if(c.group!=='基础')return c.group;
  return lightKeys.has(c.key)?'光影':colorKeys.has(c.key)?'色彩':'细节';
}
export function groupControls(controls: Control[], groupOf:(c:Control)=>string=c=>c.group) {
  const groups = new Map<string, Control[]>();
  for (const control of controls) {
    const group = groupOf(control) || '基础调整';
    const existing = groups.get(group);
    if (existing) existing.push(control);
    else groups.set(group, [control]);
  }
  return [...groups.entries()];
}

function gradeHue(
  key:string, recipes:Recipe[], current:Values, amount:number, additive:boolean,
) {
  const saturation=key.replace(/_hue$/,'_sat');
  const existing=current[key]??0, existingSat=current[saturation]??0;
  const tones=recipes.filter(r=>Number.isFinite(r.values[key])&&r.values[saturation]>0)
    .map(r=>({hue:r.values[key],sat:r.values[saturation]}));
  if(!tones.length)return existing;
  if(additive&&existingSat>0)tones.unshift({hue:existing,sat:existingSat});
  let x=0,y=0;
  for(const tone of tones){
    const radians=tone.hue*Math.PI/180;
    x+=tone.sat*Math.cos(radians);y+=tone.sat*Math.sin(radians);
  }
  if(Math.hypot(x,y)<.00001)return existing;
  const target=(Math.atan2(y,x)*180/Math.PI+360)%360;
  if(existingSat===0)return amount>0?target:existing;
  const arc=((target-existing+540)%360)-180;
  return (existing+amount*arc+360)%360;
}

export function recipeValues(
  recipes: Recipe[], controls: Control[], doc: PhotoDocument, strength: number, additive: boolean,
): Values {
  const result: Values = {};
  const fingerprints = controls.filter(c=>c.group==='LUT 资源');
  const lastWithLut = fingerprints.length ? [...recipes].reverse().find(r=>fingerprints.every(c=>Number.isInteger(r.values[c.key])) && (!additive || fingerprints.some(c=>r.values[c.key]!==0))) : undefined;
  const lutProtected = fingerprints.some(c=>doc.locks.includes(c.key));
  const changeLut=lastWithLut && strength>0 && !lutProtected && fingerprints.some(c=>doc.parameters[c.key]!==lastWithLut.values[c.key]);
  const faceSelected=doc.parameters.portrait_face_w>0&&doc.parameters.portrait_face_h>0;
  const hasLocalRegion=controls.some(c=>c.group==='局部资源'&&(doc.parameters[c.key]??c.initial)!==c.initial);
  for (const c of controls) {
    if(c.key.startsWith('portrait_p'))continue;
    if (doc.locks.includes(c.key)||isDocumentControl(c)||c.group==='独立蒙版'||c.key==='local_warp'||c.key==='local_strength'||c.key.startsWith('segment_')||c.key.startsWith('body_')) continue;
    if(c.key.startsWith('portrait_')&&!faceSelected)continue;
    if(c.key.startsWith('local_')&&!hasLocalRegion)continue;
    if(c.group==='LUT 资源') {
      if(changeLut && lastWithLut)result[c.key]=lastWithLut.values[c.key];
      continue;
    }
    if(c.key==='lut_strength' && lastWithLut && lutProtected && fingerprints.some(f=>doc.parameters[f.key]!==lastWithLut.values[f.key])) continue;
    const recipeSet = c.key==='lut_strength' && lastWithLut ? [lastWithLut] : recipes;
    const values = recipeSet.map(recipe => recipe.values[c.key]).filter(Number.isFinite);
    if (!values.length) continue;
    const original = doc.parameters[c.key] ?? c.initial;
    const delta = values.reduce((n, value) => n + value - c.initial, 0);
    const target = (additive && c.key!=='lut_strength' ? original : c.initial) + delta;
    const mixed=/^grade_.+_hue$/.test(c.key)
      ?gradeHue(c.key,recipes,doc.parameters,strength/100,additive)
      :original+(target-original)*strength/100;
    const value = Number(clamp(mixed, c.min, c.max).toFixed(6));
    if (value !== original) result[c.key] = value;
  }
  return result;
}
