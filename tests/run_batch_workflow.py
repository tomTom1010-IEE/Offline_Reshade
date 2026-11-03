"""Frozen batch tasks and editor restoration through real native/MCP, without UI automation."""
import argparse, json, queue, subprocess, threading, time
from pathlib import Path
from PIL import Image

ap = argparse.ArgumentParser()
ap.add_argument('--package', required=True); ap.add_argument('--work', required=True); ap.add_argument('--session')
args = ap.parse_args(); package = Path(args.package).resolve(); work = Path(args.work).resolve(); work.mkdir(parents=True, exist_ok=True)
effects = work / 'Effects'; effects.mkdir(exist_ok=True)
(effects / 'Batch.fx').write_text('''
uniform float Gain = 1.0;
void VS(uint id : SV_VertexID, out float4 p : SV_Position, out float2 uv : TEXCOORD) {
uv=float2((id<<1)&2,id&2);p=float4(uv*float2(2,-2)+float2(-1,1),0,1);}
float4 PS(float4 p : SV_Position,float2 uv : TEXCOORD) : SV_Target {return float4(Gain,0,0,1);}
technique Batch {pass {VertexShader=VS;PixelShader=PS;}}
''', encoding='utf-8')
source = work / 'source.png'; second = work / 'second.png'; broken = work / 'broken.png'
Image.new('RGB', (48, 32), (0, 255, 0)).save(source); Image.new('RGB', (80, 64), (0, 0, 255)).save(second); broken.write_bytes(b'broken')
preset = work / 'original.ini'; other = work / 'other.ini'
prefix = 'Techniques=Batch@Batch.fx\nTechniqueSorting=Batch@Batch.fx\n[Batch.fx]\n'
preset.write_text(prefix + 'Gain=1\n', encoding='utf-8'); other.write_text(prefix + 'Gain=0.7\n', encoding='utf-8')
original_bytes = preset.read_bytes()
base = dict(colorPath=str(source), outputPath=str(work / 'original.png'), effectDir=str(effects), presetPath=str(preset))
items = [dict(base, outputPath=str(work / 'a.png'), presetPath=str(other)),
         dict(base, colorPath=str(broken), outputPath=str(work / 'b.png')),
         dict(base, colorPath=str(second), outputPath=str(work / 'c.png'))]
command = [str(package / 'Mcp/OfflineReShade.Mcp.exe'), '--native', str(package / 'OfflineReShadePrototype.exe')]
if args.session: command += ['--session', args.session]
log = (work / 'mcp.log').open('w', encoding='utf-8')
p = subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=log, text=True, bufsize=1)
lines = queue.Queue(); threading.Thread(target=lambda: [lines.put(json.loads(line)) for line in p.stdout], daemon=True).start(); sequence = 0
def rpc(method, params):
    global sequence
    sequence += 1; p.stdin.write(json.dumps(dict(jsonrpc='2.0', id=sequence, method=method, params=params)) + '\n'); p.stdin.flush()
    while True:
        result = lines.get(timeout=180)
        if result.get('id') == sequence:
            assert 'error' not in result, result
            return result['result']
def tool(name, **kw):
    result = rpc('tools/call', dict(name=name, arguments=kw)); assert not result.get('isError'), (name, result)
    return result.get('structuredContent') or json.loads(next(x['text'] for x in result['content'] if x['type'] == 'text'))
def job():
    end = time.monotonic() + 180
    while time.monotonic() < end:
        result = tool('get_job')
        if result['state'] != 'running': return result
        time.sleep(.1)
    raise TimeoutError('Batch remained active')
def red(path, expected, size):
    with Image.open(path) as image:
        assert image.size == size, image.size
        assert abs(image.convert('RGB').getpixel((2, 2))[0] - round(expected * 255)) <= 2, image.getpixel((2, 2))
def gain(): return next(x['value'][0] for x in tool('list_effects', effectName='Batch.fx')['uniforms'] if x['name'] == 'Gain')
try:
    rpc('initialize', dict(protocolVersion='2025-11-25', capabilities={}, clientInfo=dict(name='batch-workflow', version='1')))
    p.stdin.write('{"jsonrpc":"2.0","method":"notifications/initialized"}\n'); p.stdin.flush()
    tool('open_input', **base, unsavedChanges='discard')
    tool('set_parameters', updates=[dict(id='Batch.fx::Gain', value=.35)])
    tool('set_preview_comparison', original=True)
    before = tool('get_edit_history')
    review = tool('preflight_batch', items=items + [dict(base, colorPath=str(work / 'missing.png'), outputPath=str(work / 'missing-out.png'))])
    assert review['valid'] == 3 and review['invalid'] == 1, review
    tool('start_batch', items=items, warmupFrames=2, retries=0)
    result = job(); assert result['state'] == 'failed' and result['exported'] == 2 and result['failed'] == 1 and result['completed'] == 3, result
    manifest = result['manifestPath']; red(work / 'a.png', .7, (48, 32)); red(work / 'c.png', .35, (80, 64))
    assert abs(gain() - .35) < 1e-6 and tool('get_edit_history')['revision'] == before['revision'], 'Batch lost unsaved FX/history'
    runtime = tool('get_status'); assert runtime['previewOriginal'] and runtime['appliedInput']['colorPath'] == str(source), runtime
    assert preset.read_bytes() == original_bytes, 'Batch wrote to source preset'
    tool('undo'); assert abs(gain() - 1) < 1e-6; tool('redo'); assert abs(gain() - .35) < 1e-6
    stamps = {name: (work / name).stat().st_mtime_ns for name in ['a.png', 'c.png']}
    # Explicitly loading another preset must not alter settings in the saved task.
    tool('load_preset', path=str(other), unsavedChanges='discard')
    tool('set_parameters', updates=[dict(id='Batch.fx::Gain', value=.9)])
    tool('resume_batch', path=manifest); result = job()
    assert result['reused'] == 2 and result['failed'] == 1, result
    assert all((work / name).stat().st_mtime_ns == stamp for name, stamp in stamps.items()), 'Resume rewrote matched outputs'
    assert abs(gain() - .9) < 1e-6, 'Resume did not restore current editor'
    Image.new('RGB', (48, 32), (255, 255, 255)).save(broken)
    tool('retry_failed_batch', path=manifest); result = job()
    assert result['state'] == 'completed' and result['exported'] == 1 and result['reused'] == 2, result
    red(work / 'b.png', .35, (48, 32)); assert abs(gain() - .9) < 1e-6
    (work / 'a.png').write_bytes(b'tampered')
    tool('resume_batch', path=manifest); result = job()
    assert result['exported'] == 1 and result['reused'] == 2, result
    red(work / 'a.png', .7, (48, 32))
    tool('start_batch', items=[items[0]], warmupFrames=0, conflictPolicy='skip'); result = job()
    assert result['skipped'] == 1 and result['exported'] == result['reused'] == 0, result
    tool('set_technique', id='Batch.fx::Batch', enabled=False)
    disabled_history = tool('get_edit_history')['revision']
    disabled = dict(base, presetPath=str(other), outputPath=str(work / 'disabled.png'))
    tool('start_batch', items=[disabled], warmupFrames=1, resume=False); result = job()
    assert result['state'] == 'completed' and abs(gain() - .9) < 1e-6, result
    assert tool('get_edit_history')['revision'] == disabled_history, 'Batch lost disabled-effect parameter history'
    assert not tool('list_effects', effectName='Batch.fx')['techniques'][0]['enabled']
    with Image.open(work / 'disabled.png') as image: assert image.convert('RGB').getpixel((2, 2)) == (0, 255, 0)
    tool('set_technique', id='Batch.fx::Batch', enabled=True)
    assert abs(gain() - .9) < 1e-6, 'Re-enabling a disabled effect restored shader defaults'
    tool('start_batch', items=[items[0]], warmupFrames=100000, resume=False)
    tool('cancel_job'); result = job()
    assert result['state'] == 'cancelled' and abs(gain() - .9) < 1e-6, result
    assert preset.read_bytes() == original_bytes, 'Task operation modified original source preset'
    # Resume in a fresh client/process with no shared cache. The task itself must
    # carry evidence of earlier outputs, independently of later tasks in its folder.
    (work / '.OfflineReShade.batch.json').unlink(missing_ok=True)
    p.stdin.close(); p.wait(timeout=15)
    p = subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=log, text=True, bufsize=1)
    lines = queue.Queue(); sequence = 0
    threading.Thread(target=lambda client=p, sink=lines: [sink.put(json.loads(line)) for line in client.stdout], daemon=True).start()
    rpc('initialize', dict(protocolVersion='2025-11-25', capabilities={}, clientInfo=dict(name='batch-restart', version='1')))
    p.stdin.write('{"jsonrpc":"2.0","method":"notifications/initialized"}\n'); p.stdin.flush()
    tool('resume_batch', path=manifest); result = job()
    assert result['state'] == 'completed' and result['reused'] == 3 and result['exported'] == 0, result
    if not args.session:
        status = tool('get_status'); assert not status['connected'] and not status['busy'], status
    else: assert abs(gain() - .9) < 1e-6
    (work / 'result.json').write_text(json.dumps(dict(status='passed', manifest=manifest, final=result), indent=2), encoding='utf-8')
    print('PASS: real batch preflight, mixed presets/resolutions, per-item failures, frozen resume/retry, verified reuse, tampered outputs, skips, cancellation and exact FX/history/comparison restoration.', flush=True)
finally:
    p.stdin.close()
    try: p.wait(timeout=15)
    except subprocess.TimeoutExpired: p.terminate(); p.wait(timeout=5)
    log.close()
