import path from 'node:path';
import {root, run} from './build.mjs';
export function checkTypes() {
  const tsc = path.join(root, 'node_modules', 'typescript', 'bin', 'tsc');
  run(process.execPath, [tsc, '-p', path.join(root, 'ui', 'tsconfig.json'), '--pretty', 'false']);
}
