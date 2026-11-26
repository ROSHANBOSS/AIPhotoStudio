import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import {fileURLToPath} from 'node:url';
const root = fileURLToPath(new URL('../', import.meta.url));
const read = name => JSON.parse(fs.readFileSync(path.join(root,name),'utf8'));
const lock = read('package-lock.json');
const hashes = read('native-dependencies.lock.json');
const jsCache = process.env.AIPHOTO_NPM_SOURCE;
const sum = file => crypto.createHash('sha256').update(fs.readFileSync(file)).digest('hex');

function fingerprint(dir) {
  const result=crypto.createHash('sha256');
  function visit(base,relative) {
    for (const e of fs.readdirSync(base,{withFileTypes:true}).sort((a,b)=>a.name < b.name?-1:a.name > b.name?1:0)) {
      if (e.name==='node_modules') continue;
      const sub=path.join(base,e.name), key=relative+e.name;
      if(e.isDirectory()) visit(sub,key+'/');
      else if(e.isFile()) result.update(key+'\0'+sum(sub)+'\n');
      else throw Error('依赖目录含非常规文件：'+sub);
    }
  }
  visit(dir,'');
  return result.digest('hex');
}
for (const [relative, item] of Object.entries(lock.packages)) {
  if (!relative) continue;
  const dest=path.join(root,relative), source=jsCache && path.join(jsCache,relative.slice('node_modules/'.length));
  if (fs.existsSync(dest) && fingerprint(dest)===hashes.npmTrees[relative]) continue;
  if (!source || !fs.existsSync(source) || fingerprint(source)!==hashes.npmTrees[relative]) throw Error('未找到已核验的离线副本 '+relative+'；可使用项目 npm ci 后重新校验。');
  if (fs.existsSync(dest)) throw Error('项目依赖已有不同内容，请检查后还原：'+relative);
  fs.mkdirSync(path.dirname(dest),{recursive:true});
  fs.cpSync(source,dest,{recursive:true,dereference:true,filter:p=>p===source || path.basename(p)!=='node_modules'});
  if(fingerprint(dest)!==hashes.npmTrees[relative]) throw Error('还原后校验不符：'+relative);
}
const {restoreBodyAssets}=await import('./body-assets.mjs');
const allowNetwork=process.argv.includes('--fetch');
await restoreBodyAssets({root,allowNetwork});
const {execFileSync}=await import('node:child_process');
const python=process.env.AIPHOTO_BUILD_PYTHON || 'python';
for (const name of ['prepare-webview.py','raw-stage.py']) {
  execFileSync(python,['-B',path.join(root,'scripts',name),...(allowNetwork?['--fetch']:[])],{stdio:'inherit',windowsHide:true,env:{...process.env,AIPHOTO_NODE:process.execPath}});
}
console.log('Runtime assets verified.');
