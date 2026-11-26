import {offlineInstruction} from './english-commands';
import {applyLanguage,Language,LanguagePreference} from './locale';
import {LanguageDialog} from './language-dialog';
import {ParameterAction,ActionMode} from './parameter-actions';
import {PhotoNote,RemovedPhotos} from './personal-library';
import React, {useCallback, useEffect, useMemo, useRef, useState} from 'react';
import {createRoot} from 'react-dom/client';
import {Args, Control, ExportResult, Photo, Plan, Recipe, Snapshot, Values, displayValue, formatOf, invoke, messageOf, photoMetadata, publicControls, quantize, recipeValues, requestId, sameRecipe} from './model';
import {Icon, Mark} from './icons';
import {Histogram, IconButton, Modal, ParameterPanel} from './widgets';
import {Filmstrip, LibraryFilter, LibraryView, Sidebar, filterPhotos} from './library';
import {SetReviewDialog} from './set-review';
import {useGridExposure} from './set-review-grid';
import {keyMarks,markLabels,writeReview} from './set-review-data';
import {RelinkDialog} from './relink';
import {CaptureIntake} from './capture';
import {allCriteria,matchesCriteria} from './library-filters';
import {LibraryStorage} from './library-storage';
import {CopyCompare} from './copy-compare';
import {PhotoCanvas, ViewCommand, usePreview} from './preview';
import {useWhiteBalance,WhiteBalancePanel,WhitePoints} from './white-balance';
import {ColorBandPanel,useColorBand} from './color-band';
import {AiPanel, ChatItem, ProposalReview, StagedPlan} from './assistant';
import {BatchDialog, CopyRecipeDialog, HistoryPanel, PresetDialog, ProjectDialog, ShortcutDialog} from './dialogs';
import {LocalOverlay, LocalPanel, isSpotRepair, localSection, useLocalEdits} from './local';
import {SegmentOverlay, SegmentPanel, useSegmentation} from './segment';
import {BodyPanel} from './body-panel';
import {AreaOverlay,AreaPanel,AreaTool,useAreaEditor} from './area-tools';
import {PortraitPanel} from './portrait';
import {PeopleBoxes,PeopleChoices,PeopleContext,usePhotoPeople} from './people';
import {PersonSearchContext,PersonSearchOverlay,usePersonSearch} from './person-search';
import {frameChanges,frameLayout,frameValues} from './framing';
import {useFrameDraft} from './frame-draft';
import {draftLabel,useEditingDrafts} from './editing-drafts';
import {FramingOverlay,FramingPanel} from './framing-panel';
import type {FrameTool} from './frame-marks';
import {ExportDialog,ExportSettings} from './export-settings';
import {mayHaveAlpha,PaperView,TransparencyControl} from './transparency';
import {InputGamut} from './input-gamut';
import {AdjustedPanel,AdjustmentTarget,ControlVisit,controlVisit} from './adjusted';

type Mode = 'editor' | 'library';
type Inspector = 'manual' | 'ai' | 'history' | 'local' | 'body' | AreaTool;
type Dialog = 'removed' | 'batch' | 'sets' | 'relink' | 'capture' | 'preset' | 'keys' | 'recipe' | 'copyRecipe' | 'info' | 'projects' | 'exportResult' | 'exportSettings' | 'exportSaved' | null;
type StudioState = Snapshot & {languagePreference?:LanguagePreference;providerName?: string; importErrors?: unknown[];maskSlot?:number;draftIndex?:string[]};
const noop = () => {};
const empty: Photo[] = [];
function edgeForView(photo:Photo, zoom:number) {
  const pixels=Math.max(photo.width,photo.height)*zoom/100;
  return [480,768,1080,1440,2048].find(edge=>edge>=pixels)||2048;
}

function App() {
  const [state, setState] = useState<StudioState | null>(null);
  const stateRef = useRef<StudioState | null>(null);
  const [preference,setPreference]=useState<LanguagePreference>({language:null,askOnStartup:false});
  const [languageDialog,setLanguageDialog]=useState<'startup'|'settings'|null>(null);
  const languageInitialized=useRef(false);
  async function chooseLanguage(language:Language,askOnStartup:boolean){
    const saved=await invoke<LanguagePreference>('setLanguagePreference',{language,askOnStartup});
    applyLanguage(saved.language||language);setPreference(saved);setLanguageDialog(null);
  }
  const [startup, setStartup] = useState('');
  const [busy, setBusy] = useState('');
  const busyRef = useRef('');
  const [mode, setMode] = useState<Mode>('editor');
  const [criteria,setCriteria]=useState(allCriteria);
  const [inspector, setInspector] = useState<Inspector>('manual');
  const [adjustedPhoto,setAdjustedPhoto]=useState('');
  const [visit,setVisit]=useState<ControlVisit>();
  const [leftOpen, setLeftOpen] = useState(true);
  const [rightOpen, setRightOpen] = useState(true);
  const [stripOpen, setStripOpen] = useState(true);
  const [filter, setFilter] = useState<LibraryFilter>({kind: 'all'});
  const [query, setQuery] = useState('');
  const [dialog, setDialog] = useState<Dialog|'libraryStorage'|'copies'>(null);
  const [privacyReview,setPrivacyReview]=useState('');
  const [watchingCapture,setWatchingCapture]=useState(false);
  const [relinkGeneration,setRelinkGeneration]=useState(0);
  const [param, setParam] = useState<Control | null>(null);
  const [notice, setNotice] = useState<{text: string; error: boolean; id: string; restore?:{id:string;revision:number}} | null>(null);
  const noticeTimer = useRef<number | undefined>(undefined);
  const [draft, setDraft] = useState<Values>();
  const [frameResult,setFrameResult]=useState(false);
  const [frameTool,setFrameTool]=useState<FrameTool>('crop');
  const [frameIncomplete,setFrameIncomplete]=useState(false);
  const gesture=useRef(0);
  const [pending, setPending] = useState<StagedPlan | null>(null);
  const [chosen, setChosen] = useState<Recipe[]>([]);
  const [strength, setStrength] = useState(100);
  const [additive, setAdditive] = useState(false);
  const [chat, setChat] = useState<Record<string, ChatItem[]>>({});
  const [proposedName,setProposedName]=useState('');
  const [generating, setGenerating] = useState(false);
  const planJob = useRef('');
  const intent = useRef(0);
  const [compare, setCompare] = useState(false);
  const [clipping,setClipping]=useState(0);
  const [peek, setPeek] = useState(false);
  const [zoom, setZoom] = useState(100);
  const [showFaceBoxes,setShowFaceBoxes]=useState(true);
  const [faceSearchPhoto,setFaceSearchPhoto]=useState('');
  const [showPersonBoxes,setShowPersonBoxes]=useState(true);
  const [viewCommand, setViewCommand] = useState<ViewCommand>({action:'fit', serial:0});
  const [copied, setCopied] = useState<Values|null>(null);
  const [result, setResult] = useState<ExportResult|null>(null);
  const [paperView,setPaperView]=useState<PaperView>('canvas');
  const [batchToken,setBatchToken]=useState<string>();
  const [renderTarget,setRenderTarget]=useState({id:'',edge:768});
  const renderEdge=renderTarget.edge;
  const doc = state?.document || null;
  const white=useWhiteBalance(doc);
  const colorBand=useColorBand(doc);
  const picking=!!(white.session||colorBand.session);
  const [rangeSeed,setRangeSeed]=useState<{id:string;revision:number;x:number;y:number}>();
  function closePicking(){white.cancel();colorBand.cancel();}
  const showAdjusted=!!doc&&adjustedPhoto===doc.photoId;
  const currentVisit=visit?.photoId===doc?.photoId?visit:undefined;
  const catalog = state?.catalog || empty;
  const photo = catalog.find(p=>p.id === state?.selectedId) || null;
  const capabilities = state?.capabilities || {};
  const areaTool=mode==='editor'&&rightOpen&&(inspector==='privacy'||inspector==='guard')?inspector:null;
  const storedDrafts=useEditingDrafts(tell);
  const areaEditor=useAreaEditor(photo?.id,doc?.revision??0,areaTool,storedDrafts.write);
  const pendingPrivacy=(ids?:string[])=>storedDrafts.privacy(ids)||areaEditor.pendingPrivacy(ids);
  const areaStatus=photo&&pendingPrivacy([photo.id])?'隐私遮挡待应用':areaEditor.dirty?'文字保护待应用':'';
  const areaLocating=!!areaTool&&!areaEditor.preview;
  const canFrame=!!state?.controls.some(c=>c.key==='frame_angle');
  const crop=useFrameDraft(doc,storedDrafts.write);
  const frameDraft=crop.open||crop.hasDraft?crop.values:null;
  const frameEditing=crop.open&&inspector==='manual'&&mode==='editor'&&rightOpen;
  useEffect(()=>{if(!frameEditing)setFrameTool('crop');},[frameEditing,photo?.id]);
  const frameDelta=crop.hasDraft?frameChanges(crop.values,doc?.parameters):undefined;
  const committedFrame=frameLayout(photo?.width??1,photo?.height??1,doc?.parameters);
  const showFrameResult=frameEditing&&frameResult;
  const currentFrame=showFrameResult?frameLayout(photo?.width??1,photo?.height??1,frameDraft):committedFrame;
  const sourceToolsView=areaLocating || frameEditing&&!frameResult || committedFrame.active&&rightOpen&&(inspector==='local'||inspector==='body'||
    inspector==='ai'&&!!(state?.portraitPeople?.length||state?.bodyPeople?.length));
  const displayPhoto=photo&&!sourceToolsView&&currentFrame.active?{...photo,width:currentFrame.width,height:currentFrame.height}:photo;
  const frameView=sourceToolsView?'source':currentFrame.active?'output':'default';
  const layoutKey=frameView==='output'?JSON.stringify(frameValues(showFrameResult?frameDraft:doc?.parameters)):'';
  useEffect(()=>{if(inspector!=='manual'||mode!=='editor'||!rightOpen)crop.setOpen(false);},[inspector,mode,rightOpen]);
  const canBrush=capabilities.localEdits===true,canFace=capabilities.portraitDetection===true,canSegment=capabilities.segmentation===true;
  const local=useLocalEdits(photo,doc?.revision??0,canBrush && mode==='editor' && (inspector==='local'||inspector==='ai'),tell,mode==='editor'&&rightOpen&&inspector==='local',storedDrafts.write);
  const finishKind=local.masks.find(m=>m.slot===local.slot)?.kind;
  const garmentMask=finishKind==='clothes'||finishKind==='fabric';
  const maskLightKeys=(finishKind==='lens'||finishKind==='fabric'?[]:['exposure','shadows','highlights','temperature','saturation'])
    .map(key=>(local.slot>0?'local_m'+local.slot+'_':'local_')+key);
  const finishLabel=finishKind==='skin'?'协调脸颈与手臂肤色':garmentMask?'淡化衣褶':finishKind==='backdrop'?'淡化背景布折痕':'';
  const finishKey='local_m'+local.slot+'_finish';
  if(finishLabel)maskLightKeys.push(finishKey);
  const finishPrefix='local_m'+local.slot+'_';
  maskLightKeys.push((local.slot>0?finishPrefix:'local_')+'strength');
  if(finishKind==='backdrop')maskLightKeys.push(finishPrefix+'backgroundBlur',finishPrefix+'backgroundOutput');
  if(finishKind==='skin')maskLightKeys.push(finishPrefix+'skintone');
  if(garmentMask||finishKind==='backdrop')maskLightKeys.push(finishPrefix+'finefold',finishPrefix+'broadfold');
  if(garmentMask)maskLightKeys.push(finishPrefix+'fabricguard');
  if(finishKind==='lens')maskLightKeys.push(finishPrefix+'lensglare');
  const segment=useSegmentation(photo?.id||null,doc?.revision??0,renderEdge);
  const region=localSection(local.section,canBrush,canFace,canSegment);
  const faceMode=region==='face',segmentMode=region==='segment'&&canSegment;
  const faceLightOnly=!!photo&&faceSearchPhoto===photo.id&&inspector==='local'&&faceMode;
  useEffect(()=>{if(inspector!=='local'||!faceMode||mode!=='editor'||photo?.id!==faceSearchPhoto)setFaceSearchPhoto('');},
    [inspector,faceMode,mode,photo?.id,faceSearchPhoto]);
  const segmentView=segmentMode&&inspector==='local'&&rightOpen&&segment.active;
  const subjects=usePhotoPeople(state,()=>{abandonTransient();setParam(null);local.cancelMask();});
  const search=usePersonSearch(photo?.id,doc?.revision??0,mode==='editor'&&rightOpen&&(inspector==='body'||inspector==='local'&&faceMode),
    (next,index)=>{if(adoptBody(next))subjects.focus(next,index);},()=>void manualPersonMask());
  useEffect(()=>{if(search.active?.phase==='draw'){abandonTransient();setCompare(false);setPeek(false);}},[search.active?.phase]);
  const warpedHeal=region==='brush'&&(local.tool==='redEye'||isSpotRepair(local.tool)&&(doc?.parameters.local_warp??100)!==0&&local.strokes.some(s=>
    s.kind==='warp'&&s.strength>0&&s.points.length===2&&(s.points[0].x!==s.points[1].x||s.points[0].y!==s.points[1].y)));
  const healView=warpedHeal&&inspector==='local'&&rightOpen;
  const rangeView=inspector==='local'&&rightOpen&&!!local.range;
  const neutralPick=areaLocating||segmentView||(healView&&!local.healPreview)||!!search.active||rangeView&&local.visible&&local.bypassSlot===undefined;
  useEffect(()=>{if(rangeView){setCompare(false);setPeek(false);closePicking();}},[rangeView]);
  useEffect(()=>{
    if(healView&&!local.healPreview){setCompare(false);setPeek(false);closePicking();}
  },[healView,local.healPreview]);
  const localShapes = inspector==='local'&&region==='brush'&&local.dirty&&!local.conflict ? local.strokes : undefined;
  const localGradient=inspector==='local'&&region==='brush'?local.gradientPreview??local.rangePreview:undefined;
  const baseVisible = useMemo(()=>filterPhotos(catalog,filter,query,state?.projects).filter(p=>matchesCriteria(p,criteria)),[catalog,filter,query,state?.projects,criteria]);
  const gridExposure=useGridExposure(baseVisible,mode==='library'&&capabilities.setReview===true);
  const visible=gridExposure.items;
  const groupRecipes = chosen.length && doc && state ? recipeValues(chosen,state.controls,doc,strength,additive) : undefined;
  const adjustment=pending?.action==='savePreset'?null:pending;
  const selectedProposal=adjustment&&Object.keys(adjustment.selected).length?adjustment.selected:undefined;
  const previewValues = showFrameResult&&frameDelta&&Object.keys(frameDelta).length?frameDelta:white.values || draft || selectedProposal || groupRecipes;
  const cutout=Array.from({length:7},(_,i)=>'local_m'+(i+1)+'_backgroundOutput').some(key=>(previewValues?.[key]??doc?.parameters[key])===1);
  const canvasId=mode==='editor'&&photo?.id===renderTarget.id?renderTarget.id:null;
  // A frame rendered under a previous spatial resource can still contain newly concealed pixels.
  const maskResource=[0,1,2,3,4].map(i=>doc?.parameters['mask_ref'+i]??0);
  const rangeReference=rangeView&&neutralPick&&maskResource.some(Boolean)?
    local.range?.token?local.range.reference??undefined:null:undefined;
  const editedLayout=maskResource.some(Boolean)?layoutKey+'/mask/'+maskResource.join('/'):layoutKey;
  const edited = usePreview(canvasId, doc?.revision ?? 0, previewValues, false, renderEdge, localShapes,
    draft&&!busy?gesture.current:undefined,!!draft&&!!busy,local.slot,frameView,editedLayout,localGradient,neutralPick||picking?0:clipping,local.bypassSlot,
    areaTool&&areaEditor.preview&&areaEditor.dirty?{areaTool,areas:areaEditor.marks}:undefined);
  const original = usePreview((compare||peek)&&!frameEditing||neutralPick ? canvasId : null, doc?.revision ?? 0, undefined, true,renderEdge,undefined,undefined,false,0,frameView,layoutKey);
  const working = !!busy || !state;
  const editableControls=useMemo(()=>publicControls(state?.controls||[]),[state?.controls]);
  const hasSavedPaint=!!local.alpha||!!local.savedGradient||local.base.some(s=>(s.kind==='paint'||s.kind==='maskPaint')&&s.strength>0);
  const savedWarp=local.base.some(s=>s.kind==='warp'&&s.strength>0);
  const faceSelected=!!doc && doc.parameters.portrait_face_w>0 && doc.parameters.portrait_face_h>0;
  const portraitPeople=state?.portraitPeople??[];
  const privacyFaces=portraitPeople.length?portraitPeople.map(({box})=>({x:box.x,y:box.y,width:box.w,height:box.h})):local.faces?.faces??[];
  const facePerson=subjects.active?.face;
  const segmentSaved=!!doc&&[0,1,2,3,4].some(i=>{
    const part=doc.parameters['segment_ref'+i];return Number.isFinite(part)&&part!==0;
  });
  const segmentKeys=['segment_foreground','segment_background'];
  const bodyPeople=state?.bodyPeople??[];
  const bodyScene=doc?[doc.photoId,...[0,1,2,3,4].map(i=>doc.parameters['body_ref'+i])].join('/'):'';
  const bodyPerson=subjects.active?.body;
  const canLink=segmentSaved&&segmentKeys.every(key=>!doc?.locks.includes(key)&&editableControls.some(c=>c.key===key));
  const portraitControl=(c:Control)=>c.key.startsWith('portrait_') || /人像|肤质/.test(c.group);
  const activeControls=useMemo(()=>editableControls.filter(c=>(!portraitControl(c)||(c.key.startsWith('portrait_p')?facePerson?.available.includes(c.key):faceSelected&&!subjects.all.length)) && (!c.key.startsWith('segment_')||segmentSaved) && (!c.key.startsWith('body_')||bodyPerson?.available.includes(c.key)))
    .map(c=>c.key===finishKey&&finishLabel?{...c,label:finishLabel}:c.key.startsWith('portrait_p')?{...c,label:c.label.replace(/^人脸 \d+ · /,'')}:
      c.group==='人体美型'&&bodyPerson&&bodyPerson.index>0?{...c,label:c.label.replace(/^人物 \d+ · /,'')}:c),[editableControls,facePerson,bodyPerson,faceSelected,subjects.all.length,finishKey,finishLabel,segmentSaved]);
  const aiControls=activeControls.filter(c=>!c.key.startsWith('local_') || (c.key==='local_warp'?savedWarp:hasSavedPaint&&(maskLightKeys.includes(c.key)||local.slot===0&&c.key==='local_smooth')));
  const lightTool=region==='brush'&&['paint','erase','hand'].includes(local.tool);
  const regionalControls=activeControls.filter(c=>faceMode?portraitControl(c):segmentMode?segmentKeys.includes(c.key)&&!segment.active:(local.slot===0&&c.key.startsWith('local_')&&c.group!=='独立蒙版'&&local.strokes.length>0&&(!lightTool||!maskLightKeys.includes(c.key))));
  const savedLocal=useMemo(()=>state?.controls.some(c=>c.group==='局部资源' && (doc?.parameters[c.key]??c.initial)!==c.initial),[state?.controls,doc?.parameters]);
  const manualControls=useMemo(()=>activeControls.filter(c=>c.group!=='独立蒙版'&&(!c.key.startsWith('local_')||savedLocal||local.base.length>0)),[activeControls,savedLocal,local.base.length]);
  const duplicateName=!!proposedName.trim() && [...(state?.presets||[]),...(state?.userPresets||[])]
    .some(p=>p.name.toLocaleLowerCase()===proposedName.trim().toLocaleLowerCase());

  function tell(text: string, error = false, restore?:{id:string;revision:number}) {
    window.clearTimeout(noticeTimer.current);
    setNotice({text, error, id:requestId(), restore});
    noticeTimer.current = window.setTimeout(()=>setNotice(null), restore?12000:error?8500:4300);
  }
  function appendChat(id: string, who: ChatItem['who'], text: string) {
    const item={id:requestId(), who, text};
    setChat(all=>({...all,[id]:[...(all[id] || []),item].slice(-80)}));
  }
  function abandonTransient() {
    intent.current++;
    setDraft(undefined); setPending(null); setChosen([]); closePicking();
    crop.setOpen(false);
  }
  function adopt(next: StudioState) {
    if (!next || !Array.isArray(next.catalog) || !Array.isArray(next.controls)) throw new Error('本机服务返回了无效的工作区状态。');
    const prior = stateRef.current;
    if(crop.reconcile(next.document))tell('构图或参数锁已变更，已载入这张照片最新保存的取景。');
    if(prior?.selectedId!==next.selectedId || prior?.document?.revision!==next.document?.revision) {
      abandonTransient(); setParam(null);
      if(prior?.selectedId!==next.selectedId) {
        setAdjustedPhoto('');setVisit(undefined);
        planJob.current=''; setGenerating(false); setPeek(false); setCompare(false);
        setViewCommand(v=>({action:'fit',serial:v.serial+1}));
      }
    }
    stateRef.current=next; setState(next); setStartup('');
  }
  function start(action: string) {
    if(busyRef.current) return false;
    busyRef.current=action; setBusy(action); return true;
  }
  function end() {busyRef.current=''; setBusy('');}
  async function loadDrafts(next:StudioState){
    const target=next.document;
    if(target)await storedDrafts.load(target.photoId,(kind,data)=>{
      if(kind==='crop')crop.restore(target,data);
      else if(kind==='privacy')areaEditor.restore(target.photoId,data);
      else local.restoreDraft(target.photoId,kind,data);
    });
  }
  async function init() {
    if (!start('正在打开工作区')) return;
    try {const next=await invoke<StudioState>('init');
      if(!languageInitialized.current&&next.languagePreference){
        languageInitialized.current=true;const pref=next.languagePreference;applyLanguage(pref.language||'zh-CN');setPreference(pref);
        if(!pref.language||pref.askOnStartup)setLanguageDialog('startup');
      }
      storedDrafts.begin(next.draftIndex);await loadDrafts(next);adopt(next);} catch(error){setStartup(messageOf(error));} finally {end();}
  }
  useEffect(()=>{void init(); return ()=>window.clearTimeout(noticeTimer.current);},[]);

  async function mutate(method: string, args: Args={}, status = '正在保存'): Promise<StudioState|null> {
    if (!start(status)) return null;
    try {
      const next = await invoke<StudioState>(method,args);
      if(next.selectedId!==stateRef.current?.selectedId)await loadDrafts(next);
      adopt(next);
      if(next.importErrors?.length) tell(next.importErrors.map(messageOf).join('、'), true);
      return next;
    } catch(error) {
      const text = messageOf(error);
      if(/revision|修订|过期|stale|版本冲突|新的编辑版本/i.test(text)) {
        try { adopt(await invoke<StudioState>('init')); } catch { /* 保留当前视图，错误可重试。 */ }
      }
      tell(text,true); return null;
    } finally {end();}
  }
  function versionArgs() {
    const d=stateRef.current?.document;
    return d ? {id:d.photoId, revision:d.revision} : null;
  }
  async function docAction(method:string, extra: Args={}) {
    const base=versionArgs(); if(!base) return null;
    return mutate(method,{...base,...extra});
  }
  function submitValues(values: Values,label:string,source: 'manual'|'ai'='manual', token=requestId()) {
    return docAction('commit',{values,label,source,requestId:token});
  }
  function openSampler(kind:'white'|'band'){
    if(kind==='white'?white.session:colorBand.session){closePicking();return;}
    if(photo&&pendingPrivacy([photo.id])){tell('先在“隐私遮挡”中应用或放弃这张照片的草稿，再取样。');return;}
    abandonTransient();setCompare(false);setPeek(false);showInspector('manual');
    if(kind==='white')white.begin();
    else void colorBand.begin(committedFrame.active?'output':'default');
  }
  function pickBand(x:number,y:number){
    if(!doc||!colorBand.session)return;
    const point=colorBand.session.frameView==='output'?committedFrame.toSource(x,y):{x,y};
    void colorBand.pick(point,band=>{
      const key='hsl_'+band+'_sat';
      setVisit(controlVisit(doc.photoId,key,()=>setParam(state?.controls.find(c=>c.key===key)??null)));
    });
  }
  function openLocalColor(){
    const point=colorBand.session?.point;
    if(!point||!doc)return;
    setRangeSeed({id:doc.photoId,revision:doc.revision,...point});
    closePicking();local.setSection('brush');local.setTool('paint');local.setVisible(true);showInspector('local');
  }
  useEffect(()=>{
    if(!rangeSeed)return;
    if(rangeSeed.id!==doc?.photoId||rangeSeed.revision!==doc.revision||inspector!=='local'||local.section!=='brush'){setRangeSeed(undefined);return;}
    if(!local.ready&&!local.loadError)return;
    setRangeSeed(undefined);
    if(local.loadError||local.dirty||local.conflict){tell(local.loadError||'请先保存或放弃当前画笔草稿，再创建颜色范围。',true);return;}
    if(local.masks.filter(m=>m.slot>0).length>=7){tell('独立蒙版已满，请先整理现有蒙版。',true);return;}
    local.startRange('colorRange',true,{x:rangeSeed.x,y:rangeSeed.y});
  },[rangeSeed,doc?.photoId,doc?.revision,inspector,local.ready,local.loadError,local.section]);
  function applyWhite(){
    if(!white.values||!white.active||!doc||!edited.ready||busyRef.current)return;
    const current=stateRef.current?.document;
    if(current?.photoId===doc.photoId&&current.revision===doc.revision)
      void submitValues(white.values,'白平衡吸管 · 候选 '+white.session?.active,'manual',white.active.token);
  }
  useEffect(()=>{if(mode!=='editor'||inspector!=='manual')closePicking();},[mode,inspector]);
  function setView(action:ViewCommand['action']) {setViewCommand(v=>({action,serial:v.serial+1}));}
  function showInspector(value: Inspector) {setAdjustedPhoto('');setVisit(undefined);setMode('editor');setRightOpen(true);setInspector(value);}
  function openArea(tool:AreaTool){abandonTransient();setCompare(false);setPeek(false);showInspector(tool);areaEditor.setPreview(false);setView('fit');}
  function handleRecovered(key:string,keep:boolean){
    if(!doc)return;
    const id=doc.photoId,kind=key.slice(id.length+1);
    if(keep){
      abandonTransient();setCompare(false);setPeek(false);setView('fit');
      if(kind==='crop'){showInspector('manual');setFrameResult(false);crop.setOpen(true);}
      else if(kind==='privacy')openArea('privacy');
      else {showInspector('local');local.openRecovered(id,kind);}
    }else if(kind==='crop')crop.discard(id);
    else if(kind==='privacy')areaEditor.discard(id);
    else local.discardRecovered(id,kind);
    storedDrafts.acknowledge(key);
  }
  async function saveAreas(){
    if(!photo||!doc||!areaTool||!areaEditor.ready||!areaEditor.dirty)return;
    const next=await mutate('areaSave',{id:photo.id,revision:doc.revision,areaTool,areas:areaEditor.marks});
    if(next){areaEditor.saved();showInspector(areaTool==='privacy'?'manual':'body');setView('fit');}
  }
  function locateAdjustment(target:AdjustmentTarget) {
    if(!doc||!state||working)return;
    const c=state.controls.find(c=>c.key===target.key);
    if(!c)return;
    const details=()=>setParam(target.title?{...c,label:target.title}:c);
    if(target.panel==='details'){details();return;}
    abandonTransient();setCompare(false);setPeek(false);setFaceSearchPhoto('');
    if(target.person!==undefined&&subjects.active?.index!==target.person)subjects.focus(state,target.person);
    switch(target.panel){
      case 'face': local.setSection('face');showInspector('local');break;
      case 'body': showInspector('body');break;
      case 'mask':
        local.setSection('brush');local.setTool('paint');local.chooseMask(target.slot??0);showInspector('local');break;
      case 'warp':
        local.setSection('brush');local.setTool('warp');showInspector('local');break;
      case 'segment':
        if(segment.active)segment.cancel();
        local.setSection('segment');showInspector('local');break;
      case 'frame':
        showInspector('manual');setFrameTool(target.key.startsWith('frame_doc_')?'page':'crop');setFrameResult(false);crop.setOpen(true);setView('fit');break;
      case 'raw': showInspector('manual');setDialog('info');break;
      default: showInspector('manual');
    }
    setVisit(controlVisit(doc.photoId,target.key,details));
  }
  function adoptBody(snapshot:Snapshot) {
    const live=stateRef.current?.document;
    if(!doc||live?.photoId!==doc.photoId||live.revision!==doc.revision||snapshot.document?.photoId!==doc.photoId)return false;
    adopt(snapshot);return true;
  }
  async function manualPersonMask(){
    abandonTransient();setCompare(false);setPeek(false);local.setSection('brush');local.setTool('paint');local.setVisible(true);showInspector('local');
    if(local.dirty){tell('先保存或取消当前蒙版草稿，再新建手绘蒙版。');return;}
    const next=await docAction('maskManage',{operation:'add',kind:'brush'});
    if(next?.maskSlot!==undefined)local.chooseMask(next.maskSlot);
  }
  async function addPhotos(method: 'sample'|'importFiles'|'importFolder') {
    const before=stateRef.current;
    const next = await mutate(method,{},'正在导入照片');
    if(next?.selectedId && (method==='sample' || next.selectedId!==before?.selectedId || next.catalog.length!==before?.catalog.length)) {
      setFilter({kind:'all'}); setQuery(''); setMode('editor');
    }
  }
  async function select(p: Photo) {
    if(busyRef.current) return;
    if(p.id!==stateRef.current?.selectedId && !await mutate('select',{id:p.id},'正在打开照片')) return;
    setMode('editor');
  }
  function navigate(direction: number) {
    const pos=visible.findIndex(p=>p.id===photo?.id);
    const target=visible[pos+direction];
    if(target) void select(target);
  }
  function favorite(p:Photo) {void mutate('favorite',{id:p.id,value:!p.favorite});}
  async function removePhoto(target:Photo) {
    const position=visible.findIndex(p=>p.id===target.id);
    const neighbor=position<0?undefined:visible[position+1]||visible[position-1];
    const revision=target.revision??0;
    const next=await mutate('removePhoto',{id:target.id,revision,...(neighbor?{nextId:neighbor.id}:{})},'正在从图库移除');
    if(!next)return;
    if(photo?.id===target.id&&!neighbor&&visible.length===1&&(filter.kind!=='all'||query.trim()))setMode('library');
    tell('已从图库移除「'+target.name+'」。磁盘原片和编辑记录已保留。',false,{id:target.id,revision:revision+1});
  }
  function draftMany(values:Values) {
    if(!draft)gesture.current++;
    intent.current++; setPending(null); setChosen([]); closePicking();
    setDraft(values);
  }
  async function commitMany(values:Values,label:string) {
    const current=stateRef.current?.document;
    if(!doc||!current||current.photoId!==doc.photoId||current.revision!==doc.revision)return;
    draftMany(values);
    const changes:Values={}; for(const [k,v] of Object.entries(values))if(v!==doc?.parameters[k])changes[k]=v;
    const keys=Object.keys(changes);
    if(keys.length && inspector==='local' && lightTool && local.dirty && keys.every(k=>maskLightKeys.includes(k))) {
      await local.save(async(id,revision,strokes,maskSlot)=>(await mutate('localSave',{id,revision,strokes,maskSlot,values:changes}))?.document?.revision??null);
    } else if(keys.length) {
      const target=keys.every(k=>k.startsWith('portrait_p'))&&facePerson&&subjects.active?'人脸 '+(subjects.active.index+1)+' · ':
        keys.every(k=>k.startsWith('body_'))&&bodyPerson?'人物 '+(bodyPerson.index+1)+' · ':'';
      await submitValues(changes,target+label);
    }
    setDraft(undefined);
  }
  function togglePreset(recipe:Recipe) {
    if(!doc||busyRef.current) return;
    intent.current++; setPending(null); setDraft(undefined); closePicking(); setMode('editor');
    if(!chosen.length) {setStrength(100); setAdditive(false);}
    setChosen(list=>list.some(p=>sameRecipe(p,recipe))?list.filter(p=>!sameRecipe(p,recipe)):[...list,recipe]);
  }
  async function applyPreset() {
    if(!groupRecipes || !Object.keys(groupRecipes).length) return;
    if(await submitValues(groupRecipes, chosen.map(p=>p.name).join(' + ')+' · '+strength+'%')) setChosen([]);
  }
  function stageProposal(proposal:Plan,base:{id:string;revision:number},epoch:number,scope:string,controls:Control[],origin:'ai'|'reference') {
    const now=stateRef.current?.document;
    if(!now || now.photoId!==base.id || now.revision!==base.revision || intent.current!==epoch || proposal.revision!==base.revision || proposal.id!==base.id) {
      appendChat(base.id,'assistant','编辑状态已变化。这次提案已作废，可按当前调整重新生成。');return;
    }
    const entries=Object.entries(proposal.values || {});
    if(proposal.action==='savePreset'){
      if(origin!=='ai'||!proposal.requestId||entries.length)throw new Error('保存预设的请求格式无效，请重新发送。');
      setProposedName(typeof proposal.suggestedName==='string'?proposal.suggestedName.trim().slice(0,80):'');
      setPending({...proposal,scope,origin,groups:[],selected:{}});return;
    }
    if(!entries.length){appendChat(base.id,'assistant',proposal.message || '本次提案未产生参数变化。');return;}
    if(!proposal.requestId || entries.some(([key,value])=>{
      const c=controls.find(item=>item.key===key);
      return !c || !Number.isFinite(value) || value<c.min || value>c.max || now.locks.includes(key) || (scope!=='all' && c.group!==scope);
    }))throw new Error('提案中的参数超出了允许范围，请重试。');
    const groups=proposal.groups??entries.map(([key])=>[key]);
    if(!Array.isArray(groups)||groups.some(keys=>!Array.isArray(keys)||!keys.length))throw new Error('提案的参数分组无效，请重新生成。');
    const keys=groups.flat();
    if(keys.length!==entries.length||new Set(keys).size!==keys.length||keys.some(key=>!Object.hasOwn(proposal.values,key)))
      throw new Error('提案的参数分组不完整，请重新生成。');
    setDraft(undefined);setChosen([]);closePicking();setPending({...proposal,scope,origin,groups,selected:proposal.values});
  }
  async function plan(text:string,scope:string) {
    const base=versionArgs();
    if(!base || planJob.current || busyRef.current) return;
    abandonTransient();
    const originalIntent=intent.current, job=requestId();
    planJob.current=job; setGenerating(true); appendChat(base.id,'user',text);
    try{
      const instruction=/本地指令|本地规则|offline|local rules/i.test(sourceName)?offlineInstruction(text):text;
      const proposal=await invoke<Plan>('plan',{...base,text:instruction,scope,subjectIndex:subjects.active?.index??-1,personIndex:bodyPerson?.index,faceIndex:facePerson?.index,maskSlot:Math.max(0,local.slot)});
      if(planJob.current!==job) return;
      stageProposal(proposal,base,originalIntent,scope,aiControls,'ai');
    } catch(error) {
      if(planJob.current===job) appendChat(base.id,'assistant',messageOf(error));
    } finally { if(planJob.current===job) {planJob.current=''; setGenerating(false);} }
  }
  async function matchColor(){
    const base=versionArgs();
    if(!base||busyRef.current||planJob.current)return;
    const job=requestId(), epoch=intent.current;
    planJob.current=job;setGenerating(true);setDialog(null);
    try{
      const proposal=await invoke<Plan|null>('matchColor',base);
      if(planJob.current!==job||!proposal)return;
      showInspector('ai');
      stageProposal(proposal,base,epoch,'all',activeControls,'reference');
    }catch(error){if(planJob.current===job)tell(messageOf(error),true);}
    finally{if(planJob.current===job){planJob.current='';setGenerating(false);}}
  }
  async function autoBody(personIndex:number){
    const current=stateRef.current, doc=current?.document;
    if(!current||!doc||busyRef.current)return;
    const person=current.bodyPeople?.find(p=>p.index===personIndex);
    if(!person)return;
    abandonTransient();
    const base={id:doc.photoId,revision:doc.revision},epoch=intent.current;
    try {
      const proposal=await invoke<Plan>('autoBody',{...base,personIndex});
      stageProposal(proposal,base,epoch,'人体美型',current.controls.filter(c=>person.available.includes(c.key)),'ai');
    } catch(error) {tell(messageOf(error),true);}
  }
  function toggleProposalGroup(keys:string[]) {
    if(busyRef.current)return;
    setPending(previous=>{
      if(!previous||!previous.groups.includes(keys))return previous;
      const selected={...previous.selected}, remove=Object.hasOwn(selected,keys[0]);
      for(const key of keys) {
        if(remove)delete selected[key];
        else selected[key]=previous.values[key];
      }
      return {...previous,selected};
    });
  }
  async function acceptPlan() {
    if(!pending || !doc || !selectedProposal || !edited.ready || edited.error) return;
    const current=stateRef.current?.document;
    if(!current || pending.revision!==current.revision || pending.id!==current.photoId) {abandonTransient(); tell('照片已改变，请重新生成提案。',true); return;}
    const count=pending.groups.filter(keys=>Object.hasOwn(selectedProposal,keys[0])).length;
    const label=count===pending.groups.length?pending.label:pending.label.slice(0,210)+'（已选 '+count+'/'+pending.groups.length+' 项）';
    const args:Args={id:pending.id,revision:pending.revision,values:selectedProposal,label,source:'ai',requestId:pending.requestId};
    if(pending.scope && pending.scope!=='all')args.module=pending.scope;
    if(await mutate('commit',args)) {setPending(null);appendChat(doc.photoId,'assistant','已应用「'+label+'」。');}
  }
  async function confirmPreset(){
    const proposal=pending, current=stateRef.current?.document, name=proposedName.trim();
    if(!proposal||proposal.action!=='savePreset'||!name||duplicateName)return;
    if(!current||current.photoId!==proposal.id||current.revision!==proposal.revision){
      abandonTransient();tell('照片已改变，请重新发起保存预设。',true);return;
    }
    const next=await mutate('confirmPreset',{id:proposal.id,revision:proposal.revision,requestId:proposal.requestId,name},'正在保存预设');
    if(next){setPending(null);setProposedName('');appendChat(proposal.id,'assistant','已保存预设「'+name+'」。');}
  }
  function cancelPlan() {
    if(doc&&pending)appendChat(doc.photoId,'assistant',pending.action==='savePreset'?'已取消保存预设。':'已取消「'+pending.label+'」。');
    if(adjustment)void invoke('discardProposal',{id:adjustment.id,revision:adjustment.revision,requestId:adjustment.requestId}).catch(error=>tell(messageOf(error),true));
    abandonTransient();
  }
  async function configure() {
    if(!start('正在配置服务')) return;
    try {await invoke('providerSettings'); adopt(await invoke<StudioState>('init'));} catch(error){tell(messageOf(error),true);} finally{end();}
  }
  function checkPrivacyDrafts(ids:string[]){
    const id=pendingPrivacy(ids);
    if(!id)return true;
    setDialog(null);setPrivacyReview(id);return false;
  }
  async function returnToPrivacy(){
    const id=privacyReview;
    if(id!==stateRef.current?.selectedId && !await mutate('select',{id},'正在打开照片'))return;
    setPrivacyReview('');openArea('privacy');
  }
  async function doExport(ids?:string[],confirmed=false,settings?:ExportSettings) {
    if(!checkPrivacyDrafts(ids??(photo?[photo.id]:[])))return;
    if(!ids&&!confirmed&&(photo&&storedDrafts.has(photo.id)||adjustment||draft||white.values||chosen.length||areaEditor.dirty||local.dirty||local.gradient||local.range||local.category?.alpha||segment.state.phase==='ready'||frameDelta&&Object.keys(frameDelta).length)){setDialog('exportSaved');return;}
    if(!ids&&!settings&&canFrame){setDialog('exportSettings');return;}
    if(!photo && !ids?.length || !start('正在导出')) return;
    const job=ids&&canFrame?requestId():undefined;
    setBatchToken(job);
    try {
      const outcome=await invoke<ExportResult>(ids?'batchExport':'export',{...(ids?{ids,batchToken:job}:{id:photo!.id,revision:doc?.revision}),...settings});
      if(outcome.count || outcome.errors?.length||outcome.cancelled) {setResult(outcome); setDialog('exportResult');}
    } catch(error){tell(messageOf(error),true);} finally{setBatchToken(undefined);end();}
  }
  async function copyFinal(){
    if(!photo||!doc||!checkPrivacyDrafts([photo.id]))return;
    if(storedDrafts.has(photo.id)||adjustment||draft||white.values||chosen.length||areaEditor.dirty||local.dirty||local.gradient||local.range||local.category?.alpha||segment.state.phase==='ready'||frameDelta&&Object.keys(frameDelta).length){
      tell('请先应用或放弃当前预览，再复制成图。');return;
    }
    if(!start('正在复制成图'))return;
    try{
      const image=await invoke<{width:number;height:number}>('copyFinalImage',{id:photo.id,revision:doc.revision});
      tell('已复制成图 · '+image.width+' × '+image.height+' 像素');
    }catch(error){tell(messageOf(error),true);}finally{end();}
  }
  function copy() { if(doc) {setCopied({...doc.parameters}); setDialog(null); tell('已复制当前照片的参数。');} }
  const [paramAction,setParamAction]=useState<{mode:ActionMode;id:string;revision:number;person?:number}|null>(null);
  useEffect(()=>setParamAction(null),[doc?.photoId,doc?.revision,subjects.active?.index]);
  function openParamAction(mode:ActionMode){
    if(!doc)return;setDialog(null);setParamAction({mode,id:doc.photoId,revision:doc.revision,person:subjects.active?.index});
  }
  function togglePanels() {const next=!leftOpen&&!rightOpen; setLeftOpen(next); setRightOpen(next);}
  const brushHistory=mode==='editor'&&rightOpen&&inspector==='local'&&region==='brush'&&(local.dirty||local.canUndoDraft||local.canRedoDraft);
  function stepHistory(redo:boolean){
    if(brushHistory)local.stepDraft(redo);
    else if(redo?doc?.canRedo:doc?.canUndo)void docAction(redo?'redo':'undo');
  }
  useEffect(()=>{
    const down=(e:KeyboardEvent)=>{
      if(document.querySelector('[data-language-picker]'))return;
      const element=e.target as HTMLElement;
      const key=e.key.toLowerCase(), ctrl=e.ctrlKey||e.metaKey;
      const sliderUndo=element?.tagName==='INPUT' && (element as HTMLInputElement).type==='range' && ctrl && (key==='z'||key==='y');
      if(e.isComposing || dialog || privacyReview || param || (!sliderUndo&&['INPUT','TEXTAREA','SELECT'].includes(element?.tagName)) || element?.isContentEditable) return;
      const call=(fn:()=>void)=>{e.preventDefault(); fn();};
      if(ctrl && !e.altKey && key==='o') return call(()=>void addPhotos('importFiles'));
      if(!doc || !photo || busyRef.current) return;
      if(ctrl && (key==='z'||key==='y'))return call(()=>stepHistory(key==='y'||e.shiftKey));
      if(ctrl && e.shiftKey && key==='e') return call(()=>void doExport());
      if(ctrl || e.altKey) return;
      if(mode==='library' && capabilities.setReview && !e.repeat && keyMarks[key]){
        const target=visible.find(p=>p.id===photo.id)||visible[0],mark=keyMarks[key];
        if(target)return call(()=>void writeReview(target.id,'mark',mark).then(()=>tell(target.name+' · '+markLabels[mark]),err=>tell(messageOf(err),true)));
      }
      if(key==='delete'&&!e.repeat&&capabilities.libraryRemoval&&(mode==='editor'||visible.some(p=>p.id===photo.id))) return call(()=>void removePhoto(photo));
      if(key==='f8') return call(togglePanels);
      if(mode!=='editor') return;
      if(key==='arrowleft') return call(()=>navigate(-1));
      if(key==='arrowright') return call(()=>navigate(1));
      if(key==='0'||key==='1'||key==='+'||key==='='||key==='-') return call(()=>setView(key==='0'?'fit':key==='1'?'actual':key==='-'?'out':'in'));
      if(e.repeat) return;
      if(key==='f') return call(()=>favorite(photo));
      if(key==='c'&&!picking&&!neutralPick&&!frameEditing) return call(()=>setCompare(v=>!v));
      if(key===' ' && element?.tagName!=='BUTTON'&&!picking&&!neutralPick&&!frameEditing) return call(()=>setPeek(true));
      if(key==='escape') { closePicking(); if(search.active)search.cancel();else if(segmentView)segment.cancel();else if(pending) cancelPlan(); else if(chosen.length) abandonTransient(); else setCompare(false); }
    };
    const up=(e:KeyboardEvent)=>{if(e.code==='Space')setPeek(false);};
    const blur=()=>setPeek(false);
    window.addEventListener('keydown',down); window.addEventListener('keyup',up); window.addEventListener('blur',blur);
    return ()=>{window.removeEventListener('keydown',down); window.removeEventListener('keyup',up); window.removeEventListener('blur',blur);};
  });
  const selectFilter=(f:LibraryFilter)=>{setFilter(f); setQuery(''); setMode('library');};
  const [photoIndex, photoCount]=[visible.findIndex(p=>p.id===photo?.id),visible.length];
  const zoomChange=useCallback((v:number)=>{
    setZoom(v);
    if(displayPhoto)setRenderTarget(old=>old.id===displayPhoto.id?old:{id:displayPhoto.id,edge:edgeForView(displayPhoto,v)});
  },[displayPhoto?.id,displayPhoto?.width,displayPhoto?.height]);
  useEffect(()=>{
    if(!displayPhoto||renderTarget.id!==displayPhoto.id)return;
    const edge=edgeForView(displayPhoto,zoom);
    if(edge===renderTarget.edge)return;
    const timer=window.setTimeout(()=>setRenderTarget(old=>old.id===displayPhoto.id?{id:displayPhoto.id,edge}:old),130);
    return ()=>window.clearTimeout(timer);
  },[displayPhoto?.id,displayPhoto?.width,displayPhoto?.height,zoom,renderTarget]);
  const channel=state?.channel||'';
  const channelLabel=/stable|稳定/i.test(channel)?'稳定版':/dev|开发/i.test(channel)?'开发版':channel;
  const sourceName=state?.providerName || state?.provider?.name || '本地指令';
  const title=mode==='library'?'照片管理': photo?.name || '照片工作区';
  function manualPatch(control:Control,value:number,linked:boolean):Values{
    const patch:Values={[control.key]:value};
    if(!linked||!segment.linked||!canLink||!doc||!segmentKeys.includes(control.key))return patch;
    const other=editableControls.find(c=>segmentKeys.includes(c.key)&&c.key!==control.key);
    if(other)patch[other.key]=quantize(doc.parameters[other.key]+value-doc.parameters[control.key],other);
    return patch;
  }
  const parameters=(controls:Control[],linked=false,compact=false)=> <ParameterPanel key={currentVisit?.id} controls={controls} doc={doc} previewValues={previewValues} busy={working} compact={compact}
    visit={inspector==='local'&&region==='brush'&&!local.ready?undefined:currentVisit}
    openFaceLight={inspector==='manual'&&state?.portraitLandmarks&&photo?()=>{
      abandonTransient();setCompare(false);setPeek(false);setFaceSearchPhoto(photo.id);local.setSection('face');showInspector('local');
    }:undefined}
    draft={(c,v)=>draftMany(manualPatch(c,v,linked))} commit={(c,v)=>{const patch=manualPatch(c,v,linked);void commitMany(patch,Object.keys(patch).length>1?'联动前景与背景曝光':'调整'+c.label);}}
    draftMany={draftMany} commitMany={(p,l)=>void commitMany(p,l)} importLut={capabilities.importLut===true?()=>void docAction('importLut'):undefined} cancel={()=>setDraft(undefined)} more={setParam}/>;
  let editorOverlay:React.ReactNode;
  if(rightOpen&&photo&&local.bypassSlot===undefined){
    if(areaTool&&areaLocating)editorOverlay=<AreaOverlay tool={areaTool} editor={areaEditor} busy={working} w={photo.width} h={photo.height} faces={privacyFaces}/>;
    else if(search.active)editorOverlay=<PersonSearchOverlay/>;
    else if(inspector==='local'&&faceMode&&state?.portraitLandmarks){
      if(showFaceBoxes)editorOverlay=<PeopleBoxes view="face" disabled={working} passive={!!adjustment}/>;
    }else if(inspector==='body'&&showPersonBoxes)editorOverlay=<PeopleBoxes view="body" disabled={working} passive={!!adjustment}/>;
    else if(inspector==='ai')editorOverlay=<PeopleBoxes view="ai" disabled={working} passive={!!adjustment}/>;
    else if(inspector==='local'){
      if(segmentMode)editorOverlay=<SegmentOverlay editor={segment} busy={working}/>;
      else if(!(healView&&local.healPreview)&&(canBrush||canFace))editorOverlay=<>
        <LocalOverlay local={local} photo={photo} busy={working} faceMode={faceMode} originalHeal={healView}/>
        {lightTool&&!local.gradient&&!local.range&&<PeopleBoxes view="mask" disabled passive/>}
      </>;
    }
  }
  return <PeopleContext.Provider value={subjects}><PersonSearchContext.Provider value={search}><div className="studio-root" data-language-ready={state?true:undefined}>
    <header className="app-header">
      <div className="brand"><span className="brand-mark"><Mark/></span><span>AI<span className="brand-photo">Photo</span>Studio</span></div>
      <nav className="main-nav" aria-label="工作区模式">
        <button className={mode==='library'?'active':''} onClick={()=>setMode('library')}><Icon name="grid" size={15}/>图库</button>
        <button className={mode==='editor'&&(inspector==='manual'||inspector==='history')?'active':''} onClick={()=>showInspector('manual')}><Icon name="sliders" size={15}/>调色</button>
        {canFace&&<button className={mode==='editor'&&inspector==='local'&&faceMode?'active':''} onClick={()=>{abandonTransient();setCompare(false);local.setSection('face');showInspector('local');}}><Icon name="target" size={15}/>人像</button>}
        {capabilities.body===true&&<button className={mode==='editor'&&inspector==='body'?'active':''} onClick={()=>{abandonTransient();setCompare(false);showInspector('body');}}><Icon name="target" size={15}/>人体美型</button>}
        {canBrush&&<button title="画蒙版，调局部光影" className={mode==='editor'&&inspector==='local'&&lightTool?'active':''} onClick={()=>{abandonTransient();setCompare(false);setPeek(false);local.setSection('brush');local.setTool('paint');local.setVisible(true);showInspector('local');}}><Icon name="brush" size={15}/>蒙版</button>}
        {canBrush&&<button className={mode==='editor'&&inspector==='local'&&region==='brush'&&local.tool==='warp'?'active':''} onClick={()=>{abandonTransient();setCompare(false);local.setSection('brush');local.setTool('warp');showInspector('local');}}><Icon name="brush" size={15}/>液化</button>}
        <button className={mode==='editor'&&inspector==='ai'?'active':''} onClick={()=>showInspector('ai')}><Icon name="sparkle" size={15}/>AI 助手</button>
      </nav>
      <div className="top-actions"><IconButton icon="settings" label="设置" disabled={!state||working} onClick={()=>setLanguageDialog('settings')}/><button className="top-batch" disabled={!visible.length || working} onClick={()=>setDialog('batch')}><Icon name="stack" size={15}/>批量处理</button>
        <span className="top-separator"/><button className="secondary small" disabled={!photo||working} onClick={()=>void copyFinal()}>复制成图</button>
        <button className="primary export-top" disabled={!photo || working} onClick={()=>void doExport()}><Icon name="export" size={15}/>导出照片</button></div>
    </header>
    <div className={'workspace '+(!leftOpen?'left-hidden ':'')+(!rightOpen||mode==='library'?'right-hidden':'')}>
      {leftOpen&&<Sidebar state={state} filter={filter} onFilter={selectFilter} busy={working} files={()=>void addPhotos('importFiles')} folder={()=>void addPhotos('importFolder')} sample={()=>void addPhotos('sample')}
        swatchPhoto={mode==='editor'?photo:null} swatchesReady={edited.ready&&!working&&!dialog&&!privacyReview&&!areaTool&&!previewValues&&!frameEditing&&!local.dirty&&!localGradient&&!neutralPick&&!picking&&!compare&&!peek}
        chosen={chosen} togglePreset={togglePreset} savePreset={()=>setDialog('preset')} manageProjects={()=>setDialog('projects')} close={()=>setLeftOpen(false)}/>}
      <main className="center" data-paper={photo&&(cutout||mayHaveAlpha(photo.name))?paperView:'canvas'}>
        <div className="document-bar">
          {!leftOpen&&<IconButton icon="collapseL" label="展开左侧面板" onClick={()=>setLeftOpen(true)}/>}
          <span className="document-glyph"><Icon name={mode==='library'?'grid':'photo'} size={15}/></span>
          <strong translate={photo&&mode==='editor'?'no':undefined} title={photo && mode==='editor' ? photo.path : title}>{title}</strong>
          {photo && mode==='editor'&&<IconButton label={photo.favorite?'取消收藏':'收藏当前照片'} icon="star" className="doc-star" active={photo.favorite} disabled={working} onClick={()=>favorite(photo)}/>}
          {photo && mode==='editor'&&capabilities.libraryRemoval===true&&<button className="remove-current" title="从图库移除当前照片（Delete），保留磁盘原片" onClick={()=>void removePhoto(photo)} disabled={working}><Icon name="trash" size={14}/>从图库移除</button>}
          {photo&&mode==='editor'&&(cutout||mayHaveAlpha(photo.name))&&<TransparencyControl view={paperView} change={setPaperView}/>}
          <span className="spacer"/>
          {mode==='library'&&capabilities.libraryRemoval===true&&<button className="secondary small" onClick={()=>setDialog('removed')}>最近移除</button>}
          {capabilities.capture===true&&(mode==='library'||watchingCapture)&&<button className="secondary small" disabled={working} onClick={()=>setDialog('capture')}>{watchingCapture?'拍摄监听中':'拍摄导入'}</button>}
          {mode==='editor'&&<><IconButton label="撤销（Ctrl+Z）" icon="undo" size={15} disabled={!(brushHistory?local.canUndoDraft:doc?.canUndo)||working} onClick={()=>stepHistory(false)}/>
            <IconButton label="重做（Ctrl+Y）" icon="redo" size={15} disabled={!(brushHistory?local.canRedoDraft:doc?.canRedo)||working} onClick={()=>stepHistory(true)}/></>}
          {mode==='editor'&&!rightOpen&&<IconButton label="展开右侧面板" icon="collapseR" onClick={()=>setRightOpen(true)}/>}
        </div>
        {mode==='library'?<LibraryView photos={visible} exposure={gridExposure} selected={state?.selectedId||null} filter={filter} query={query} setQuery={setQuery} select={p=>void select(p)} favorite={favorite} files={()=>void addPhotos('importFiles')} busy={working} sample={()=>void addPhotos('sample')}
          criteria={criteria} changeCriteria={setCriteria} tools={state?.libraryTools&&<div className="button-line">
            <button className="secondary small" disabled={working||visible.length<2} onClick={()=>setDialog('copies')}>查找同画面副本</button>
            <button className="secondary small" disabled={working} onClick={()=>setDialog('libraryStorage')}>图库与空间</button></div>} review={capabilities.setReview===true?()=>setDialog('sets'):undefined}
          key={relinkGeneration} relink={capabilities.relinkOriginals===true?()=>setDialog('relink'):undefined}
          project={state?.projects?.find(p=>p.id===filter.projectId)} manageProjects={()=>setDialog('projects')} remove={capabilities.libraryRemoval===true?p=>void removePhoto(p):undefined}/>:
          <>
            {photo&&displayPhoto&&doc?<PhotoCanvas key={photo.id} photo={displayPhoto} edited={edited} original={original} compare={compare&&!neutralPick&&!frameEditing&&local.bypassSlot===undefined} peek={peek&&!neutralPick&&!frameEditing&&local.bypassSlot===undefined} command={viewCommand} onZoom={zoomChange} neutral={neutralPick} detailSourceView={sourceToolsView}
              detailEnabled={capabilities.viewport===true}
              cameraPoster={capabilities.viewport===true&&doc.cursor===0&&!draft&&!pending&&!chosen.length&&!picking&&!rangeView}
              picker={white.session?(x,y)=>void white.pick(x,y,frameView,renderEdge):colorBand.session&&!colorBand.session.showFinal?pickBand:undefined}
              pickerImage={colorBand.session&&!colorBand.session.showFinal?colorBand.session.input??null:rangeReference}
              pickerOverlay={white.session?<WhitePoints session={white.session}/>:colorBand.session?.showOverlay&&colorBand.session.result?.overlay?
                <img className="band-coverage" alt="全图颜色影响范围" src={colorBand.session.result.overlay} draggable={false}/>:undefined}
              overlay={frameEditing?(frameDraft&&!frameResult?<FramingOverlay photo={photo} values={frameDraft} change={crop.change} locks={doc.locks} busy={working} tool={frameTool} onIncomplete={setFrameIncomplete}/>:undefined):editorOverlay}
              transient={frameEditing?(frameResult?'构图成品预览':'在完整原图上取景'):segmentView?(segment.state.phase==='foreground'?'原片取样 · 点选前景':segment.state.phase==='background'?'原片取样 · 点选背景':segment.state.phase==='loading'?'正在计算分割…':'前景分割预览'):healView&&!local.healPreview?(local.tool==='sampleRepair'?(local.samplePoint?'原片定位 · 点污点':'原片定位 · 点干净纹理'):local.tool==='redEye'?'原片定位 · 点击红瞳中心':'原片定位 · 单击瑕疵中心'):white.session?'白平衡 · '+(white.session.active?'候选 '+white.session.active:'已保存效果'):colorBand.session?(colorBand.session.showFinal?'成片对照':'HSL 取色底图'):adjustment?(adjustment.origin==='reference'?'参考图仿色预览':'AI 提案预览'):chosen.length?'预设预览':localShapes?'局部笔迹预览':sourceToolsView?'完整原图 · 定位选区':''}/>:
              <div className="empty-stage"><div className="empty-frame"><i/><i/><i/><i/><span className="empty-lens"><Mark large/></span></div>
                <h1>{startup?'无法打开工作区':!state?'正在打开工作区…':'从一张照片开始'}</h1>
                <p>{startup || (state?'导入本地照片，随时调整光影与色彩。':'正在读取本地图库和编辑记录。')}</p>
                {startup?<button className="secondary" onClick={()=>void init()}>重试</button>:state&&<div className="button-line">
                  <button className="primary" disabled={working} onClick={()=>void addPhotos('importFiles')}><Icon name="plus" size={16}/>导入照片</button>
                  <button className="secondary" disabled={working} onClick={()=>void addPhotos('sample')}>打开示例<Icon name="right" size={12}/></button></div>}
              </div>}
            {!!chosen.length&&<div className="preset-compose"><div className="preset-description"><Icon name="stack" size={15}/><b>{chosen.map(p=>p.name).join(' + ')}</b><IconButton label="取消预设预览" icon="close" size={13} onClick={abandonTransient}/></div>
              <div className="preset-range"><label>强度 <b>{strength}%</b></label><input aria-label="预设强度" type="range" min="0" max="100" value={strength} onChange={e=>setStrength(Number(e.target.value))}/></div>
              <div className="preset-confirm"><label><input type="checkbox" checked={additive} onChange={e=>setAdditive(e.target.checked)}/>叠加到当前调整</label>
               <button className="primary small" disabled={working||!groupRecipes||!Object.keys(groupRecipes).length} onClick={()=>void applyPreset()}>应用预设</button></div>
            </div>}
            {white.session&&doc&&<WhiteBalancePanel tool={white} doc={doc} ready={edited.ready} busy={working} apply={applyWhite}/>}
            {colorBand.session&&<ColorBandPanel tool={colorBand} canLocal={canBrush} local={openLocalColor}/>}
            <div className="canvas-tools"><div className="zoom-group">
              <IconButton label="缩小（−）" icon="minus" disabled={!photo} onClick={()=>setView('out')}/><button className="zoom-number" title="原始尺寸（1）" disabled={!photo} onClick={()=>setView('actual')}>{photo?zoom+'%':'—'}</button><IconButton label="放大（+）" icon="plus" disabled={!photo} onClick={()=>setView('in')}/>
              <i className="tool-rule"/><button className="canvas-tool-button fit-button" title="适应画布（0）" disabled={!photo} onClick={()=>setView('fit')}><Icon name="fit" size={15}/><span>适应</span></button></div>
              <div className="canvas-mid"><button className={'canvas-tool-button compare-toggle '+(compare?'on':'')} disabled={!photo||picking||neutralPick||frameEditing} aria-pressed={compare&&!neutralPick&&!frameEditing} title="拖动分界线查看原片与调整（C）" onClick={()=>setCompare(v=>!v)}><Icon name="compare" size={15}/><span>前后对比</span></button>
                {capabilities.whiteBalance===true&&<IconButton label="白平衡吸管" icon="target" active={!!white.session} disabled={!doc||working||neutralPick||frameEditing} onClick={()=>openSampler('white')}/>}
                {capabilities.colorBandPicking===true&&<button className="canvas-tool-button" aria-label="点照片调色" aria-pressed={!!colorBand.session}
                  title="点照片里的颜色，定位全图通道或创建局部范围" disabled={!doc||working||neutralPick||frameEditing} onClick={()=>openSampler('band')}><Icon name="sliders" size={15}/><span>点图调色</span></button>}</div>
              <div className="photo-nav"><IconButton label="上一张照片" icon="left" size={13} disabled={working||photoIndex<1} onClick={()=>navigate(-1)}/><span>{photoIndex<0?'—':photoIndex+1} / {photoCount}</span><IconButton label="下一张照片" icon="right" size={13} disabled={working||photoIndex<0||photoIndex>=photoCount-1} onClick={()=>navigate(1)}/></div>
            </div>
            <Filmstrip photos={visible} selected={state?.selectedId||null} choose={p=>void select(p)} files={()=>void addPhotos('importFiles')} busy={working} open={stripOpen} toggle={()=>setStripOpen(v=>!v)}/>
          </>}
      </main>
      {mode==='editor' && rightOpen && <aside className="inspector" aria-label="照片编辑面板">
        <div className="panel-title"><span>编辑面板</span>{doc&&<button type="button" className="adjusted-toggle" aria-pressed={showAdjusted}
          onClick={()=>setAdjustedPhoto(showAdjusted?'':doc.photoId)}><Icon name="sliders" size={12}/>只看已调整</button>}
          <IconButton label="收起右侧面板" icon="collapseR" onClick={()=>setRightOpen(false)}/></div>
        <div className="inspector-tabs" aria-label="编辑工具"><button className={!showAdjusted&&inspector==='manual'?'active':''} onClick={()=>showInspector('manual')}><Icon name="sliders" size={14}/>参数</button>
          <button className={!showAdjusted&&inspector==='ai'?'active':''} onClick={()=>showInspector('ai')}><Icon name="sparkle" size={14}/>AI 助手</button>
          {(canBrush||canFace||canSegment)&&<button className={!showAdjusted&&inspector==='local'?'active':''} onClick={()=>{showInspector('local');setCompare(false);setPeek(false);abandonTransient();}}><Icon name="brush" size={14}/>局部</button>}
          <button className={!showAdjusted&&inspector==='history'?'active':''} onClick={()=>showInspector('history')}><Icon name="history" size={14}/>历史</button></div>
        {doc&&storedDrafts.recovered.filter(key=>key.startsWith(doc.photoId+'/')).map(key=>{
          const label=draftLabel(key.slice(doc.photoId.length+1));
          return <div className="recovered-draft" key={key}><span>上次的{label}草稿</span>
            <button disabled={working} aria-label={'继续'+label+'草稿'} onClick={()=>handleRecovered(key,true)}>继续</button>
            <button disabled={working} aria-label={'放弃'+label+'草稿'} onClick={()=>handleRecovered(key,false)}>放弃</button></div>;
        })}
        {showAdjusted&&doc&&state&&<AdjustedPanel doc={doc} controls={state.controls} busy={working} open={locateAdjustment}/>}
        <div className="inspector-tool-content" hidden={showAdjusted}>
        {((inspector==='local'&&faceMode)||inspector==='body')&&subjects.active&&<button className="secondary small" disabled={working} onClick={()=>openParamAction('person')}>只还原所选人物美颜</button>}
        {areaTool?<div className="inspector-scroll"><AreaPanel tool={areaTool} editor={areaEditor} busy={working} close={()=>showInspector(areaTool==='privacy'?'manual':'body')} save={()=>void saveAreas()}
          faces={privacyFaces} detect={canFace?()=>void local.analyze():undefined} detecting={local.detecting} faceError={local.faceError}/></div>:inspector==='manual'?<>
          <div className="inspector-scroll">{frameEditing&&frameDraft&&photo&&doc?<FramingPanel photo={photo} values={frameDraft} saved={doc.parameters} locks={doc.locks} preview={frameResult} busy={working} visit={currentVisit}
            tool={frameTool} incomplete={frameIncomplete} choose={tool=>{setFrameTool(tool);setFrameResult(false);}}
            change={crop.change} toggle={()=>setFrameResult(!frameResult)} cancel={()=>{crop.discard(doc.photoId);crop.setOpen(false);}} info={key=>setParam(state?.controls.find(c=>c.key===key)??null)}
            apply={async()=>{if(frameDelta&&await submitValues(frameDelta,'裁切与校正')){crop.discard(doc.photoId);crop.setOpen(false);setView('fit');}}}/>:
            <><Histogram frame={edited.image} empty={!photo}/>{photo&&<div className="clip-controls" aria-label="画布曝光提示">
              <button aria-pressed={!!(clipping&1)} title="用红色标出成品中任一 sRGB 通道达到上限的区域" onClick={()=>setClipping(v=>v^1)}>高光溢出</button>
              <button aria-pressed={!!(clipping&2)} title="用蓝色标出成品中三个 sRGB 通道均为零的区域" onClick={()=>setClipping(v=>v^2)}>暗部剪切</button>
              {!!clipping&&<small>成品 sRGB</small>}
            </div>}{canFrame&&photo&&doc&&<button className="frame-open" disabled={working} aria-label={crop.hasDraft?'继续裁切草稿':'裁切与校正'} onClick={()=>{abandonTransient();setCompare(false);setPeek(false);setFrameResult(false);crop.setOpen(true);setView('fit');}}>
              <Icon name="fit" size={15}/>{crop.hasDraft?'继续裁切草稿':'裁切与校正'}<span>{committedFrame.active?committedFrame.width+' × '+committedFrame.height:''}</span><Icon name="right" size={13}/></button>}
              {photo&&capabilities.areaTools===true&&<button className="frame-open" disabled={working} onClick={()=>openArea('privacy')}><Icon name="lock" size={15}/>隐私遮挡<Icon name="right" size={13}/></button>}{parameters(manualControls)}</>}</div>
          <div className="inspector-foot"><button onClick={()=>setDialog('recipe')} disabled={!doc||working}><Icon name="stack" size={15}/>编辑配方<Icon name="right" size={12}/></button></div>
        </>:inspector==='body'?<div className="inspector-scroll">
          <div className="body-heading"><h2 className="body-title">人体美型</h2>
            {subjects.visible.length>0&&<label><input type="checkbox" checked={showPersonBoxes}
              onChange={e=>setShowPersonBoxes(e.target.checked)}/>显示人物框</label>}
          </div>
          {photo&&doc?<>{capabilities.areaTools===true&&<button className="frame-open" disabled={working} onClick={()=>openArea('guard')}><Icon name="lock" size={15}/>文字与直线保护<Icon name="right" size={13}/></button>}
            <BodyPanel key={photo.id} photoId={photo.id} revision={doc.revision}
            people={bodyPeople} selected={bodyPerson?.index} disabled={working} searching={!!search.active} onSaved={adoptBody} onAuto={autoBody}/>
            {adjustment?.scope==='人体美型'&&<ProposalReview body plan={adjustment} controls={activeControls} doc={doc} busy={working}
              previewReady={edited.ready&&!edited.error} toggleGroup={toggleProposalGroup} accept={()=>void acceptPlan()} cancel={cancelPlan}/>}
            {!!bodyPerson?.available.length&&parameters(activeControls.filter(c=>c.group==='人体美型'))}
          </>:<p className="local-hint">导入照片后，在这里识别并选择人物。</p>}
        </div>:
        inspector==='history'?<div className="inspector-scroll"><HistoryPanel doc={doc} busy={working} restore={index=>void docAction('restore',{index})}/></div>:inspector==='local'?<div className="inspector-scroll">
          <LocalPanel local={local} photo={photo} people={bodyPeople} personScene={bodyScene} busy={working} canBrush={canBrush} canFace={canFace} canSegment={canSegment} warpedHeal={warpedHeal} previewReady={edited.ready&&!edited.error}
            replacingBackground={!!doc?.parameters[finishPrefix+'backgroundOutput']}
            portraitPanel={state?.portraitLandmarks&&photo&&doc?<PortraitPanel key={photo.id} id={photo.id} revision={doc.revision}
              people={portraitPeople} selected={facePerson?.index} disabled={working} searching={!!search.active} onSaved={adoptBody}
              boxes={showFaceBoxes} showBoxes={setShowFaceBoxes}/>:undefined}
            lightControls={parameters(activeControls.filter(c=>maskLightKeys.includes(c.key)),false,true)}
            segment={<SegmentPanel editor={segment} enabled={!!doc&&!working} stored={segmentSaved} linkAvailable={canLink}
              onBackdrop={async(cutout)=>{const next=await docAction('maskManage',{operation:'add',kind:cutout?'segmentCutout':'segmentBackdrop',maskSlot:0});
                if(next?.maskSlot){local.setSection('brush');local.chooseMask(next.maskSlot);local.setTool('paint');local.setVisible(false);}}}
              onStart={()=>{abandonTransient();setCompare(false);setPeek(false);segment.start();}}
              onSave={()=>void segment.save(async(id,revision,segmentToken)=>!!await mutate('segmentSave',{id,revision,segmentToken}))}/>}
            onManage={async data=>{const next=await docAction('maskManage',data);return next?.maskSlot??null;}}
            onSave={()=>void local.save(async(id,revision,strokes,maskSlot)=>{const next=await mutate('localSave',{id,revision,strokes,maskSlot});return next?.document?.revision??null;})}
            onFace={index=>void local.selectFace(index,async(id,revision,index)=>{const next=await mutate('selectFace',{id,revision,index});return next?.document?.revision??null;})}>
            {faceLightOnly?<section className="local-section"><b>只提亮选中的人脸</b>
              {facePerson?parameters(regionalControls.filter(c=>c.key==='portrait_p'+(facePerson.index+1)+'_light'),false,true):
                <p>检测人脸后，点选要提亮的人。</p>}
              <button className="text-button" onClick={()=>setFaceSearchPhoto('')}>查看全部人像参数</button></section>:
              regionalControls.length?parameters(regionalControls,segmentMode):photo&&!segmentMode&&!lightTool&&!faceMode&&<p className="local-hint">先在画布上标记要调整的区域。</p>}
            {faceMode&&faceSelected&&subjects.all.length>0&&<details className="portrait-legacy"><summary>旧版人脸框 · 肤质</summary>
              <p className="local-hint">这三项作用于旧配方记录的人脸框。</p>{parameters(editableControls.filter(c=>c.key.startsWith('portrait_skin_')))}</details>}
          </LocalPanel></div>:
          <><PeopleChoices view="ai" disabled={working} manage/>
          <AiPanel controls={aiControls} doc={doc} chat={doc?(chat[doc.photoId]||[]):[]} pending={pending} generating={generating} busy={working} provider={sourceName}
            previewReady={edited.ready&&!edited.error} toggleGroup={toggleProposalGroup}
           configure={capabilities.providerSettings===true?()=>void configure():undefined} send={(text,scope)=>void plan(text,scope)} accept={()=>void acceptPlan()} cancel={cancelPlan}
           presetName={proposedName} setPresetName={setProposedName} duplicatePreset={duplicateName} confirmPreset={()=>void confirmPreset()}/></>}
        </div>
      </aside>}
    </div>
    <footer className="status-bar"><span className="save-state"><i className={'live-dot '+(busy||areaStatus||white.values||draft||pending||chosen.length||crop.hasDraft||local.gradient||local.range||local.category?.alpha||local.dirty||segment.state.phase==='ready'?'busy':'')}/>{busy || areaStatus || (startup?'工作区未连接':!state?'正在连接':white.values?'白平衡待应用':draft?'正在预览调整':pending?'提案待确认':chosen.length?'预设待应用':crop.hasDraft?'构图待应用':local.gradient?'渐变待应用':local.range?'范围待应用':local.category?.alpha?'蒙版选区待应用':segment.state.phase==='ready'?'分割待应用':local.dirty?'局部笔迹待保存':doc?'已自动保存':'本地工作区')}</span><span className="status-rule"/>
      {photo&&<button title="查看照片信息" className="photo-meta-button" onClick={()=>setDialog('info')}><Icon name="info" size={12}/>{formatOf(photo)} · {photo.width} × {photo.height}{photo.cameraModel?' · '+photo.cameraModel:''}</button>}
      <span className="spacer"/>{state?.renderMode&&<label className="render-mode" title="自动模式在支持的显卡上处理大图曝光、白平衡和曲线"><span>渲染</span>
        <select aria-label="渲染方式" disabled={working} value={state.renderMode} onChange={e=>void mutate('setRenderMode',{mode:e.target.value},'正在保存渲染方式')}>
          <option value="auto">自动</option><option value="cpu">仅 CPU</option>
        </select></label>}<button className="keyboard-entry" onClick={()=>setDialog('keys')}><Icon name="keyboard" size={13}/>快捷键</button>
      {!!channelLabel&&<><span className="status-rule"/><span title={state?.version}>{channelLabel}</span></>}
    </footer>
    {notice&&<div className={'toast '+(notice.error?'error':'')} role={notice.error?'alert':'status'}><Icon name={notice.error?'info':'checkCircle'} size={16}/><span>{notice.text}</span>
      {notice.restore&&<button className="restore-photo" disabled={working} onClick={async()=>{if(notice.restore&&await mutate('restorePhoto',notice.restore,'正在恢复图库照片'))tell('照片已恢复到图库。');}}>撤销移除</button>}
      <IconButton label="关闭提示" size={13} icon="close" onClick={()=>setNotice(null)}/></div>}
    {dialog==='keys'&&<ShortcutDialog close={()=>setDialog(null)}/>}
    {dialog==='libraryStorage'&&state&&<LibraryStorage state={state} close={()=>setDialog(null)}/>}
    {dialog==='copies'&&<CopyCompare photos={visible} initial={state?.selectedId??null} close={()=>setDialog(null)} choose={p=>{setDialog(null);void select(p);}}/>}
    {capabilities.capture===true&&<CaptureIntake open={dialog==='capture'} close={()=>setDialog(null)} adopt={adopt} watching={setWatchingCapture}/>}
    {dialog==='sets'&&<SetReviewDialog photos={baseVisible} initialScope={gridExposure.scope} selected={state?.selectedId||null} close={()=>setDialog(null)} saved={adopt}/>}
    {dialog==='relink'&&<RelinkDialog close={()=>setDialog(null)} saved={next=>{
      adopt(next);setDialog(null);setRelinkGeneration(n=>n+1);
      if(filter.kind==='folder'&&!filterPhotos(next.catalog,filter).length)setFilter({kind:'all'});
      tell('已找回 '+next.relinkCount+' 张原片。');
    }}/>}
    {dialog==='projects'&&Array.isArray(state?.projects)&&<ProjectDialog projects={state.projects} photos={catalog} initial={filter.projectId} busy={!!busy} close={()=>setDialog(null)}
      save={async(name,id)=>{const next=id?await mutate('renameProject',{projectId:id,name}):await mutate('createProject',{name});return next?.projects?.find(p=>id?p.id===id:p.name===name);}}
      member={(projectId,id,member)=>void mutate('setProjectPhoto',{projectId,id,member})}/>}
    {dialog==='batch'&&<BatchDialog photos={visible} source={photo} busy={!!busy} options={canFrame} delivery={capabilities.deliveryPresets===true} batchToken={batchToken} close={()=>setDialog(null)} write={(ids,options)=>void doExport(ids,false,options)}
      canDeliver={pendingPrivacy()?checkPrivacyDrafts:undefined}
      apply={capabilities.batchApply===true? async ids=>{if(await docAction('batchApply',{ids})) {setDialog(null);tell('已将参数同步到 '+ids.length+' 张照片。');}}:undefined}/>}
    {dialog==='preset'&&<PresetDialog presets={[...(state?.userPresets||[]),...(state?.presets||[])]} busy={!!busy} close={()=>setDialog(null)}
      save={async name=>{if(photo && await mutate('savePreset',{id:photo.id,name})) {setDialog(null);tell('预设已保存到「我的」。');}}}/>}
    {dialog==='recipe'&&<Modal title="编辑配方" close={busy?noop:()=>setDialog(null)}>
      <div className="action-list"><button onClick={copy} disabled={!!busy}><Icon name="copy"/>复制当前参数</button><button disabled={!!busy||!copied} onClick={()=>openParamAction('paste')}><Icon name="sliders"/>粘贴已复制的参数</button>
        <button disabled={!!busy} onClick={()=>openParamAction('color')}>只还原全图调色</button>
        <button disabled={!!busy||!subjects.active} onClick={()=>openParamAction('person')}>只还原所选人物美颜</button>
        {capabilities.copyRecipe===true&&<button disabled={!!busy||catalog.length<2} onClick={()=>setDialog('copyRecipe')}><Icon name="photos"/>从其他照片复制配方</button>}
        {capabilities.matchColor===true&&<button disabled={!!busy||generating} onClick={()=>void matchColor()}><Icon name="palette"/>参考图仿色</button>}
        <button disabled={!!busy} onClick={()=>setDialog('preset')}><Icon name="save"/>保存为预设</button>
        {capabilities.recipes===true&&<><button disabled={!!busy} onClick={async()=>{if(photo && await mutate('exportRecipe',{id:photo.id},'正在导出配方')) setDialog(null);}}><Icon name="export"/>导出配方文件</button>
        <button disabled={!!busy} onClick={async()=>{if(await docAction('importRecipe'))setDialog(null);}}><Icon name="import"/>导入配方文件</button>
        {doc?.revision===0&&doc.history.length===1&&<button disabled={!!busy} onClick={async()=>{if(await docAction('importRecipe',{mode:'history'}))setDialog(null);}}><Icon name="history"/>导入完整编辑历史</button>}</>}
      </div></Modal>}
    {dialog==='copyRecipe'&&photo&&<CopyRecipeDialog photos={catalog} target={photo} busy={!!busy} close={()=>setDialog(null)} copy={async fromId=>{if(await docAction('copyRecipe',{fromId})){setDialog(null);tell('已复制所选照片的编辑配方。');}}}/>}
    {param&&doc&&<Modal title={param.label} close={busy?noop:()=>setParam(null)}><div className="param-info"><span>当前值</span><b>{displayValue(doc.parameters[param.key]??param.initial,param)}</b></div>
      <div className="action-list">{[
        {label:doc.locks.includes(param.key)?'解除参数锁定':'锁定此参数', icon:'lock' as const, method:'lock', args:{locked:!doc.locks.includes(param.key)}, disabled:false},
        {label:'撤销此项上次调整',icon:'undo' as const,method:'undoParam',args:{},disabled:doc.locks.includes(param.key)||doc.undoableParams?.includes(param.key)===false},
        {label:'重做此项调整',icon:'redo' as const,method:'redoParam',args:{},disabled:doc.locks.includes(param.key)||doc.redoableParams?.includes(param.key)===false},
        {label:'恢复默认值（'+displayValue(param.initial,param)+'）',icon:'reset' as const,method:'resetParam',args:{},disabled:doc.locks.includes(param.key)||doc.parameters[param.key]===param.initial},
      ].map(item=><button key={item.method} disabled={!!busy||item.disabled} onClick={async()=>{if(await docAction(item.method,{key:param.key,...item.args}))setParam(null);}}><Icon name={item.icon}/>{item.label}</button>)}</div></Modal>}
    {paramAction&&state&&doc&&paramAction.id===doc.photoId&&paramAction.revision===doc.revision&&(paramAction.mode!=='person'||paramAction.person===subjects.active?.index)&&
      <ParameterAction mode={paramAction.mode} doc={doc} controls={state.controls} copied={copied} person={subjects.active} busy={working} close={()=>setParamAction(null)}
        apply={(values,label)=>mutate('commit',{id:paramAction.id,revision:paramAction.revision,values,label,source:'manual',requestId:requestId()})}/>}
    {dialog==='removed'&&<RemovedPhotos busy={working} close={()=>setDialog(null)} restore={p=>mutate('restorePhoto',{id:p.id,revision:p.revision})}/>}
    {dialog==='info'&&photo&&<Modal title="照片信息" close={()=>setDialog(null)}><dl className="info-list">
      <dt>文件名</dt><dd translate="no">{photo.name}</dd><dt>格式</dt><dd>{formatOf(photo)}</dd><dt>尺寸</dt><dd>{photo.width} × {photo.height} 像素</dd>
      {photoMetadata(photo).map(([k,v])=><React.Fragment key={k}><dt>{k}</dt><dd>{v}</dd></React.Fragment>)}
      <InputGamut key={photo.id} photo={photo}/>
      {doc&&photo.format==='CR3'&&photo.referenceSampleId===4611&&photo.frameCount===2&&<><dt><label htmlFor="raw-track">RAW 感光轨</label></dt>
        <dd><select id="raw-track" data-focus={currentVisit?.key==='raw_frame'?true:undefined} value={doc.parameters.raw_frame??0} disabled={!!busy||local.dirty||!!local.gradient||!!local.range||!!frameDraft||doc.locks.includes('raw_frame')}
          onChange={event=>void submitValues({raw_frame:Number(event.target.value)},'选择 RAW 感光轨')}>
          <option value={0}>第 1 轨（主轨）</option><option value={1}>第 2 轨</option>
        </select>{local.dirty&&' 请先保存或取消当前蒙版草稿。'}</dd></>}
      <dt>原片位置</dt><dd translate="no">{photo.path}</dd>{!!state?.dataRoot&&<><dt>数据目录</dt><dd>{state.dataRoot}</dd></>}
    </dl><PhotoNote key={photo.id+'@'+photo.revision} photo={photo} busy={working} save={(taken,caption)=>mutate('setPhotoNote',{id:photo.id,revision:photo.revision,taken,caption})}/></Modal>}
    {dialog==='exportSettings'&&photo&&doc&&<ExportDialog key={photo.id+'@'+doc.revision} id={photo.id} revision={doc.revision} name={photo.name} cutout={cutout} width={committedFrame.width} height={committedFrame.height} busy={!!busy} close={()=>setDialog(null)} write={options=>void doExport(undefined,true,options)}/>}
    {dialog==='exportResult'&&result&&<Modal title={result.cancelled?'已停止批量导出':result.errors?.length?'导出结果':'已导出照片'} close={()=>setDialog(null)}>
      <p className="dialog-note">已写入 {result.count} 个文件。</p><div className="output-paths">{(result.paths||[]).map((p,i)=><div key={p}>{p}{result.files?.[i]&&
        <small className="export-file-size">{result.files[i].width} × {result.files[i].height} px · {result.files[i].bytes.toLocaleString()} 字节{result.files[i].quality>0?' · JPEG 质量 '+result.files[i].quality:''}</small>}</div>)}
        {(result.errors||[]).map((e,i)=><div key={i} className="field-error">{messageOf(e)}</div>)}</div><div className="dialog-actions"><button className="primary" onClick={()=>setDialog(null)}>完成</button></div>
    </Modal>}
    {privacyReview&&<Modal title="先处理隐私遮挡" close={()=>setPrivacyReview('')}><p className="dialog-note">「{catalog.find(p=>p.id===privacyReview)?.name??'所选照片'}」还有遮挡草稿。应用遮挡后再导出，就能把它写进成品。</p>
      <div className="dialog-actions"><button className="secondary" onClick={()=>setPrivacyReview('')}>取消导出</button><button className="primary" disabled={working} onClick={()=>void returnToPrivacy()}>返回遮挡工具</button></div></Modal>}
    {dialog==='exportSaved'&&<Modal title="当前预览尚未应用" close={()=>setDialog(null)}><p className="dialog-note">返回工作区确认调整，即可导出当前预览。也可以直接导出最近保存的版本。</p>
      <div className="dialog-actions"><button className="secondary" onClick={()=>{setDialog(null);void doExport(undefined,true);}}>导出已保存版本</button><button className="primary" onClick={()=>setDialog(null)}>返回编辑</button></div></Modal>}
    {languageDialog&&<LanguageDialog initial={preference} startup={languageDialog==='startup'} close={()=>setLanguageDialog(null)} save={chooseLanguage}/>}
  </div></PersonSearchContext.Provider></PeopleContext.Provider>;
}
const root=document.getElementById('root');
if(root) createRoot(root).render(<App/>);
