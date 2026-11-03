"""GPU integration checks against actual staged native and MCP executables.
Requires Python 3 and Pillow; outputs are isolated in a caller-provided directory.
"""
import argparse, base64, json, queue, struct, subprocess, threading, time
from pathlib import Path
from PIL import Image

ap=argparse.ArgumentParser(); ap.add_argument('--package',required=True); ap.add_argument('--work',required=True); ap.add_argument('--session')
args=ap.parse_args(); package=Path(args.package).resolve(); work=Path(args.work).resolve(); work.mkdir(parents=True,exist_ok=True)
effects=work/'Effects'; effects.mkdir(exist_ok=True)
(effects/'Integration.fx').write_text('''texture2D OfflineColor : COLOR;
sampler2D ColorSampler { Texture = OfflineColor; };
texture2D OfflineDepth : DEPTH;
sampler2D DepthSampler { Texture = OfflineDepth; };
uniform float Gain < ui_min = 0.0; ui_max = 1.0; > = 1.0;
uniform bool UseDepth = false;
void VS(uint id : SV_VertexID, out float4 position : SV_Position, out float2 uv : TEXCOORD) {
uv = float2((id << 1) & 2, id & 2); position = float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
}
float4 PS(float4 p : SV_Position, float2 uv : TEXCOORD) : SV_Target { return float4(0, UseDepth ? tex2D(DepthSampler, uv).r : Gain, 0, 1); }
technique Integration { pass { VertexShader = VS; PixelShader = PS; } }
''',encoding='utf-8')
def fixture(name,w,h):
    color=work/(name+'.png'); depth=work/(name+'.rfloat')
    Image.new('RGB',(w,h),(255,0,0)).save(color); depth.write_bytes(struct.pack('<f',0.75)*(w*h)); return color,depth
c1,d1=fixture('first',320,240); c2,d2=fixture('second',480,320)
preset=work/'IntegrationPreset.ini';preset.write_text('Techniques=Integration@Integration.fx\nTechniqueSorting=Integration@Integration.fx\n',encoding='utf-8')
native=package/'OfflineReShadePrototype.exe'; mcp=package/'Mcp'/'OfflineReShade.Mcp.exe'
output=work/'cli.png'
command=[str(native),'--color',str(c1),'--depth',str(d1),'--effect-dir',str(effects),'--preset',str(preset),'--output',str(output),'--work-dir',str(work/'cli-runtime'),'--width','160','--height','120','--warmup-frames','5','--timeout-seconds','60']
result=subprocess.run(command,capture_output=True,text=True,timeout=90)
assert result.returncode==0,(result.stdout,result.stderr)
im=Image.open(output); assert im.size==(160,120),im.size
assert im.getpixel((8,6))[1]>240,('CLI FX not applied',im.getpixel((8,6)))
print('PASS: CLI waits for FX and exports resampled color/depth at requested dimensions',flush=True)
small=command.copy();small[small.index('--width')+1]='64';small[small.index('--height')+1]='48';small[small.index('--output')+1]=str(work/'small.png')
result=subprocess.run(small,capture_output=True,text=True,timeout=90);assert result.returncode==0,(result.stdout,result.stderr)
assert Image.open(work/'small.png').size==(64,48)
print('PASS: offline runtime accepts small render targets',flush=True)

server_command=[str(mcp),'--native',str(native)]
if args.session: server_command+=['--session',args.session]
proc=subprocess.Popen(server_command,stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,bufsize=1)
lines=queue.Queue(); logs=[]
def stdout():
    for line in proc.stdout: lines.put(line)
def stderr():
    for line in proc.stderr: logs.append(line.rstrip())
threading.Thread(target=stdout,daemon=True).start(); threading.Thread(target=stderr,daemon=True).start()
request_id=0
pending={}

def send_rpc(method,params):
    global request_id
    request_id+=1; ident=request_id
    proc.stdin.write(json.dumps({'jsonrpc':'2.0','id':ident,'method':method,'params':params})+'\n'); proc.stdin.flush()
    return ident

def receive_rpc(ident):
    deadline=time.time()+90
    while time.time()<deadline:
        if ident in pending:
            obj=pending.pop(ident); assert 'error' not in obj,obj; return obj['result']
        try: line=lines.get(timeout=1)
        except queue.Empty:
            assert proc.poll() is None,logs[-20:]; continue
        obj=json.loads(line)
        if 'id' in obj: pending[obj['id']]=obj
    raise TimeoutError((ident,logs[-20:]))

def rpc(method,params): return receive_rpc(send_rpc(method,params))

def tool(name,**kw):
    r=rpc('tools/call',{'name':name,'arguments':kw}); assert not r.get('isError'),(name,r,logs[-15:]); return r
def data(r):
    if 'structuredContent' in r: return r['structuredContent']
    return json.loads(next(c['text'] for c in r['content'] if c['type']=='text'))
try:
    rpc('initialize',{'protocolVersion':'2025-11-25','capabilities':{},'clientInfo':{'name':'OfflineReShade integration','version':'1.0'}})
    proc.stdin.write(json.dumps({'jsonrpc':'2.0','method':'notifications/initialized'})+'\n');proc.stdin.flush()
    names={t['name'] for t in rpc('tools/list',{})['tools']}
    assert {'open_input','render_preview','export_image','start_batch','cancel_job','attach_session','get_diagnostics','get_operation_status','cancel_operation'}<=names,names
    diagnostics=data(tool('get_diagnostics'))
    expected_rpc=json.loads((package/'maintenance/baseline.json').read_text())['interfaces']['nativeRpc']
    assert diagnostics['schemaVersion']==1 and diagnostics['expected']['protocolVersion']==expected_rpc,diagnostics
    def config(c,d,out): return dict(colorPath=str(c),depthPath=str(d),effectDir=str(effects),outputPath=str(out),presetPath=str(preset))
    tool('open_input',**config(c1,d1,work/'mcp-first.png'),unsavedChanges='discard')
    diagnostics=data(tool('get_diagnostics'))
    assert diagnostics['runtime']['upstreamCommit']==diagnostics['expected']['upstreamCommit'] and diagnostics['runtimeError'] is None,diagnostics
    preview=tool('render_preview',maxWidth=128,warmupFrames=3)
    assert preview['structuredContent']['operation']['status']=='succeeded',preview
    image=next(c for c in preview['content'] if c['type']=='image'); assert base64.b64decode(image['data']).startswith(b'\x89PNG')
    metadata=data(tool('list_effects',effectName='Integration.fx',limit=10))
    assert {u['id'] for u in metadata['uniforms']} >= {'Integration.fx::Gain','Integration.fx::UseDepth'},metadata
    tool('reorder_techniques',ids=[t['id'] for t in metadata['techniques']])
    tool('export_preset_copy',path=str(work/'saved.ini'));assert (work/'saved.ini').exists()
    tool('set_parameters',updates=[{'id':'Integration.fx::Gain','value':0}])
    black=data(tool('export_image',path=str(work/'black.png'),warmupFrames=3))
    assert black['operation']['status']=='warning' and 'black' in black['operation']['message'],black
    assert max(Image.open(work/'black.png').convert('RGB').getpixel((8,6)))<5
    notice=data(tool('get_operation_status'))['notice']
    assert notice['status']=='warning',notice
    for _ in range(2):
        tool('set_parameters',updates=[{'id':'Integration.fx::Gain','value':0.2}])
        tool('load_preset',path=str(preset),unsavedChanges='discard')
        tool('export_image',path=str(work/'reloaded-preset.png'),warmupFrames=2)
        assert Image.open(work/'reloaded-preset.png').getpixel((8,6))[1]>240,'Same-path preset load retained old edits'
    tool('set_parameters',updates=[{'id':'Integration.fx::Gain','value':1}])
    tool('set_parameters',updates=[{'id':'Integration.fx::UseDepth','value':True}])
    tool('export_image',path=str(work/'depth.png'),warmupFrames=2,reShadeShot=False)
    assert 180 <= Image.open(work/'depth.png').getpixel((8,6))[1] <= 200,'Raw depth texture was not correctly uploaded/resampled'
    tool('set_parameters',updates=[{'id':'Integration.fx::UseDepth','value':False}])
    for i in range(3):
        dest=work/f'repeated-{i}.png';tool('export_image',path=str(dest),warmupFrames=1)
        img=Image.open(dest).convert('RGB'); assert img.getextrema()==((0,0),(255,255),(0,0)),img.getextrema()
    preset_before=preset.read_bytes()
    tool('set_parameters',updates=[{'id':'Integration.fx::Gain','value':0.25}])
    tool('open_input',**config(c2,d2,work/'mcp-second.png'))
    tool('export_image',path=str(work/'mcp-second.png'),warmupFrames=3,reShadeShot=False)
    assert Image.open(work/'mcp-second.png').size==(480,320)
    assert 60 <= Image.open(work/'mcp-second.png').convert('RGB').getpixel((8,6))[1] <= 70,'Resize lost unsaved effect parameters'
    tool('open_input',**config(c2,d2,work/'mcp-scaled.png'),width=160,height=120)
    tool('export_image',path=str(work/'mcp-scaled.png'),warmupFrames=2)
    scaled=Image.open(work/'mcp-scaled.png').convert('RGB')
    assert scaled.size==(160,120) and 60 <= scaled.getpixel((8,6))[1] <= 70,'Explicit render resize lost parameters'
    damaged=work/'damaged.png';damaged.write_bytes(b'incomplete')
    failure=rpc('tools/call',{'name':'open_input','arguments':config(damaged,d2,work/'damaged-output.png')})
    assert failure.get('isError'),failure
    outcome=data(failure)['operation']
    assert outcome['status']=='failed' and outcome['operation']=='open_input' and outcome['sourcePath']==str(damaged),outcome
    tool('export_image',path=str(work/'after-failed-open.png'),warmupFrames=2)
    restored=Image.open(work/'after-failed-open.png').convert('RGB')
    assert restored.size==(160,120) and 60 <= restored.getpixel((8,6))[1] <= 70,'Failed input did not restore previous image/parameters'
    assert preset.read_bytes()==preset_before,'Rebuild silently overwrote the source preset'
    tool('set_parameters',updates=[{'id':'Integration.fx::Gain','value':1}])
    print('PASS: source/explicit resolution changes preserve unsaved parameters; damaged input restores the previous session without saving the source preset',flush=True)
    long_export=send_rpc('tools/call',{'name':'export_image','arguments':{'path':str(work/'cancel-export.png'),'warmupFrames':100000}})
    for _ in range(100):
        activity=data(tool('get_operation_status'))
        if activity['active'] and activity['active']['operation']=='export_image': break
        time.sleep(.05)
    assert activity['active'] and activity['active']['operation']=='export_image',activity
    tool('cancel_operation')
    cancelled=receive_rpc(long_export)
    assert cancelled.get('isError') and data(cancelled)['operation']['status']=='cancelled',cancelled
    tool('export_image',path=str(work/'after-cancel.png'),warmupFrames=1)
    print('PASS: active status and cancellation stay available during export; the next export succeeds',flush=True)
    items=[config(c1,d1,work/'batch-first.png'),config(c2,d2,work/'batch-second.png')]
    tool('start_batch',items=items,warmupFrames=3)
    for _ in range(200):
        job=data(tool('get_job'))
        if job['state']!='running': break
        time.sleep(.1)
    assert job['state']=='completed',job
    assert Image.open(work/'batch-first.png').size==(320,240)
    assert Image.open(work/'batch-second.png').size==(480,320)
    tool('start_batch',items=items,warmupFrames=100000,resume=False)
    tool('cancel_job')
    for _ in range(100):
        job=data(tool('get_job'))
        if job['state']!='running':break
        time.sleep(.1)
    assert job['state']=='cancelled',job
    print('PASS: MCP handshake/tools, preview image, parameter editing, clean repeated screenshots, mixed-resolution batch and cancellation',flush=True)
finally:
    proc.stdin.close()
    try: proc.wait(timeout=10)
    except subprocess.TimeoutExpired: proc.terminate();proc.wait(timeout=5)
    (work/'mcp-test.log').write_text('\n'.join(logs),encoding='utf-8')
