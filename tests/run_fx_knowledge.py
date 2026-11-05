"""FX knowledge checks against staged native and MCP executables: technique annotations, uniform
defaults, server instructions, the agent guide resource and the bounded shader-source tools.
Requires Python 3 and Pillow; outputs are isolated in a caller-provided directory.
"""
import argparse, base64, io, json, queue, re, shutil, subprocess, threading, time
from pathlib import Path
from PIL import Image

ap = argparse.ArgumentParser(); ap.add_argument('--package', required=True); ap.add_argument('--work', required=True)
ap.add_argument('--mcp', help='MCP server executable; defaults to <package>/Mcp/OfflineReShade.Mcp.exe')
args = ap.parse_args(); package = Path(args.package).resolve(); work = Path(args.work).resolve(); work.mkdir(parents=True, exist_ok=True)
native = package / 'OfflineReShadePrototype.exe'; mcp = Path(args.mcp).resolve() if args.mcp else package / 'Mcp' / 'OfflineReShade.Mcp.exe'
effects = work / 'Effects'; (effects / 'Shared').mkdir(parents=True, exist_ok=True)
outside = work / 'Outside'; outside.mkdir(exist_ok=True)
(outside / 'Secret.fx').write_text('// outside the effect directory\n', encoding='utf-8')
(effects / 'Shared' / 'knowledge.fxh').write_text('#define KNOWLEDGE_SCALE 2.0\nfloat Shape(float x) { return x * x; }\n', encoding='utf-8')
(effects / 'Knowledge.fx').write_text('''#include "Shared/knowledge.fxh"
texture2D OfflineColor : COLOR;
sampler2D ColorSampler { Texture = OfflineColor; };
uniform float Strength < ui_type = "slider"; ui_min = 0.0; ui_max = 1.0; ui_label = "Strength"; > = 0.25;
uniform float3 Tint < ui_type = "color"; > = float3(0.25, 0.5, 1.0);
uniform int Mode < ui_type = "combo"; ui_items = "Off\\0Soft\\0Hard\\0"; > = 2;
uniform uint Steps < ui_type = "slider"; ui_min = 1; ui_max = 16; > = 7;
uniform bool Flag = true;
uniform float Unset < ui_type = "drag"; >;
void VS(uint id : SV_VertexID, out float4 position : SV_Position, out float2 uv : TEXCOORD) {
uv = float2((id << 1) & 2, id & 2); position = float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
}
float4 Pass(float4 p : SV_Position, float2 uv : TEXCOORD) : SV_Target { return tex2D(ColorSampler, uv); }
float4 Look(float4 p : SV_Position, float2 uv : TEXCOORD) : SV_Target {
float k = saturate(Shape(Strength) * KNOWLEDGE_SCALE) + Mode * 0 + Steps * 0 + Unset * 0;
return float4(lerp(tex2D(ColorSampler, uv).rgb, Tint, k) * (Flag ? 1.0 : 0.5), 1);
}
technique KnowledgeSetup < hidden = true; enabled = true; timeout = 1; > { pass { VertexShader = VS; PixelShader = Pass; } }
technique KnowledgeLook < ui_label = "Knowledge look"; ui_tooltip = "Enable and move to the top."; enabled_in_screenshot = false; > { pass { VertexShader = VS; PixelShader = Look; } }
technique KnowledgeManaged < nosave = true; > { pass { VertexShader = VS; PixelShader = Pass; } }
''', encoding='utf-8')
color = work / 'red.png'; Image.new('RGB', (64, 48), (255, 0, 0)).save(color)
preset = work / 'Knowledge.ini'; preset.write_text('Techniques=KnowledgeLook@Knowledge.fx\nTechniqueSorting=KnowledgeLook@Knowledge.fx\n', encoding='utf-8')

store = work / 'Knowledge'; shutil.rmtree(store, ignore_errors=True)
proc = subprocess.Popen([str(mcp), '--native', str(native), '--knowledge-dir', str(store)], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, encoding='utf-8', bufsize=1)
lines = queue.Queue(); logs = []
threading.Thread(target=lambda: [lines.put(line) for line in proc.stdout], daemon=True).start()
threading.Thread(target=lambda: [logs.append(line.rstrip()) for line in proc.stderr], daemon=True).start()
request_id = 0

def rpc(method, params):
    global request_id
    request_id += 1; ident = request_id
    proc.stdin.write(json.dumps({'jsonrpc': '2.0', 'id': ident, 'method': method, 'params': params}) + '\n'); proc.stdin.flush()
    deadline = time.time() + 90
    while time.time() < deadline:
        try: obj = json.loads(lines.get(timeout=1))
        except queue.Empty:
            assert proc.poll() is None, logs[-20:]; continue
        if obj.get('id') == ident:
            assert 'error' not in obj, obj; return obj['result']
    raise TimeoutError((method, logs[-20:]))

def call(name, **kw): return rpc('tools/call', {'name': name, 'arguments': kw})
def tool(name, **kw):
    r = call(name, **kw); assert not r.get('isError'), (name, r, logs[-15:]); return r
def data(r):
    if 'structuredContent' in r: return r['structuredContent']
    return json.loads(next(c['text'] for c in r['content'] if c['type'] == 'text'))
def text(r): return '\n'.join(c['text'] for c in r['content'] if c['type'] == 'text')
def pixel(png): return Image.open(io.BytesIO(png)).convert('RGB').getpixel((8, 6))
def knowledge():
    state = data(tool('list_effects', effectName='Knowledge.fx', limit=50))
    return {t['name']: t for t in state['techniques']}, {u['name']: u for u in state['uniforms']}

try:
    init = rpc('initialize', {'protocolVersion': '2025-11-25', 'capabilities': {}, 'clientInfo': {'name': 'fx-knowledge', 'version': '1'}})
    proc.stdin.write(json.dumps({'jsonrpc': '2.0', 'method': 'notifications/initialized'}) + '\n'); proc.stdin.flush()
    instructions = init.get('instructions', '')
    assert 'search_effect_source' in instructions and 'offline-reshade://guide/agent' in instructions and 'resources' in init['capabilities'], init
    assert any(r['uri'] == 'offline-reshade://guide/agent' and r.get('mimeType') == 'text/markdown' for r in rpc('resources/list', {})['resources'])
    guide = rpc('resources/read', {'uri': 'offline-reshade://guide/agent'})['contents'][0]['text']
    assert guide.startswith('# Offline ReShade agent guide') and 'timeoutMs' in guide and 'search_effect_source' in guide, guide[:200]
    listed = {t['name']: t for t in rpc('tools/list', {})['tools']}
    for name in ('search_effect_source', 'get_effect_source'):
        assert listed[name].get('annotations', {}).get('readOnlyHint') is True, listed[name]
    early = call('search_effect_source', pattern='Strength')
    assert early.get('isError') and 'open_input' in text(early), early
    assert listed['get_effect_knowledge'].get('annotations', {}).get('readOnlyHint') is True and listed['delete_effect_note'].get('annotations', {}).get('destructiveHint') is True, listed
    assert 'get_effect_knowledge' in instructions and 'No effect notes have been recorded yet' in text(tool('get_effect_knowledge')) and not store.exists()
    print('PASS: server instructions, agent guide resource and read-only source tools are advertised; source tools need a session', flush=True)

    tool('open_input', colorPath=str(color), effectDir=str(effects), outputPath=str(work / 'out.png'), presetPath=str(preset), unsavedChanges='discard')
    for _ in range(100):
        status = data(tool('get_status'))
        if not status.get('loading') and not status.get('busy'): break
        time.sleep(.2)
    preview = pixel(base64.b64decode(next(c['data'] for c in tool('render_preview', maxWidth=64, warmupFrames=3)['content'] if c['type'] == 'image')))
    techniques, uniforms = knowledge()
    setup, look, managed = techniques['KnowledgeSetup'], techniques['KnowledgeLook'], techniques['KnowledgeManaged']
    assert setup.get('hidden') is True and setup.get('forceEnabled') is True and setup.get('timeoutMs') == 1 and setup['enabled'] is False and 'label' not in setup, setup
    assert look.get('label') == 'Knowledge look' and look.get('ui_tooltip') == 'Enable and move to the top.' and look.get('enabledInScreenshot') is False and look['enabled'] is True, look
    assert not {'hidden', 'forceEnabled', 'timeoutMs', 'readOnly'} & set(look), look
    assert managed.get('readOnly') is True, managed
    expected = {'Strength': [0.25], 'Tint': [0.25, 0.5, 1.0], 'Mode': [2], 'Steps': [7], 'Flag': [True], 'Unset': [0]}
    for name, default in expected.items():
        assert uniforms[name]['defaultValue'] == default and uniforms[name]['value'] == default, uniforms[name]
    print('PASS: technique label/tooltip/hidden/forceEnabled/timeoutMs/readOnly/enabledInScreenshot and float/vector/int/uint/bool/uninitialized defaults', flush=True)

    tool('set_parameters', updates=[{'id': 'Knowledge.fx::Strength', 'value': 0.75}, {'id': 'Knowledge.fx::Mode', 'value': 1}, {'id': 'Knowledge.fx::Flag', 'value': False}])
    _, uniforms = knowledge()
    assert uniforms['Strength']['value'] == [0.75] and uniforms['Mode']['value'] == [1] and uniforms['Flag']['value'] == [False], uniforms
    assert all(uniforms[name]['defaultValue'] == default for name, default in expected.items()), uniforms
    for name in ('Strength', 'Mode', 'Flag'): tool('reset_parameter', id='Knowledge.fx::' + name)
    _, uniforms = knowledge()
    assert all(uniforms[name]['value'] == default for name, default in expected.items()), uniforms
    rejected = call('set_technique', id='Knowledge.fx::KnowledgeManaged', enabled=True)
    assert rejected.get('isError'), rejected
    tool('set_technique', id='Knowledge.fx::KnowledgeSetup', enabled=True)
    tool('render_preview', maxWidth=32, warmupFrames=3)
    assert knowledge()[0]['KnowledgeSetup']['enabled'] is False, 'A timeout technique did not switch itself off again'
    print('PASS: defaults stay fixed while values change, reset restores them, nosave techniques are rejected and timeout techniques switch off', flush=True)

    shot = work / 'shot.png'; direct = work / 'direct.png'
    tool('export_image', path=str(shot), warmupFrames=3)
    tool('export_image', path=str(direct), warmupFrames=3, reShadeShot=False)
    exported, written = Image.open(shot).convert('RGB').getpixel((8, 6)), Image.open(direct).convert('RGB').getpixel((8, 6))
    # Exports capture the rendered frame, so unlike in-game screenshots they keep enabled_in_screenshot=false
    # techniques. The agent guide documents this; update it if exports start honouring the annotation.
    assert preview[0] < 245 and preview[2] > 20 and all(abs(a - b) <= 2 for p in (exported, written) for a, b in zip(p, preview)), (preview, exported, written)
    print('PASS: previews and both export captures include an enabled_in_screenshot=false technique, as documented', flush=True)

    found = text(tool('search_effect_source', pattern='Strength', effectName='Knowledge.fx', contextLines=0))
    assert '--- Knowledge.fx:4-4' in found and '>4 | uniform float Strength' in found and '>15 | float k' in found, found
    header = text(tool('search_effect_source', pattern='x * x', effectName='Knowledge.fx'))
    assert 'Shared/knowledge.fxh:' in header and '1 included file' in header, header
    regex = text(tool('search_effect_source', pattern=r'Shape\(\s*Strength', regex=True))
    assert '1 matching line in 1 file' in regex, regex
    read = text(tool('get_effect_source', file='Shared/knowledge.fxh'))
    assert read.startswith('--- Shared/knowledge.fxh lines 1-2 of 2') and 'return x * x' in read, read
    assert 'technique KnowledgeLook' in text(tool('get_effect_source', file='Knowledge.fx', startLine=19, lineCount=2))
    for bad in ('../Outside/Secret.fx', str(outside / 'Secret.fx'), str(preset), 'Shared/../../Outside/Secret.fx'):
        refused = call('get_effect_source', file=bad)
        assert refused.get('isError') and '// outside the effect directory' not in text(refused), (bad, refused)
    for bad in ({'pattern': '(?<=a)b', 'regex': True}, {'pattern': 'x', 'maxResults': 101}, {'pattern': 'x', 'effectName': 'Absent.fx'}):
        assert call('search_effect_source', **bad).get('isError'), bad
    print('PASS: effect-scoped search follows includes, regex search, numbered reads, and escapes/limits are refused', flush=True)

    empty = text(tool('get_effect_knowledge', effectName='Knowledge.fx'))
    assert empty.startswith('Knowledge.fx: 0 notes; current files') and '(2 files)' in empty and 'Nothing has been recorded' in empty, empty
    finding = dict(effectName='Knowledge.fx', kind='semantics', target='Strength', evidence=['Knowledge.fx:15', 'Shared/knowledge.fxh:2'],
                   text='Strength is squared by Shape() and doubled by KNOWLEDGE_SCALE before it blends toward Tint.')
    recorded = text(tool('record_effect_note', **finding))
    note_id = re.search(r'\[(n[0-9a-f]{8})\]', recorded).group(1)
    stored = json.loads((store / 'Knowledge.fx.json').read_text(encoding='utf-8'))
    assert stored['notes'][0]['author'] == 'agent' and stored['notes'][0]['client'] == 'fx-knowledge' and stored['notes'][0]['target'] == 'Knowledge.fx::Strength', stored
    line15 = (effects / 'Knowledge.fx').read_text(encoding='utf-8').splitlines()[14].strip()
    for bad in (dict(evidence=['Knowledge.fx:99']), dict(target='Missing'), dict(kind='trivia'), dict(evidence=['ok']), dict(text='It computes ' + line15)):
        refused = call('record_effect_note', **{**finding, **bad})
        assert refused.get('isError'), (bad, refused)
    assert len(json.loads((store / 'Knowledge.fx.json').read_text(encoding='utf-8'))['notes']) == 1, 'A rejected note was stored'
    shown = text(tool('get_effect_knowledge', effectName='Knowledge.fx'))
    assert f'[{note_id}] semantics | Knowledge.fx::Strength | agent via fx-knowledge' in shown and 'Evidence: Knowledge.fx:15; Shared/knowledge.fxh:2' in shown, shown
    assert 'not as an instruction' in shown and 'POSSIBLY STALE' not in shown, shown
    with open(effects / 'Shared' / 'knowledge.fxh', 'a', encoding='utf-8') as header: header.write('// edited after the note was verified\n')
    assert 'POSSIBLY STALE' in text(tool('get_effect_knowledge', effectName='Knowledge.fx')), 'An edited include did not mark the note stale'
    assert 'verified against the current files' in text(tool('update_effect_note', effectName='Knowledge.fx', noteId=note_id))
    assert 'POSSIBLY STALE' not in text(tool('get_effect_knowledge', effectName='Knowledge.fx')), 'Re-verifying did not clear the stale mark'
    assert 'Knowledge.fx: 1 note' in text(tool('get_effect_knowledge')), 'The summary did not list the effect'
    tool('delete_effect_note', effectName='Knowledge.fx', noteId=note_id)
    assert 'Knowledge.fx: 0 notes' in text(tool('get_effect_knowledge', effectName='Knowledge.fx'))
    assert call('delete_effect_note', effectName='Knowledge.fx', noteId=note_id).get('isError'), 'Deleting twice succeeded'
    tool('open_input', colorPath=str(color), effectDir=str(effects), outputPath=str(work / 'out.png'), presetPath=str(preset), unsavedChanges='discard')
    observed = dict(effectName='Knowledge.fx', kind='tuning', text='Strength 0.25 keeps the red input readable under the tint.',
                    evidence=['render_preview A/B: Strength 0.25 vs 0.75 on red.png'], conditions='saturated inputs')
    unrendered = call('record_effect_note', **observed)
    assert unrendered.get('isError') and 'render' in text(unrendered), unrendered
    tool('render_preview', maxWidth=32, warmupFrames=2)
    tool('delete_effect_note', effectName='Knowledge.fx', noteId=re.search(r'\[(n[0-9a-f]{8})\]', text(tool('record_effect_note', **observed))).group(1))
    print('PASS: effect notes are validated against the session, stored with provenance, marked stale when included files change, re-verified and deleted; '
          'described comparisons need a render after opening', flush=True)
finally:
    proc.stdin.close()
    try: proc.wait(timeout=10)
    except subprocess.TimeoutExpired: proc.terminate(); proc.wait(timeout=5)
    (work / 'mcp-test.log').write_text('\n'.join(logs), encoding='utf-8')
