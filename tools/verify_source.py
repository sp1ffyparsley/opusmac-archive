"""Verify the published canonical Git blobs against SOURCE_MANIFEST.json.
Use from a Git clone; working-tree edits are intentionally not represented as HEAD.
"""
from pathlib import Path
import hashlib
import json
import subprocess

root = Path(__file__).resolve().parents[1]
def git(*args):
    return subprocess.run(['git', '-C', str(root), *args], check=True,
        stdout=subprocess.PIPE, stderr=subprocess.PIPE).stdout

manifest = json.loads(git('show', 'HEAD:SOURCE_MANIFEST.json'))
entries = manifest['files']
actual = set(git('ls-tree', '-r', '--name-only', '-z', 'HEAD').decode('utf-8').split('\0')) - {'', 'SOURCE_MANIFEST.json'}
assert actual == {e['path'] for e in entries}, 'Manifest file set differs from HEAD'
for e in entries:
    data = git('show', 'HEAD:' + e['path'])
    assert len(data) == e['bytes'], 'Length mismatch: ' + e['path']
    assert hashlib.sha256(data).hexdigest() == e['sha256'], 'SHA-256 mismatch: ' + e['path']
print('Verified', len(entries), 'canonical Git source blobs; manifest excludes itself.')
