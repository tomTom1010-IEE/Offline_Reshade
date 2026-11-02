"""Fail closed on upstream, dependency, generated contract or CI pin drift."""
import argparse
import hashlib
import json
import re
import subprocess
from pathlib import Path
from sync_offline_contract import ROOT, sync


def git(*args, directory=ROOT):
    return subprocess.check_output(['git', '-c', 'safe.directory=' + directory.as_posix(), '-C', str(directory), *args], text=True).strip()


def interface_snapshot(root=ROOT):
    baseline = json.loads((root / 'maintenance/baseline.json').read_text())
    # Lock public C++ layouts too: a method insertion changes the runtime vtable.
    headers = ['include/reshade.hpp', 'include/reshade_api.hpp', 'include/reshade_api_device.hpp',
               'include/reshade_api_resource.hpp', 'include/reshade_api_pipeline.hpp', 'include/reshade_api_format.hpp', 'include/reshade_events.hpp']
    files = {name: hashlib.sha256((root / name).read_text(encoding='utf-8').encode()).hexdigest() for name in headers}
    bridge = (root / 'tools/offline_reshade_preview_bridge.cpp').read_text()
    exports = re.findall(r'extern "C" __declspec\(dllexport\) ([^{\n]+)', bridge)
    host = (root / 'tools/offline_reshade_prototype.cpp').read_text()
    return {'versions': baseline['interfaces'], 'reShadeApi': baseline['upstream']['apiVersion'],
            'headers': files, 'previewExports': exports,
            'nativeMethods': sorted(set(re.findall(r'command\.method == "([^"]+)"', host)))}


def source_files():
    paths = git('ls-files', '--cached', '--others', '--exclude-standard', '-z').split('\0')
    directories = {'source', 'include', 'res', 'deps', 'tools', 'tests', 'maintenance', '.github', 'docs',
                   'OfflineReShade.Core', 'OfflineReShade.Mcp', 'OfflineReShadeWinUI'}
    root_files = {'global.json', '.gitmodules', 'README.md', 'README.zh-CN.md', 'LICENSE.md', 'CHANGELOG.md', 'offlineReshadelogo.png'}
    return sorted({p for p in paths if p and (p.split('/')[0] in directories or p in root_files or '/' not in p and Path(p).suffix in {'.vcxproj', '.props', '.sln'}) and not (ROOT / p).is_dir()})


def provenance(b):
    files = source_files()
    digest = hashlib.sha256()
    for name in files:
        path = ROOT / name
        digest.update(name.encode() + b'\0' + (hashlib.sha256(path.read_bytes()).digest() if path.is_file() else b'deleted'))
    status = git('status', '--porcelain', '--untracked-files=all', '--ignore-submodules', '--', *files)
    return {'sourceCommit': git('rev-parse', 'HEAD'), 'sourceDirty': bool(status),
            'sourceSnapshotSha256': digest.hexdigest(), 'sourceFileCount': len(files),
            'baselineSha256': hashlib.sha256((ROOT / 'maintenance/baseline.json').read_bytes()).hexdigest(),
            'upstream': b['upstream'], 'interfaces': b['interfaces'], 'submodules': b['submodules'],
            'toolchainExpected': b['toolchain'],
            'dependencyLockHashes': {p: hashlib.sha256((ROOT / p).read_bytes()).hexdigest() for p in files if p.endswith(('packages.lock.json', 'requirements.lock.txt'))}}


def verify(root=ROOT):
    b = sync(root)
    upstream = b['upstream']
    assert git('rev-parse', upstream['commit'] + '^{tree}') == upstream['tree'], 'Upstream tree mismatch'
    assert git('rev-parse', upstream['nearestTag']) == upstream['tagCommit'], 'Upstream tag moved'
    git('merge-base', '--is-ancestor', upstream['commit'], 'HEAD')
    assert re.search(r'#define RESHADE_API_VERSION (\d+)', (root / 'include/reshade.hpp').read_text()).group(1) == str(upstream['apiVersion'])
    for path, commit in b['submodules'].items():
        assert git('ls-files', '--stage', '--', path).split()[1] == commit, f'Submodule index drift: {path}'
        directory = root / path
        assert git('rev-parse', 'HEAD', directory=directory) == commit, f'Submodule checkout drift: {path}'
        assert not git('status', '--porcelain', '--untracked-files=all', directory=directory), f'Modified dependency: {path}'
    actual = interface_snapshot(root)
    assert actual == json.loads((root / 'maintenance/interfaces.lock.json').read_text()), 'Interface surface changed; review ABI/protocol version and regenerate interfaces.lock.json deliberately'
    for workflow in (root / '.github/workflows').glob('*.yml'):
        text = workflow.read_text()
        for action, revision in re.findall(r'uses:\s*([^\s@]+)@([^\s#]+)', text):
            assert b['actions'].get(action) == revision and re.fullmatch('[0-9a-f]{40}', revision), f'Unpinned action: {action}@{revision}'
        assert 'windows-latest' not in text, f'Floating runner: {workflow}'
    for name in ['OfflineReShade.Core', 'OfflineReShade.Mcp', 'OfflineReShadeWinUI', 'tests/OfflineReShade.Core.Tests']:
        assert (root / name / 'packages.lock.json').is_file(), f'Missing dependency lock: {name}'
    return b


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--write-interface-lock', action='store_true')
    parser.add_argument('--provenance', type=Path)
    args = parser.parse_args()
    if args.write_interface_lock:
        (ROOT / 'maintenance/interfaces.lock.json').write_text(json.dumps(interface_snapshot(), indent=2) + '\n', encoding='utf-8')
    else:
        baseline = verify()
        if args.provenance:
            args.provenance.parent.mkdir(parents=True, exist_ok=True)
            args.provenance.write_text(json.dumps(provenance(baseline), indent=2) + '\n', encoding='utf-8')
        print('PASS: pinned upstream, submodules, interfaces, generated constants and CI actions')
