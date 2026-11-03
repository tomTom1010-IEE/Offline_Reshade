"""Actual native/MCP regressions for failed exports, watch resizing and checkpoint identity."""
import argparse, json, queue, struct, subprocess, threading, time
from pathlib import Path
from PIL import Image

ap = argparse.ArgumentParser()
ap.add_argument('--package', required=True); ap.add_argument('--work', required=True)
ap.add_argument('--session')
args = ap.parse_args(); package = Path(args.package).resolve(); work = Path(args.work).resolve()
work.mkdir(parents=True, exist_ok=True)
native = package / 'OfflineReShadePrototype.exe'
vertex = '''void VS(uint id : SV_VertexID, out float4 p : SV_Position, out float2 uv : TEXCOORD) {
uv = float2((id << 1) & 2, id & 2); p = float4(uv * float2(2,-2)+float2(-1,1),0,1); }
'''
def shader(body, declarations=''):
    return declarations + vertex + 'float4 PS(float4 p : SV_Position, float2 uv : TEXCOORD) : SV_Target { ' + body + ' }\ntechnique Check { pass { VertexShader=VS; PixelShader=PS; } }'
green = shader('return float4(0,1,0,1);')
black = shader('return float4(0,0,0,1);')
broken = shader('return NotDefined;')
missing = shader('return float4(tex2D(S,uv).rgb,1);', 'texture2D T < source="missing.png"; > { Width=16; Height=16; Format=RGBA8; }; sampler2D S { Texture=T; };\n')
depth_shader = shader('return float4(tex2D(S,uv).rrr,1);', 'texture2D T : DEPTH; sampler2D S { Texture=T; };\n')

def fixture(name, code, depth=None, enabled=True):
    folder=work/name; folder.mkdir(exist_ok=True); effects=folder/'Effects';effects.mkdir(exist_ok=True)
    (effects/'Check.fx').write_text(code,encoding='utf-8')
    preset=folder/'preset.ini';preset.write_text('Techniques='+('Check@Check.fx' if enabled else '')+'\n',encoding='utf-8')
    color=folder/'color.png';Image.new('RGB',(320,240),(255,0,0)).save(color)
    item=dict(colorPath=str(color),outputPath=str(folder/'output.png'),effectDir=str(effects),presetPath=str(preset))
    if depth is not None:
        path=folder/'depth.rfloat';path.write_bytes(depth);item['depthPath']=str(path)
    return folder,item

def cli(item, success, warning=False):
    output=Path(item['outputPath']);output.write_bytes(b'previous-good-output')
    command=[str(native),'--color',item['colorPath'],'--effect-dir',item['effectDir'],'--preset',item['presetPath'],
             '--output',str(output),'--work-dir',str(output.parent/'runtime'),'--warmup-frames','3','--timeout-seconds','60']
    if item.get('depthPath'):command+=['--depth',item['depthPath']]
    p=subprocess.run(command,capture_output=True,text=True,timeout=90)
    (output.parent/'cli.log').write_text(p.stdout+p.stderr,encoding='utf-8')
    if success:
        assert p.returncode==0,(p.stdout,p.stderr)
        Image.open(output).verify()
        if warning:assert 'entirely black' in p.stderr,p.stderr
    else:
        assert p.returncode!=0,(output.parent,p.stdout,p.stderr)
        assert output.read_bytes()==b'previous-good-output','Failed export replaced the existing output'

for name,code,depth in [('compile',broken,None),('missing',missing,None),
    ('nan',depth_shader,struct.pack('<f',float('nan'))*(320*240)),
    ('infinity',depth_shader,struct.pack('<f',float('inf'))*(320*240)),
    ('out-of-range',depth_shader,struct.pack('<f',-0.1)*(320*240)),
    ('truncated',depth_shader,b'bad')]:
    _,item=fixture(name,code,depth);cli(item,False)
_,item=fixture('intentional-black',black);cli(item,True,True)
_,item=fixture('disabled-broken',broken,enabled=False);cli(item,True)
print('PASS: native rejects broken enabled FX, missing textures and invalid depth; preserves outputs; permits intentional black and disabled broken FX',flush=True)

logs=open(work/'mcp.log','w',encoding='utf-8')
server=[str(package/'Mcp/OfflineReShade.Mcp.exe'),'--native',str(native)]
if args.session:server+=['--session',args.session]
p=subprocess.Popen(server,stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=logs,text=True,bufsize=1)
messages=queue.Queue();sequence=0
def reader():
    for line in p.stdout:messages.put(json.loads(line))
threading.Thread(target=reader,daemon=True).start()
def rpc(method,params):
    global sequence
    sequence+=1;p.stdin.write(json.dumps({'jsonrpc':'2.0','id':sequence,'method':method,'params':params})+'\n');p.stdin.flush()
    while True:
        msg=messages.get(timeout=90)
        if msg.get('id')==sequence:
            assert 'error' not in msg,msg
            return msg['result']
def tool(name,expect_error=False,**args):
    result=rpc('tools/call',{'name':name,'arguments':args})
    assert bool(result.get('isError'))==expect_error,(name,result)
    if expect_error:return result
    if 'structuredContent' in result:return result['structuredContent']
    return json.loads(next(x['text'] for x in result['content'] if x['type']=='text'))
def job():
    for _ in range(800):
        result=tool('get_job')
        if result['state']!='running':return result
        time.sleep(.05)
    raise TimeoutError('Batch did not finish')
def pixel(path):return Image.open(path).convert('RGB').getpixel((8,6))
try:
    rpc('initialize',{'protocolVersion':'2025-11-25','capabilities':{},'clientInfo':{'name':'correctness-tests','version':'1'}})
    p.stdin.write(json.dumps({'jsonrpc':'2.0','method':'notifications/initialized'})+'\n');p.stdin.flush()
    for name,code in [('mcp-compile',broken),('mcp-missing',missing)]:
        folder,item=fixture(name,code);tool('open_input',**item,unsavedChanges='discard')
        for shot in (False,True):tool('export_image',expect_error=True,path=item['outputPath'],reShadeShot=shot,warmupFrames=2)
        tool('start_batch',items=[item],warmupFrames=2,retries=0)
        result=job();assert result['state']=='failed',result
        assert not Path(item['outputPath']).exists()
        # Snapshot/rebuild must not erase an enabled effect that failed to compile.
        for shot in (False,True):tool('export_image',expect_error=True,path=item['outputPath'],reShadeShot=shot,warmupFrames=2)
    folder,item=fixture('mcp-black',black);tool('open_input',**item,unsavedChanges='discard')
    for shot in (False,True):
        result=tool('export_image',path=item['outputPath'],reShadeShot=shot,warmupFrames=2)
        assert 'black' in result['warning'];assert pixel(item['outputPath'])==(0,0,0)
    print('PASS: both MCP export paths and batch reject damaged effects; black remains a successful export with warning',flush=True)

    folder,item=fixture('watch',green);tool('open_input',**item,watchInput=True,unsavedChanges='discard')
    tool('export_image',path=item['outputPath'],warmupFrames=2)
    Image.new('RGB',(480,320),(255,0,0)).save(folder/'next.png');(folder/'next.png').replace(folder/'color.png')
    time.sleep(.5)
    if args.session:
        info={}
        for _ in range(100):
            status=rpc('tools/call',{'name':'get_status','arguments':{}})
            if status.get('isError'):
                time.sleep(.1);continue # Native pipe is briefly closed during the UI-owned rebuild.
            info=status.get('structuredContent') or json.loads(next(x['text'] for x in status['content'] if x['type']=='text'))
            if info['width']==480 and info['height']==320 and not info.get('busy'):break
            time.sleep(.1)
        assert info.get('width')==480,'WinUI did not automatically rebuild the watched preview'
    tool('export_image',path=item['outputPath'],warmupFrames=2)
    assert Image.open(item['outputPath']).size==(480,320)
    # A broken replacement must not silently export the last good image.
    previous=Path(item['outputPath']).read_bytes();(folder/'color.png').write_bytes(b'incomplete')
    time.sleep(.5)
    tool('export_image',expect_error=True,path=item['outputPath'],warmupFrames=2)
    assert Path(item['outputPath']).read_bytes()==previous
    print('PASS: watched resolution changes rebuild at source size; invalid replacements block stale export',flush=True)

    folder,item=fixture('resume',green)
    source=Path(item['colorPath']);source.write_bytes(source.read_bytes().ljust(4096,b'\0'))
    tool('open_input',**item)
    def batch():
        tool('start_batch',items=[item],warmupFrames=2,reShadeShot=False,resume=True,retries=0)
        result=job();assert result['state']=='completed',result
    batch(); assert pixel(item['outputPath'])==(0,255,0)
    stamp=Path(item['outputPath']).stat().st_mtime_ns;batch()
    assert Path(item['outputPath']).stat().st_mtime_ns==stamp,'Unchanged batch did not resume'
    tool('set_effects_enabled',enabled=False);batch();assert pixel(item['outputPath'])==(255,0,0)
    # Same byte length and timestamp: source content hash must still invalidate resume.
    source=Path(item['colorPath']);old=source.stat();Image.new('RGB',(320,240),(0,0,255)).save(source)
    source.write_bytes(source.read_bytes().ljust(old.st_size,b'\0'));assert source.stat().st_size==old.st_size
    import os
    os.utime(source,ns=(old.st_atime_ns,old.st_mtime_ns))
    batch();assert pixel(item['outputPath'])==(0,0,255)
    tool('set_effects_enabled',enabled=True)
    (folder/'Effects/Check.fx').write_text(black,encoding='utf-8');batch();assert pixel(item['outputPath'])==(0,0,0)
    (folder/'Effects/Check.fx').write_text(broken,encoding='utf-8')
    tool('start_batch',items=[item],warmupFrames=2,retries=0)
    result=job();assert result['state']=='failed',result
    print('PASS: resume matches complete state/content, re-renders after global toggle/source/FX changes, and never skips a damaged effect',flush=True)

    folder,item=fixture('texture-resume',missing)
    textures=folder/'Effects/Textures';textures.mkdir(exist_ok=True)
    Image.new('RGB',(16,16),(255,0,0)).save(textures/'missing.png')
    tool('open_input',**item,unsavedChanges='discard');batch();assert pixel(item['outputPath'])==(255,0,0)
    Image.new('RGB',(16,16),(0,0,255)).save(textures/'missing.png')
    batch();assert pixel(item['outputPath'])==(0,0,255)
    (textures/'missing.png').unlink()
    tool('start_batch',items=[item],warmupFrames=2,retries=0)
    result=job();assert result['state']=='failed',result
    print('PASS: changed texture content invalidates resume; deleted texture fails instead of reusing cached output',flush=True)
finally:
    p.stdin.close()
    try:p.wait(timeout=10)
    except subprocess.TimeoutExpired:p.terminate();p.wait(timeout=5)
    logs.close()
