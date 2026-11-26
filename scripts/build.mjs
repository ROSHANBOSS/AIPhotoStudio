import fs from 'node:fs';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
import {spawnSync} from 'node:child_process';
import crypto from 'node:crypto';

export const root=fileURLToPath(new URL('../',import.meta.url));
const at=(...segments)=>path.join(root,...segments);
export const version=JSON.parse(fs.readFileSync(at('package.json'),'utf8')).version;
export const stableDirectory=at('dist','stable-'+version);
export const setupFilename='AIPhotoStudio-'+version+'-Setup-x64.exe';
const fw=path.join(process.env.WINDIR,'Microsoft.NET','Framework64','v4.0.30319');
const sdk=at('build','vendor','WebView2');
export function run(bin,args,options={}) {
  const r=spawnSync(bin,args,{cwd:root,stdio:'inherit',windowsHide:true,...options});
  if(r.error)throw r.error;
  if(r.status!==0)throw Error(path.basename(bin)+' 退出码 '+r.status);
}
function copy(src,dst){fs.mkdirSync(path.dirname(dst),{recursive:true});fs.copyFileSync(src,dst);}
function digest(file){return crypto.createHash('sha256').update(fs.readFileSync(file)).digest('hex');}

function filesIn(folder) {
  return fs.readdirSync(folder,{withFileTypes:true}).flatMap(entry=>{
    const item=path.join(folder,entry.name);
    return entry.isDirectory()?filesIn(item):[item];
  });
}
export function releaseFingerprint() {
  const inputs=['native','ui','installer','licenses'].flatMap(dir=>filesIn(at(dir)));
  for(const name of ['body-assets.mjs','raw-stage.py','prepare-webview.py'])inputs.push(at('scripts',name));
  for(const name of ['package.json','package-lock.json','native-dependencies.lock.json','scripts/build.mjs','scripts/bootstrap.mjs','docs/dependencies.md','LICENSE'])inputs.push(at(name));
  const hash=crypto.createHash('sha256');
  for(const file of inputs.sort())hash.update(path.relative(root,file).split(path.sep).join('/')+'\0'+digest(file)+'\n');
  return hash.digest('hex');
}
export function compile(output, main, sources, flags=[]) {
  const refs=['System.dll','System.Core.dll','System.Data.dll','System.Drawing.dll','System.Windows.Forms.dll',
    'System.Web.Extensions.dll','System.Security.dll','System.Net.Http.dll','System.Xaml.dll','System.IO.Compression.dll','System.IO.Compression.FileSystem.dll',
    'WPF/WindowsBase.dll','WPF/PresentationFramework.dll','WPF/PresentationCore.dll'].map(s=>'/r:'+path.join(fw,s));
  refs.push('/r:'+path.join(sdk,'Microsoft.Web.WebView2.Core.dll'),'/r:'+path.join(sdk,'Microsoft.Web.WebView2.WinForms.dll'));
  fs.mkdirSync(path.dirname(output),{recursive:true});
  run(path.join(fw,'csc.exe'),['/nologo','/optimize+','/platform:x64','/utf8output','/codepage:65001','/main:'+main,'/out:'+output,...refs,...flags,...sources]);
  if (main !== 'Setup') copy(at('native','application.config'), output + '.config');
}
export function sources(dir){return fs.readdirSync(at(dir),{withFileTypes:true}).filter(x=>x.isFile()&&x.name.endsWith('.cs')).map(x=>at(dir,x.name)).sort();}
export function resources(output){
  copy(at('ui','i18n-native-messages.json'),path.join(output,'ui','i18n-native-messages.json'));
  const body=at('build','vendor','BodyVision');
  copy(path.join(body,'selfie_multiclass_256x256.tflite'),path.join(output,'ui','BodyVision','selfie_multiclass_256x256.tflite'));
  copy(at('licenses','MaskVision-notice.txt'),path.join(output,'Notices','MaskVision-notice.txt'));
  const raw=at('build','vendor','Raw');
  for(const name of ['CameraRawWorker.exe','libraw.dll','COPYRIGHT','LICENSE.CDDL','LICENSE.LGPL','LibRaw-0.22.3-Win64.zip'])
    copy(path.join(raw,name),path.join(output,'Raw',name));
  if(fs.existsSync(at('docs','imaging-raw-license.md')))copy(at('docs','imaging-raw-license.md'),path.join(output,'Raw','NOTICE.md'));
  copy(at('licenses','MediaPipe-Apache-2.0.txt'),path.join(output,'Notices','MediaPipe-Apache-2.0.txt'));
  copy(at('licenses','BodyVision-notice.txt'),path.join(output,'Notices','BodyVision-notice.txt'));
  copy(at('licenses','PortraitVision-notice.txt'),path.join(output,'Notices','PortraitVision-notice.txt'));
  for(const name of ['vision_bundle.mjs','pose_landmarker_lite.task','face_landmarker.task','wasm/vision_wasm_internal.js','wasm/vision_wasm_internal.wasm','wasm/vision_wasm_nosimd_internal.js','wasm/vision_wasm_nosimd_internal.wasm'])
    copy(path.join(body,name),path.join(output,'ui','BodyVision',name));
  for(const name of Object.keys(JSON.parse(fs.readFileSync(at('native-dependencies.lock.json'))).sdk.files))copy(path.join(sdk,name),path.join(output,name));
  copy(at('examples','Stillwater.png'),path.join(output,'Samples','Stillwater.png'));
  
  for(const name of ['LICENSE','LICENSING.md','THIRD_PARTY_NOTICES.md'])copy(at(name),path.join(output,'Notices',name));
  for(const name of fs.readdirSync(at('licenses')))copy(at('licenses',name),path.join(output,'Notices','licenses',name));
  for(const pkg of ['react','react-dom','scheduler'])copy(at('node_modules',pkg,'LICENSE'),path.join(output,'Notices',pkg+'-LICENSE.txt'));
  for(const file of ['WebView2-notice.txt'])copy(at('licenses',file),path.join(output,'Notices',file));
}
export function build(channel,candidate) {
  if(!['development','stable'].includes(channel))throw Error('未知通道');
  if(channel==='stable' && version.includes('-'))throw Error('开发版本不能创建稳定版。');
  const dest=path.resolve(candidate||(channel==='stable'?stableDirectory:at('build','development')));
  const relative=path.relative(at('dist'),dest);
  if(relative==='')throw Error('请为程序指定单独的版本目录。');
  const inDistribution=relative!=='..'&&!relative.startsWith('..'+path.sep)&&!path.isAbsolute(relative);
  const retained=channel==='stable'||inDistribution;
  if(retained && fs.existsSync(path.join(dest,'AIPhotoStudio.exe')))throw Error('此版本已保留，请构建到新的版本目录。');
  const out=retained?at('build','package-'+crypto.randomUUID()):dest;
  fs.mkdirSync(path.join(out,'ui'),{recursive:true});resources(out);
  run(at('node_modules','@esbuild','win32-x64','esbuild.exe'),[at('ui','app.tsx'),'--bundle','--jsx-factory=studioElement','--inject:'+at('ui','locale.ts'),'--minify','--legal-comments=eof','--target=chrome112','--define:process.env.NODE_ENV="production"','--outfile='+path.join(out,'ui','app.js')]);
  for(const name of ['index.html','styles.css'])copy(at('ui',name),path.join(out,'ui',name));
  compile(path.join(out,'AIPhotoStudio.exe'),'AIPhotoStudio.Program',sources('native'),['/target:winexe','/win32manifest:'+at('native','app.manifest'),...(channel==='stable'?['/define:STABLE']:[])]);
  if(channel==='stable'){
    const files=Object.fromEntries(filesIn(out).map(file=>[path.relative(out,file).split(path.sep).join('/'),digest(file)]));
    fs.writeFileSync(path.join(out,'release.json'),JSON.stringify({version,source:releaseFingerprint(),files},null,2));
  }
  if(out!==dest){fs.mkdirSync(path.dirname(dest),{recursive:true});fs.renameSync(out,dest);}
  return dest;
}
export function installer(stable,setup,channel='stable'){
  if(!['development','stable'].includes(channel) || (channel==='stable')===version.includes('-'))
    throw Error('安装包通道与版本号不符。');
  const archive=at('build','installer-'+crypto.randomUUID()+'.zip');
  const env={...process.env,AIPHOTO_PACKAGE_DIR:stable,AIPHOTO_PACKAGE_ZIP:archive};
  run(path.join(process.env.WINDIR,'System32','WindowsPowerShell','v1.0','powershell.exe'),['-NoProfile','-NoLogo','-NonInteractive','-Command',
     "$ErrorActionPreference='Stop'; Add-Type -AssemblyName System.IO.Compression.FileSystem; [IO.Compression.ZipFile]::CreateFromDirectory($env:AIPHOTO_PACKAGE_DIR,$env:AIPHOTO_PACKAGE_ZIP)"],{env});
  try {
    compile(setup,'Setup',[at('installer','Setup.cs'),at('native','Assembly.cs')],['/target:winexe','/win32manifest:'+at('native','app.manifest'),'/resource:'+archive+',application.zip',...(channel==='development'?['/define:DEV_SETUP']:[])]);
  } finally { fs.unlinkSync(archive); }
  console.log('安装包',setup,fs.statSync(setup).size,'bytes, SHA-256',digest(setup));return setup;
}
if(process.argv[1]&&path.resolve(process.argv[1])===fileURLToPath(import.meta.url))build('development');
