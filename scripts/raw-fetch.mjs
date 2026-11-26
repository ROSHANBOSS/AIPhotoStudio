import https from 'node:https';
import {createHash} from 'node:crypto';
import {open} from 'node:fs/promises';
import {fileURLToPath} from 'node:url';
import path from 'node:path';

// raw-stage.py supplies a fresh, empty temporary file inside this project directory.
const root = fileURLToPath(new URL('../build/vendor/raw-downloads/', import.meta.url));
const target = path.resolve(process.argv[2] || '.');
if (path.dirname(target) !== path.resolve(root)) throw new Error('Invalid LibRaw download destination');
const address = 'https://www.libraw.org/data/LibRaw-0.22.3-Win64.zip';
const digest = '4e2213a0b10e32199a30b9114a61376cdb40f94df861262b4a6dca659701276b';
const stream = await new Promise((resolve, reject) => {
  const req = https.get(address, {headers: {'User-Agent': 'AIPhotoStudio-LibRaw-restore/1'}}, res => {
    if (res.statusCode !== 200) { res.resume(); reject(new Error('LibRaw HTTP ' + res.statusCode)); return; }
    resolve(res);
  });
  req.setTimeout(45000, () => req.destroy(new Error('LibRaw download timed out')));
  req.on('error', reject);
});
const fd = await open(target, 'r+');
try {
  if ((await fd.stat()).size !== 0) throw new Error('Expected an empty temporary file');
  const sha = createHash('sha256'); let size = 0;
  for await (const chunk of stream) {
    size += chunk.length;
    if (size > 8 * 1024 * 1024) throw new Error('LibRaw archive exceeds budget');
    sha.update(chunk);
    await fd.writeFile(chunk);
  }
  if (sha.digest('hex') !== digest) throw new Error('LibRaw archive SHA-256 mismatch');
} finally { await fd.close(); }
