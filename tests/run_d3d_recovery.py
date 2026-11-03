"""Native CPU fallback, fault recovery and output-size regressions on a real D3D device."""
import argparse, hashlib, json, os, queue, struct, subprocess, threading, time, uuid
from pathlib import Path
from PIL import Image

ap=argparse.ArgumentParser()
ap.add_argument('--package',required=True); ap.add_argument('--work',required=True)
ap.add_argument('--session'); ap.add_argument('--fault-marker')
args=ap.parse_args(); package=Path(args.package).resolve(); work=Path(args.work).resolve()
work.mkdir(parents=True,exist_ok=True)
effects=work/'Effects'; effects.mkdir(exist_ok=True)
(effects/'Check.fx').write_text('''void VS(uint id : SV_VertexID, out float4 p : SV_Position, out float2 uv : TEXCOORD) {
uv=float2((id<<1)&2,id&2); p=float4(uv*float2(2,-2)+float2(-1,1),0,1); }
float4 PS(float4 p : SV_Position, float2 uv : TEXCOORD) : SV_Target { return float4(0,1,0,1); }
technique Check { pass { VertexShader=VS; PixelShader=PS; } }''',encoding='utf-8')
preset=work/'preset.ini'; preset.write_text('Techniques=Check@Check.fx\n',encoding='utf-8')
source=work/'color.png'; Image.new('RGB',(320,240),(255,0,0)).save(source)
output=work/'output.png'; native=package/'OfflineReShadePrototype.exe'
base=[str(native),'--color',str(source),'--effect-dir',str(effects),'--preset',str(preset),'--output',str(output)]
def digest(path):return hashlib.sha256(path.read_bytes()).hexdigest()
def connect(name,mode):
    deadline=time.monotonic()+20
    while time.monotonic()<deadline:
        try:return open('\\\\.\\pipe\\'+name,mode,buffering=0)
        except OSError:time.sleep(.05)
    raise TimeoutError('Pipe did not open: '+name)
def exact(stream,count):
    result=bytearray()
    while len(result)<count:
        part=stream.read(count-len(result))
        if not part:raise EOFError('Preview pipe closed')
        result.extend(part)
    return result

if not args.session:
    output.write_bytes(b'previous-output')
    proc=subprocess.run(base+['--width','16384','--height','16384','--work-dir',str(work/'oversize')],capture_output=True,text=True,timeout=30)
    assert proc.returncode!=0 and 'limits' in proc.stderr and output.read_bytes()==b'previous-output',proc.stderr
    oversized=work/'oversized-source.png';Image.new('RGB',(17000,1),(255,0,0)).save(oversized)
    bad_source=base.copy();bad_source[2]=str(oversized)
    proc=subprocess.run(bad_source+['--work-dir',str(work/'oversized-source')],capture_output=True,text=True,timeout=30)
    assert proc.returncode!=0 and 'limits' in proc.stderr and output.read_bytes()==b'previous-output',proc.stderr
    for width,height in [(1,128),(128,1),(4096,1)]:
        proc=subprocess.run(base+['--width',str(width),'--height',str(height),'--warmup-frames','2','--work-dir',str(work/f'thin-{width}-{height}')],capture_output=True,text=True,timeout=60)
        assert proc.returncode==0,(proc.stdout,proc.stderr)
        image=Image.open(output).convert('RGB');assert image.size==(width,height) and image.getpixel((0,0))==(0,255,0)
    print('PASS: oversized output rejected before allocation; one-pixel and extreme aspect-ratio exports keep requested dimensions/pixels.',flush=True)

    control_name='OfflineReShade.Test.'+uuid.uuid4().hex; preview_name=control_name+'.preview'
    marker=work/'native-device-loss.trigger'
    if marker.exists():marker.unlink()
    env=os.environ.copy();env['OFFLINE_RESHADE_TEST_SHARED_FAILURE']='1';env['OFFLINE_RESHADE_TEST_DEVICE_LOSS_MARKER']=str(marker)
    log=open(work/'native.log','w',encoding='utf-8')
    proc=subprocess.Popen(base+['--interactive','--preview-shared','--preview-pipe',preview_name,'--control-pipe',control_name,'--work-dir',str(work/'cpu-runtime')],stdout=log,stderr=log,env=env)
    control=preview=None
    try:
        control=connect(control_name,'r+b');preview=connect(preview_name,'rb');sequence=0
        def native_rpc(method,params=None):
            global sequence
            sequence+=1;control.write((json.dumps({'id':sequence,'method':method,'params':params or {}})+'\n').encode())
            response=json.loads(control.readline());assert response['id']==sequence;return response
        for _ in range(300):
            info=native_rpc('get_runtime_info')['result']
            if not info['loading'] and info['frameCount']>3:break
            time.sleep(.05)
        assert info['cpuPreview'] and info['sharedPreviewHandle']==0 and info['previewError'] and info['adapterLuid'] and info['adapterName'],info
        frames=queue.Queue()
        def read_frames():
            try:
                for _ in range(120):
                    magic,w,h,size=struct.unpack('<4I',exact(preview,16));assert magic==0x4650524f and size==w*h*4
                    data=exact(preview,size);frames.put((w,h,bytes(data[:4])))
            except Exception as ex:frames.put(ex)
        threading.Thread(target=read_frames,daemon=True).start()
        for _ in range(120):
            value=frames.get(timeout=20)
            if isinstance(value,Exception):raise value
            if value==(320,240,b'\x00\xff\x00\xff'):break
        else:raise AssertionError('CPU fallback did not deliver rendered pixels')
        assert native_rpc('save_output',{'path':str(output)})['ok']
        expected=digest(output);marker.touch()
        for _ in range(100):
            info=native_rpc('get_runtime_info')['result']
            if info['recoveryRequired']:break
            time.sleep(.05)
        assert info['recoveryRequired'] and info['deviceError'],info
        result=native_rpc('save_output',{'path':str(output)})
        assert not result['ok'] and digest(output)==expected,result
        print('PASS: shared-preview failure falls back to actual CPU frames; injected device loss stays diagnosable and blocks stale output.',flush=True)
    finally:
        proc.kill();proc.wait(timeout=10)
        if control:control.close()
        if preview:preview.close()
        log.close()

marker=Path(args.fault_marker).resolve() if args.fault_marker else work/'mcp-device-loss.trigger'
if marker.exists():marker.unlink()
env=os.environ.copy();env['OFFLINE_RESHADE_TEST_DEVICE_LOSS_MARKER']=str(marker)
log=open(work/'mcp.log','w',encoding='utf-8')
command=[str(package/'Mcp/OfflineReShade.Mcp.exe'),'--native',str(native)]
if args.session:command+=['--session',args.session]
proc=subprocess.Popen(command,stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=log,text=True,bufsize=1,env=env)
messages=queue.Queue();sequence=0
def read_messages():
    for line in proc.stdout:messages.put(json.loads(line))
threading.Thread(target=read_messages,daemon=True).start()
def rpc(method,params):
    global sequence
    sequence+=1;proc.stdin.write(json.dumps({'jsonrpc':'2.0','id':sequence,'method':method,'params':params})+'\n');proc.stdin.flush()
    while True:
        msg=messages.get(timeout=90)
        if msg.get('id')==sequence:
            assert 'error' not in msg,msg
            return msg['result']
def tool(name,**params):
    result=rpc('tools/call',{'name':name,'arguments':params});assert not result.get('isError'),(name,result)
    return result.get('structuredContent') or json.loads(next(x['text'] for x in result['content'] if x['type']=='text'))
try:
    rpc('initialize',{'protocolVersion':'2025-11-25','capabilities':{},'clientInfo':{'name':'d3d-tests','version':'1'}})
    proc.stdin.write(json.dumps({'jsonrpc':'2.0','method':'notifications/initialized'})+'\n');proc.stdin.flush()
    tool('open_input',colorPath=str(source),outputPath=str(output),effectDir=str(effects),presetPath=str(preset),unsavedChanges='discard')
    tool('export_image',path=str(output),warmupFrames=2)
    initial=tool('get_status');assert initial['adapterLuid'] and initial['previewSynchronization']=='keyed-mutex',initial
    tool('set_effects_enabled',enabled=False)
    marker.touch()
    deadline=time.monotonic()+20
    while marker.exists() and time.monotonic()<deadline:time.sleep(.05)
    assert not marker.exists(),'Fault marker was not consumed by the native host'
    if args.session:
        # WinUI polling owns preview recovery; wait for its edit lock before exporting.
        time.sleep(1.2)
        for _ in range(200):
            result=rpc('tools/call',{'name':'get_status','arguments':{}})
            if not result.get('isError'):
                info=result.get('structuredContent') or json.loads(next(x['text'] for x in result['content'] if x['type']=='text'))
                if not info.get('busy') and not info.get('recoveryRequired') and info.get('width')==320:break
            time.sleep(.1)
        else:raise AssertionError('WinUI did not recover the rendering session')
    tool('export_image',path=str(output),warmupFrames=2)
    info=tool('get_status');assert not info['recoveryRequired'] and not info['effectsEnabled'],info
    image=Image.open(output).convert('RGB');assert image.size==(320,240) and image.getpixel((8,6))==(255,0,0)
    tool('set_effects_enabled',enabled=True);tool('export_image',path=str(output),warmupFrames=2,reShadeShot=False)
    assert Image.open(output).convert('RGB').getpixel((8,6))==(0,255,0)
    print('PASS: '+('WinUI-attached' if args.session else 'independent')+' MCP rebuilds after injected device loss, preserves global effect state and exports correct pixels.',flush=True)
finally:
    proc.stdin.close()
    try:proc.wait(timeout=15)
    except subprocess.TimeoutExpired:proc.kill();proc.wait()
    log.close()
