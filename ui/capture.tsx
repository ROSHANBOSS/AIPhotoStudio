import React,{useEffect,useRef,useState} from 'react';
import {invoke,messageOf,Snapshot} from './model';
import {Modal} from './widgets';

type Camera={id:string;name:string;shutter:boolean;issue?:string};
type Inbox={folder:string;added:number;imported:number;failed:number;lastFile:string;lastError:string};
export function CaptureIntake({open,close,adopt,watching}:{open:boolean;close:()=>void;adopt:(next:Snapshot)=>void;watching:(on:boolean)=>void}){
  const [inbox,setInbox]=useState<Inbox|null>(null);
  const [cameras,setCameras]=useState<Camera[]|null>(null),[device,setDevice]=useState('');
  const [busy,setBusy]=useState(false),[error,setError]=useState(''),[saved,setSaved]=useState('');
  const active=useRef(false),pendingPoll=useRef<Promise<void>|null>(null),alive=useRef(true),refresh=useRef(adopt);refresh.current=adopt;
  useEffect(()=>{alive.current=true;return ()=>{alive.current=false;};},[]);
  function update(value:Inbox){
    setInbox(old=>old&&old.folder===value.folder&&old.imported===value.imported&&old.failed===value.failed&&old.lastError===value.lastError?old:value);
    watching(!!value.folder);
  }
  useEffect(()=>{
    let live=true,timer:number|undefined;
    async function poll(){
      if(!active.current&&!pendingPoll.current){
        const reading=(async()=>{try{
          const value=await invoke<Inbox>('capturePoll');
          if(live){
            update(value);
            if(value.added){const next=await invoke<Snapshot>('init');if(live)refresh.current(next);}
            setError(previous=>previous.startsWith('拍摄目录暂不可读')?'':previous);
          }
        }catch(e){if(live)setError('拍摄目录暂不可读 · '+messageOf(e));}})();
        pendingPoll.current=reading;
        try{await reading;}finally{pendingPoll.current=null;}
      }
      if(live&&inbox?.folder)timer=window.setTimeout(()=>void poll(),2000);
    }
    void poll();return ()=>{live=false;window.clearTimeout(timer);};
  },[inbox?.folder]);
  async function act(work:()=>Promise<void>){
    if(active.current)return;
    active.current=true;setBusy(true);setError('');
    try{await pendingPoll.current;if(alive.current)await work();}catch(e){if(alive.current)setError(messageOf(e));}
    finally{active.current=false;if(alive.current)setBusy(false);}
  }
  if(!open)return null;
  const current=cameras?.find(c=>c.id===device);
  return <Modal title="拍摄导入" close={close}>
    <div className="capture-options"><h3>监听拍摄目录</h3>
      <p>选择相机软件保存照片的文件夹。之后写完的新照片会自动出现在图库，关闭面板后继续监听。</p>
      {inbox?.folder&&<p className="capture-path">{inbox.folder}<br/>已导入 {inbox.imported} 张{inbox.lastFile?' · '+inbox.lastFile:''}</p>}
      <div className="button-line"><button className="secondary" disabled={busy} onClick={()=>void act(async()=>update(await invoke<Inbox>('captureWatch')))}>选择拍摄目录</button>
        {inbox?.folder&&<button className="secondary" disabled={busy} onClick={()=>void act(async()=>update(await invoke<Inbox>('captureStop')))}>停止监听</button>}</div>
      {inbox?.failed? <p role="status">有 {inbox.failed} 个文件需检查。{inbox.lastError}</p>:null}
      <h3>连接 Windows 相机</h3><p>刷新后选择设备。支持 WIA 拍摄指令的相机可以在这里按快门。</p>
      <div className="button-line"><button className="secondary" disabled={busy} onClick={()=>void act(async()=>{
        const items=await invoke<Camera[]>('captureCameras');setCameras(items);setDevice(items.find(c=>c.id===device)?.id||items[0]?.id||'');
      })}>刷新相机</button>
      {!!cameras?.length&&<select aria-label="相机设备" value={device} onChange={e=>setDevice(e.target.value)}>{cameras.map(c=><option key={c.id} value={c.id}>{c.name}</option>)}</select>}
      {current?.shutter&&<button className="primary" disabled={busy} onClick={()=>void act(async()=>{
        const result=await invoke<{file?:string}>('captureShutter',{deviceId:device});
        if(result.file){setSaved(result.file);refresh.current(await invoke<Snapshot>('init'));}
      })}>拍摄一张并导入</button>}</div>
      {cameras?.length===0&&<p role="status">Windows 当前未列出 WIA 照相机。也可以监听相机软件的拍摄目录。</p>}
      {current?.issue?<p role="status">暂时无法连接这台相机，可重连后刷新。{current.issue}</p>:
        current&&!current.shutter&&<p>该设备仅提供传输功能，可用上方的拍摄目录。</p>}
      {saved&&<p className="capture-path" role="status">已保存到 {saved}</p>}
      {error&&<p className="field-error" role="alert">{error}</p>}
    </div>
  </Modal>;
}
