"""Exercise installed native ABI exports and reject mixed/old ReShade DLLs."""
import argparse
import ctypes
import json
import shutil
import subprocess
from pathlib import Path
from PIL import Image

p = argparse.ArgumentParser()
p.add_argument('--package', required=True, type=Path)
p.add_argument('--work', required=True, type=Path)
args = p.parse_args()
package, work = args.package.resolve(), args.work.resolve()
work.mkdir(parents=True, exist_ok=True)
baseline = json.loads((package / 'maintenance/baseline.json').read_text())
bridge = ctypes.WinDLL(str(package / 'OfflineReShadePreviewBridge.dll'))
assert bridge.ORPreview_GetAbiVersion() == baseline['interfaces']['previewBridge']
ctypes.windll.kernel32.FreeLibrary.argtypes = [ctypes.c_void_p]
ctypes.windll.kernel32.FreeLibrary(bridge._handle)
print('PASS: packaged preview bridge ABI export', flush=True)

original = (package / 'ReShade64.dll').read_bytes()
export = b'ReShadeGetOfflineApiVersion\0'
upstream = baseline['upstream']['commit'].encode()
assert export in original and upstream in original
for name, data in [
    ('missing-api', original.replace(export, b'ReShadeGetOfflineApiVersioX\0')),
    ('wrong-upstream', original.replace(upstream, b'0' * len(upstream))),
]:
    folder = work / name
    folder.mkdir(exist_ok=True)
    shutil.copy2(package / 'OfflineReShadePrototype.exe', folder)
    (folder / 'ReShade64.dll').write_bytes(data)
    (folder / 'Effects').mkdir(exist_ok=True)
    Image.new('RGB', (64, 48), (0, 255, 0)).save(folder / 'source.png')
    output = folder / 'output.png'
    output.write_bytes(b'previous valid output')
    result = subprocess.run([str(folder / 'OfflineReShadePrototype.exe'), '--color', str(folder / 'source.png'),
        '--effect-dir', str(folder / 'Effects'), '--output', str(output), '--work-dir', str(folder / 'runtime'),
        '--warmup-frames', '1', '--timeout-seconds', '15'], capture_output=True, text=True, timeout=30)
    assert result.returncode != 0 and 'Incompatible ReShade64.dll' in result.stderr, (name, result.stdout, result.stderr)
    assert output.read_bytes() == b'previous valid output', name
    print(f'PASS: {name} rejected before render; previous output preserved', flush=True)
