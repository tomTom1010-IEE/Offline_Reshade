"""Validate a complete release ZIP without extracting or executing its contents."""
import argparse
import hashlib
import json
import re
import zipfile
from pathlib import Path, PurePosixPath

REQUIRED = {'OfflineReShadeWinUI.exe', 'OfflineReShadeWinUI.dll', 'OfflineReShade.Core.dll',
            'OfflineReShadePrototype.exe', 'OfflineReShadePreviewBridge.dll', 'ReShade64.dll',
            'App.xbf', 'Views/MainWindow.xbf', 'OfflineReShadeWinUI.pri', 'coreclr.dll', 'hostfxr.dll',
            'Mcp/OfflineReShade.Mcp.exe', 'Mcp/OfflineReShade.Mcp.dll', 'Mcp/OfflineReShade.Core.dll', 'Mcp/coreclr.dll',
            'maintenance/baseline.json', 'maintenance/interfaces.lock.json', 'docs/MAINTENANCE.md', 'verification.json'}


def validate_manifest(manifest, names, read):
    assert manifest['schemaVersion'] == 2, 'Unsupported release manifest'
    files = manifest['files']
    paths = [f['path'] for f in files]
    assert len(paths) == len(set(p.lower() for p in paths)), 'Duplicate/case-colliding paths'
    assert set(paths) | {'manifest.json'} == set(names), 'Unlisted or missing archive entries'
    assert REQUIRED <= set(paths), f'Missing runtime files: {REQUIRED - set(paths)}'
    for entry in files:
        name = entry['path']
        assert '\\' not in name and ':' not in name and not PurePosixPath(name).is_absolute() and '..' not in PurePosixPath(name).parts, f'Unsafe path: {name}'
        assert not re.search(r'(?i)\.(fx|fxh|addon64|pdb|lib|exp|log|disabled|appxmanifest)$|(^|/)settings[^/]*\.json$', name), f'Unexpected payload: {name}'
        data = read(name)
        assert len(data) == entry['size'] and hashlib.sha256(data).hexdigest() == entry['sha256'], f'File hash/size mismatch: {name}'
    baseline = json.loads(read('maintenance/baseline.json').decode('utf-8-sig'))
    provenance = manifest['provenance']
    assert manifest['version'] == baseline['productVersion'], 'Product version mismatch'
    for key in ['upstream', 'interfaces', 'submodules']:
        assert provenance[key] == baseline[key], f'Provenance mismatch: {key}'
    assert provenance['baselineSha256'] == hashlib.sha256(read('maintenance/baseline.json')).hexdigest()
    report = json.loads(read('verification.json').decode('utf-8-sig'))
    assert report['status'] == 'passed' and all(c['exitCode'] == 0 for c in report['commands']), 'Failed build/test report'


def verify(archive):
    archive = Path(archive)
    digest = hashlib.sha256(archive.read_bytes()).hexdigest()
    sidecar = Path(str(archive) + '.sha256').read_text(encoding='utf-8-sig').split()
    assert sidecar == [digest, archive.name], 'ZIP checksum mismatch'
    with zipfile.ZipFile(archive) as z:
        names = [n for n in z.namelist() if not n.endswith('/')]
        assert len(names) == len(set(names)), 'Duplicate ZIP entries'
        validate_manifest(json.loads(z.read('manifest.json').decode('utf-8-sig')), names, z.read)
    return digest, len(names) - 1


if __name__ == '__main__':
    p = argparse.ArgumentParser()
    p.add_argument('archive')
    args = p.parse_args()
    digest, count = verify(args.archive)
    print(f'PASS: {count} release files, provenance and ZIP SHA256 {digest}')
