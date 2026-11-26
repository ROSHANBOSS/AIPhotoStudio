"""Prepare and compile only the project-local Windows x64 camera RAW worker."""
import hashlib
import argparse
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import urllib.request
import zipfile

ROOT = Path(__file__).resolve().parents[1]
NAME = 'LibRaw-0.22.3-Win64.zip'
URL = 'https://www.libraw.org/data/' + NAME
DIGEST = '4e2213a0b10e32199a30b9114a61376cdb40f94df861262b4a6dca659701276b'
DLL_DIGEST = '2c4df36d9011267fd3ffd5c23e80d69d5f42c3d503f03ac02448f5c8a7fe0fa2'
ARCHIVE = ROOT/'build/vendor/raw-downloads'/NAME
STAGE = ROOT/'build/vendor/Raw'


def restore(allow_fetch):
    ARCHIVE.parent.mkdir(parents=True, exist_ok=True)
    if not ARCHIVE.is_file():
        if not allow_fetch: raise FileNotFoundError('Missing locked LibRaw archive. Run python scripts/raw-stage.py --fetch to restore it.')
        with tempfile.NamedTemporaryFile(dir=ARCHIVE.parent,delete=False) as out:
            tmp=Path(out.name)
        try:
            node=os.environ.get('AIPHOTO_NODE') or shutil.which('node')
            if node:
                subprocess.run([node,str(ROOT/'scripts/raw-fetch.mjs'),str(tmp)],check=True,timeout=150)
            else:
                with urllib.request.urlopen(URL,timeout=45) as res, tmp.open('wb') as out:
                    for part in iter(lambda:res.read(131072),b''):
                        out.write(part)
                        if out.tell()>8*1024*1024: raise ValueError('LibRaw archive exceeded download budget')
            if hashlib.sha256(tmp.read_bytes()).hexdigest()!=DIGEST: raise ValueError('Official archive checksum mismatch')
            os.replace(tmp,ARCHIVE)
        finally:
            if tmp.exists(): tmp.unlink()
    if hashlib.sha256(ARCHIVE.read_bytes()).hexdigest()!=DIGEST: raise ValueError('Cached official archive checksum mismatch')


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--fetch',action='store_true',help='Download the locked official archive when absent')
    restore(parser.parse_args().fetch)
    STAGE.mkdir(parents=True,exist_ok=True)
    with zipfile.ZipFile(ARCHIVE) as z:
        base='LibRaw-0.22.3/'
        data=z.read(base+'bin/libraw.dll')
        if hashlib.sha256(data).hexdigest()!=DLL_DIGEST: raise ValueError('Wrong LibRaw DLL')
        (STAGE/'libraw.dll').write_bytes(data)
        for name in ['COPYRIGHT','LICENSE.CDDL','LICENSE.LGPL']:
            (STAGE/name).write_bytes(z.read(base+name))
    # Include the byte-identical source distribution beside the covered executable for the CDDL source requirement.
    shutil.copyfile(ARCHIVE,STAGE/NAME)
    notice=ROOT/'docs/imaging-raw-license.md'
    shutil.copyfile(notice,STAGE/'NOTICE.md')
    fixes=ROOT/'native'
    net=Path(os.environ.get('WINDIR','C:/Windows'))/'Microsoft.NET/Framework64/v4.0.30319'
    compiler=net/'csc.exe'
    subprocess.run([str(compiler),'/nologo','/optimize+','/platform:x64','/target:exe','/define:CAMERA_RAW_WORKER',
                    '/out:'+str(STAGE/'CameraRawWorker.exe'),'/r:'+str(net/'System.Web.Extensions.dll'),
                    str(fixes/'Imaging.RawWorker.cs'),str(fixes/'Imaging.RawModes.cs')],check=True)
    print('RAW runtime',STAGE)


if __name__=='__main__': main()
