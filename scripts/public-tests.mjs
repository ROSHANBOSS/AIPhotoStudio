import {execFileSync} from 'node:child_process';
import {dirname, join} from 'node:path';
import {fileURLToPath} from 'node:url';
import {checkTypes} from './check-types.mjs';
const root=dirname(dirname(fileURLToPath(import.meta.url)));
checkTypes();
for(const name of ['ui_recipes.mjs','ui_editing_english.mjs']){
  execFileSync(process.execPath,[join(root,'tests',name)],{cwd:root,stdio:'inherit',windowsHide:true});
}
