"""Run the exact reviewed Windows actionlint binary; cache only hash-verified bytes."""
import hashlib
import json
import subprocess
import urllib.request
import zipfile
from pathlib import Path

root = Path(__file__).resolve().parents[1]
pin = json.loads((root / 'maintenance/baseline.json').read_text(encoding='utf-8'))['toolchain']['actionlint']
cache = root / 'build/maintenance-tools'
cache.mkdir(parents=True, exist_ok=True)
archive = cache / ('actionlint-' + pin['version'] + '.zip')
if not archive.is_file():
    data = urllib.request.urlopen(pin['url'], timeout=60).read()
    assert hashlib.sha256(data).hexdigest() == pin['sha256'], 'actionlint download hash mismatch'
    archive.write_bytes(data)
assert hashlib.sha256(archive.read_bytes()).hexdigest() == pin['sha256'], 'actionlint cache hash mismatch'
with zipfile.ZipFile(archive) as z:
    executable = cache / 'actionlint.exe'
    executable.write_bytes(z.read('actionlint.exe'))
subprocess.run([str(executable), '-shellcheck=', '-pyflakes=', *map(str, sorted((root / '.github/workflows').glob('*.yml')))], check=True)
print('PASS: actionlint ' + pin['version'] + ' workflow syntax and expressions')
