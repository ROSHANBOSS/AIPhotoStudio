import { createHash, randomUUID } from 'node:crypto';
import { mkdir, readFile, rename, rm, writeFile } from 'node:fs/promises';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { gunzipSync } from 'node:zlib';

const project = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const artifacts = {
  categories: {
    url: 'https://storage.googleapis.com/mediapipe-models/image_segmenter/selfie_multiclass_256x256/float32/1/selfie_multiclass_256x256.tflite',
    relative: 'BodyVision/selfie_multiclass_256x256.tflite',
    size: 16371837, sha: 'c6748b1253a99067ef71f7e26ca71096cd449baefa8f101900ea23016507e0e0'
  },
  face: {
    url: 'https://storage.googleapis.com/mediapipe-models/face_landmarker/face_landmarker/float16/1/face_landmarker.task',
    relative: 'BodyVision/face_landmarker.task',
    size: 3758596, sha: '64184e229b263107bc2b804c6625db1341ff2bb731874b0bcc2fe6544e0bc9ff'
  },
  model: {
    url: 'https://storage.googleapis.com/mediapipe-models/pose_landmarker/pose_landmarker_lite/float16/1/pose_landmarker_lite.task',
    relative: 'BodyVision/pose_landmarker_lite.task',
    size: 5777746, sha: '59929e1d1ee95287735ddd833b19cf4ac46d29bc7afddbbf6753c459690d574a'
  },
  runtime: {
    url: 'https://registry.npmjs.org/@mediapipe/tasks-vision/-/tasks-vision-0.10.21.tgz',
    relative: 'body-downloads/tasks-vision-0.10.21.tgz',
    size: 6164925, sha: '343736570f433c0100ffc360dbe70f09cc502a5fc573f5cd95954692edc9cfde'
  },

};

const runtimeFiles = [
  'vision_bundle.mjs', 'wasm/vision_wasm_internal.js', 'wasm/vision_wasm_internal.wasm',
  'wasm/vision_wasm_nosimd_internal.js', 'wasm/vision_wasm_nosimd_internal.wasm'
];
const digest = data => createHash('sha256').update(data).digest('hex');

async function writeChanged(path, bytes) {
  await mkdir(dirname(path), { recursive: true });
  try { if ((await readFile(path)).equals(bytes)) return; }
  catch (error) { if (error.code !== 'ENOENT') throw error; }
  const temporary = join(dirname(path), '.body-' + randomUUID() + '.tmp');
  try { await writeFile(temporary, bytes, { flag: 'wx' }); await rename(temporary, path); }
  finally { await rm(temporary, { force: true }); }
}

async function acquire(vendor, item, allowNetwork) {
  const path = join(vendor, item.relative);
  let bytes;
  try { bytes = await readFile(path); }
  catch (error) {
    if (error.code !== 'ENOENT') throw error;
    if (!allowNetwork) throw new Error('Missing pinned local asset: ' + item.relative);
    const response = await fetch(item.url, { redirect: 'error', signal: AbortSignal.timeout(90000),
      headers: { 'User-Agent': 'AIPhotoStudio/BodyAssets (offline Windows photo editor)' } });
    if (!response.ok) throw new Error('Asset download failed: HTTP ' + response.status);
    bytes = Buffer.from(await response.arrayBuffer());
  }
  if (bytes.length !== item.size || digest(bytes) !== item.sha)
    throw new Error('Pinned asset hash/length mismatch: ' + item.relative);
  await writeChanged(path, bytes);
  return bytes;
}

// Only copy explicitly named regular files. No archive-supplied path is used as a destination.
function extractRuntime(tgz) {
  const tar = gunzipSync(tgz, { maxOutputLength: 60 * 1024 * 1024 });
  const wanted = new Set(runtimeFiles.map(name => 'package/' + name));
  const found = new Map();
  for (let offset = 0; offset + 512 <= tar.length;) {
    const header = tar.subarray(offset, offset + 512);
    if (header.every(x => x === 0)) break;
    const ascii = (a, b) => header.toString('utf8', a, b).split('\0')[0].trim();
    const name = ascii(0, 100);
    const size = parseInt(ascii(124, 136), 8);
    if (!Number.isSafeInteger(size) || size < 0 || offset + 512 + size > tar.length) throw new Error('Invalid npm tar entry.');
    if ((header[156] === 0 || header[156] === 48) && wanted.has(name)) {
      if (found.has(name)) throw new Error('Duplicate npm tar entry: ' + name);
      found.set(name, tar.subarray(offset + 512, offset + 512 + size));
    }
    offset += 512 + Math.ceil(size / 512) * 512;
  }
  for (const name of wanted) if (!found.has(name)) throw new Error('Missing upstream runtime file: ' + name);
  return found;
}

export async function restoreBodyAssets({ root = project, allowNetwork = false } = {}) {
  const vendor = join(root, 'build', 'vendor');
  const categories = await acquire(vendor, artifacts.categories, allowNetwork);
  const model = await acquire(vendor, artifacts.model, allowNetwork);
  const face = await acquire(vendor, artifacts.face, allowNetwork);
  const tgz = await acquire(vendor, artifacts.runtime, allowNetwork);
  const entries = extractRuntime(tgz);
  const files = [{ name: 'pose_landmarker_lite.task', bytes: model.length, sha256: digest(model) },
    { name: 'face_landmarker.task', bytes: face.length, sha256: digest(face) }];
  for (const relative of runtimeFiles) {
    const bytes = entries.get('package/' + relative);
    await writeChanged(join(vendor, 'BodyVision', relative), bytes);
    files.push({ name: relative, bytes: bytes.length, sha256: digest(bytes) });
  }
  files.push({ name: 'selfie_multiclass_256x256.tflite', bytes: categories.length, sha256: digest(categories) });
  return { runtimeVersion: '0.10.21', modelVersion: 'pose_landmarker_lite/float16/1', files };
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  const result = await restoreBodyAssets({ allowNetwork: process.argv.includes('--fetch') });
  console.log(JSON.stringify(result, null, 2));
}
