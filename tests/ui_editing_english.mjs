import assert from 'node:assert/strict';
import {execFileSync} from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
import ts from 'typescript';

const root=fileURLToPath(new URL('../',import.meta.url));
const compiler=path.join(root,'node_modules','@esbuild','win32-x64','esbuild.exe');
const bundle=execFileSync(compiler,['ui/i18n-editing.ts','--bundle','--platform=node','--format=esm','--charset=utf8'],{cwd:root});
const {strings,patterns}=await import('data:text/javascript;base64,'+bundle.toString('base64'));
function english(source){
  if(Object.hasOwn(strings,source))return strings[source];
  for(const [regex,convert] of patterns){
    const match=regex.exec(source);
    if(match)return convert(...match.slice(1));
  }
  return source;
}
// Use the actual source labels for fixed controls and captions so a new panel
// string cannot silently disappear from the English editor.
const components=['advanced','adjusted','parameter-actions','widgets','white-balance','color-band','input-gamut','frame-marks','frame-page',
  'framing','framing-panel','detail-view','preview','transparency','people','person-search','body-panel','body-detector','portrait','portrait-keep',
  'portrait-spots','portrait-detector','area-tools','brush-edge','local','gradient','mask-list','range-mask','mask-detector','segment','maskCoverage'];
const labels=new Set();
for(const name of components){
  const file=['tsx','ts'].map(ext=>path.join(root,'ui',name+'.'+ext)).find(fs.existsSync);
  const code=ts.createSourceFile(file,fs.readFileSync(file,'utf8'),ts.ScriptTarget.Latest,true,file.endsWith('tsx')?ts.ScriptKind.TSX:ts.ScriptKind.TS);
  function walk(node){
    if((ts.isStringLiteralLike(node)||ts.isJsxText(node))&&/[\u3400-\u9fff]/.test(node.text))labels.add(node.text.trim());
    ts.forEachChild(node,walk);
  }
  walk(code);
}
for(const sample of labels)assert.doesNotMatch(english(sample),/[\u3400-\u9fff]/,sample);
assert.equal(english('人脸 12 · 淡化黑眼圈数值'),'Face 12 · Reduce under-eye circles value');
assert.equal(english('红色 · 色相（°）选项'),'Red · Hue (°) options');
assert.equal(english('人物 2 · 收腰 +4 → 0，人物 2 · 收肩 -1 → +2'),'Person 2 · Narrow waist +4 → 0, Person 2 · Narrow shoulders -1 → +2');
assert.equal(english('全图绿色通道 · 此点影响 53%'),'Green channel · 53% influence at this point');
assert.equal(english('已保存 · 1 笔'),'Saved · 1 stroke');
assert.equal(english('衣物名称由用户决定'),'衣物名称由用户决定');

// Optionally inspect every generated parameter name from a built Windows app,
// including late face/body slots, curves, HSL, calibration and local masks.
let nativeCount=0;
if(process.argv[2]){
  const file=path.resolve(process.argv[2]);
  const command=`
    $ProgressPreference='SilentlyContinue'
    [Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
    $assembly=[Reflection.Assembly]::LoadFrom($env:AIPHOTO_ENGLISH_TEST_APP)
    $items=$assembly.GetType('AIPhotoStudio.Imaging').GetField('Controls').GetValue($null)
    @($items | Select-Object label,group) | ConvertTo-Json -Compress
  `;
  const output=execFileSync('powershell.exe',['-NoProfile','-NonInteractive','-EncodedCommand',Buffer.from(command,'utf16le').toString('base64')],
    {encoding:'utf8',windowsHide:true,env:{...process.env,AIPHOTO_ENGLISH_TEST_APP:file}});
  const controls=JSON.parse(output.trim());
  assert.ok(controls.length>300);
  for(const c of controls){
    for(const field of ['group','label'])assert.doesNotMatch(english(c[field]),/[\u3400-\u9fff]/,c.label+' / '+c[field]);
    assert.doesNotMatch(english(c.label+'选项'),/[\u3400-\u9fff]/,c.label+' options');
  }
  nativeCount=controls.length;
}
console.log('English editor verified: '+labels.size+' fixed panel strings, dynamic captions and '+nativeCount+' native controls.');
