"""Download public source for inspection only; never execute repository code."""
import concurrent.futures
import hashlib
import json
from pathlib import Path
import urllib.request
import urllib.parse
import sys

ROOT = Path(__file__).resolve().parents[1] / 'research' / 'deep'
HEADERS = {'User-Agent': 'Barotrauma-Rebound-Research', 'Accept': 'application/vnd.github+json'}

def fetch(url):
    with urllib.request.urlopen(urllib.request.Request(url, headers=HEADERS), timeout=45) as response:
        return response.read()

def tree(repo, branch):
    destination = ROOT / repo.replace('/', '__')
    destination.mkdir(parents=True, exist_ok=True)
    data = json.loads(fetch(f'https://api.github.com/repos/{repo}/git/trees/{branch}?recursive=1'))
    (destination / 'tree.json').write_text(json.dumps(data, indent=2), encoding='utf-8')
    paths = [node['path'] for node in data['tree'] if node['type'] == 'blob']
    (destination / 'paths.txt').write_text('\n'.join(paths), encoding='utf-8')
    return repo, destination, data

def download(repo, sha, path, destination):
    url = f'https://raw.githubusercontent.com/{repo}/{sha}/' + urllib.parse.quote(path, safe='/')
    content = fetch(url)
    output = destination / 'source' / path
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_bytes(content)
    return {'repo': repo, 'commit': sha, 'path': path, 'url': url,
            'sha256': hashlib.sha256(content).hexdigest(), 'bytes': len(content)}

if __name__ == '__main__':
    mode = sys.argv[1]
    if mode == 'trees':
        targets = [('ProjectEP/LuaCsForBarotraumaEP','master'), ('yosu-ovo/ItemOptimizer','main'),
                   ('zhu-rengong/barotrauma-mod-shadowculling','master'), ('FakeFishGames/Barotrauma','master')]
        with concurrent.futures.ThreadPoolExecutor(max_workers=4) as executor:
            for repo, destination, data in executor.map(lambda pair: tree(*pair), targets):
                print(repo, data['sha'], len(data['tree']), 'truncated=',data.get('truncated'))
    elif mode == 'files':
        requests = json.loads((ROOT / 'fetch-list.json').read_text(encoding='utf-8-sig'))
        with concurrent.futures.ThreadPoolExecutor(max_workers=6) as executor:
            futures = []
            for repo, paths in requests.items():
                destination = ROOT / repo.replace('/','__')
                data = json.loads((destination / 'tree.json').read_text(encoding='utf-8'))
                futures += [executor.submit(download,repo,data['sha'],path,destination) for path in paths]
            records = [future.result() for future in futures]
        manifest = ROOT / 'download-manifest.json'
        previous = json.loads(manifest.read_text(encoding='utf-8')) if manifest.exists() else []
        merged = {(item['repo'],item['path']): item for item in previous + records}
        manifest.write_text(json.dumps(list(merged.values()),indent=2),encoding='utf-8')
        print('Downloaded',len(records),'files;',sum(item['bytes'] for item in records),'bytes')
