"""Stage the three pinned SDK files from a user-supplied folder or the official NuGet archive."""
from pathlib import Path
import argparse, hashlib, json, os, shutil, tempfile, urllib.request, zipfile
ROOT=Path(__file__).resolve().parents[1]
def main():
    cli=argparse.ArgumentParser()
    cli.add_argument('--fetch',action='store_true')
    fetch=cli.parse_args().fetch
    sdk=json.loads((ROOT/'native-dependencies.lock.json').read_text(encoding='utf8'))['sdk']
    out=ROOT/'build/vendor/WebView2'
    checksum=lambda data:hashlib.sha256(data).hexdigest()
    if all((out/k).is_file() and checksum((out/k).read_bytes())==h for k,h in sdk['files'].items()):return
    supplied=os.environ.get('AIPHOTO_WEBVIEW_SDK')
    with tempfile.TemporaryDirectory() as temp:
        archive=None
        try:
            if not supplied:
                if not fetch:raise SystemExit('Supply AIPHOTO_WEBVIEW_SDK or run npm run bootstrap -- --fetch')
                v=sdk['version']
                url=f'https://api.nuget.org/v3-flatcontainer/microsoft.web.webview2/{v}/microsoft.web.webview2.{v}.nupkg'
                pkg=Path(temp)/'sdk.nupkg'
                with urllib.request.urlopen(url,timeout=60) as response,pkg.open('wb') as f:shutil.copyfileobj(response,f)
                archive=zipfile.ZipFile(pkg)
            staged={}
            for rel,expected in sdk['files'].items():
                names=[rel, 'lib/net462/'+rel, 'lib/net45/'+rel]
                for name in names:
                    if supplied:
                        p=Path(supplied)/name
                        if not p.is_file():continue
                        data=p.read_bytes()
                    else:
                        if name not in archive.namelist():continue
                        data=archive.read(name)
                    if checksum(data)==expected:
                        staged[rel]=data;break
                else:raise ValueError('Missing or mismatched WebView2 component: '+rel)
            for rel,data in staged.items():
                p=out/rel;p.parent.mkdir(parents=True,exist_ok=True);p.write_bytes(data)
        finally:
            if archive:archive.close()
if __name__=='__main__':main()
