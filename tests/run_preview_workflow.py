"""Preview-only comparison against real D3D/CPU pixels and MCP state; no UI automation."""
import argparse, json, queue, struct, subprocess, threading, time, uuid
from pathlib import Path
from PIL import Image

ap=argparse.ArgumentParser()
ap.add_argument('--package',required=True);ap.add_argument('--work',required=True)
ap.add_argument('--session');ap.add_argument('--graphics-sampler')
args=ap.parse_args();package=Path(args.package).resolve();work=Path(args.work).resolve();work.mkdir(parents=True,exist_ok=True)
effects=work/'Effects';effects.mkdir(exist_ok=True)
(effects/'Compare.fx').write_text('''uniform float Green = 1;
void VS(uint id : SV_VertexID, out float4 p : SV_Position, out float2 uv : TEXCOORD) {
uv=float2((id<<1)&2,id&2);p=float4(uv*float2(2,-2)+float2(-1,1),0,1);}
float4 PS(float4 p : SV_Position,float2 uv : TEXCOORD) : SV_Target {return float4(0,Green,0,1);}
technique Compare {pass {VertexShader=VS;PixelShader=PS;}}''',encoding='utf-8')
source=work/'source.png';Image.new('RGB',(320,240),(255,0,0)).save(source)
preset=work/'source.ini';preset.write_text('Techniques=Compare@Compare.fx\n',encoding='utf-8')
output=work/'export.png'

def connect(name,mode):
    deadline=time.monotonic()+30
    while time.monotonic()<deadline:
        try:return open('\\\\.\\pipe\\'+name,mode,buffering=0)
        except OSError:time.sleep(.05)
    raise TimeoutError(name)

def exact(stream,count):
    result=bytearray()
    while len(result)<count:
        block=stream.read(count-len(result))
        if not block:raise EOFError('preview stream ended')
        result.extend(block)
    return result

if not args.session:
    assert args.graphics_sampler,'Native comparison needs the graphics sampler.'
    name='OfflineReShade.Compare.'+uuid.uuid4().hex
    log=(work/'native.log').open('w',encoding='utf-8');control=preview=None
    process=subprocess.Popen([str(package/'OfflineReShadePrototype.exe'),'--color',str(source),'--effect-dir',str(effects),
        '--preset',str(preset),'--output',str(output),'--width','2560','--height','1440','--interactive','--preview-shared',
        '--preview-pipe',name+'.preview','--control-pipe',name,'--work-dir',str(work/'native')],stdout=log,stderr=log)
    try:
        control=connect(name,'r+b');preview=connect(name+'.preview','rb');sequence=0
        def native(method,params=None):
            global sequence
            sequence+=1;control.write((json.dumps(dict(id=sequence,method=method,params=params or {}))+'\n').encode())
            result=json.loads(control.readline());assert result['id']==sequence and result.get('ok'),result
            return result['result']
        for _ in range(400):
            info=native('get_runtime_info')
            if not info['loading'] and info['frameCount']>3:break
            time.sleep(.05)
        else:raise AssertionError('effect never became ready')
        assert info['sharedPreviewHandle'] and info['width']==2560 and info['height']==1440,info
        native('set_preview_transport',dict(cpu=True));frames=queue.Queue()
        def read_frames():
            try:
                while True:
                    magic,w,h,size=struct.unpack('<4I',exact(preview,16));assert magic==0x4650524f and size==w*h*4
                    data=exact(preview,size);frames.put((w,h,bytes(data[:4])))
            except (OSError,EOFError):pass
        threading.Thread(target=read_frames,daemon=True).start()
        baseline=native('get_edit_state')
        for original in [False,True,False,True]:
            native('set_preview_comparison',dict(original=original))
            for _ in range(300):
                frame=frames.get(timeout=30)
                if frame==(1920,1080,b'\x00\x00\xff\xff' if original else b'\x00\xff\x00\xff'):break
            else:raise AssertionError(('CPU comparison wrong',original,frame))
            result=subprocess.run([args.graphics_sampler,'--sample-shared',str(info['adapterLuid']),str(info['sharedPreviewHandle']),'2560','1440'],capture_output=True,text=True,timeout=30)
            assert result.returncode==0,(result.stdout,result.stderr)
            assert int(result.stdout.strip())==(0xff0000 if original else 0x00ff00),(original,result.stdout)
            assert native('get_edit_state')==baseline,'Comparison changed FX state'
            assert native('get_runtime_info')['previewOriginal']==original
        for method in ['save_output','save_screenshot','save_preview']:
            native(method,dict(path=str(output)))
            expected_size=(1280,720) if method=='save_preview' else (2560,1440)
            with Image.open(output) as image:assert image.size==expected_size and image.convert('RGB').getpixel((10,10))==(0,255,0),(method,image.size)
        print('PASS: original/processed pixels on real GPU and downsampled CPU preview; both exports and preview capture remain processed; FX state unchanged.',flush=True)
    finally:
        process.kill();process.wait(timeout=10)
        if control:control.close()
        if preview:preview.close()
        log.close()

command=[str(package/'Mcp/OfflineReShade.Mcp.exe'),'--native',str(package/'OfflineReShadePrototype.exe')]
if args.session:command+=['--session',args.session]
log=(work/'mcp.log').open('w',encoding='utf-8')
process=subprocess.Popen(command,stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=log,text=True,bufsize=1)
lines=queue.Queue();threading.Thread(target=lambda:[lines.put(json.loads(line)) for line in process.stdout],daemon=True).start();sequence=0
def rpc(method,params):
    global sequence
    sequence+=1;process.stdin.write(json.dumps(dict(jsonrpc='2.0',id=sequence,method=method,params=params))+'\n');process.stdin.flush()
    while True:
        result=lines.get(timeout=120)
        if result.get('id')==sequence:
            assert 'error' not in result,result
            return result['result']
def tool(name,**kw):
    result=rpc('tools/call',dict(name=name,arguments=kw));assert not result.get('isError'),(name,result)
    return result.get('structuredContent') or json.loads(next(x['text'] for x in result['content'] if x['type']=='text'))
try:
    rpc('initialize',dict(protocolVersion='2025-11-25',capabilities={},clientInfo=dict(name='preview-workflow',version='1')))
    process.stdin.write('{"jsonrpc":"2.0","method":"notifications/initialized"}\n');process.stdin.flush()
    tool('open_input',colorPath=str(source),effectDir=str(effects),presetPath=str(preset),outputPath=str(output),unsavedChanges='discard')
    tool('set_parameters',updates=[dict(id='Compare.fx::Green',value=.5)])
    history=tool('get_edit_history');history.pop('operation',None)
    values=tool('list_effects',effectName='Compare.fx')['uniforms'];saved_bytes=preset.read_bytes()
    for original in [True,False,True]:
        assert tool('set_preview_comparison',original=original)['previewOriginal']==original
        assert tool('get_status')['previewOriginal']==original
        after=tool('get_edit_history')
        after.pop('operation',None) # Each read has its own operation ID, separate from edit revision.
        assert after==history,dict(before=history,after=after)
        assert tool('list_effects',effectName='Compare.fx')['uniforms']==values
    for shot in [True,False]:
        tool('export_image',path=str(output),warmupFrames=1,reShadeShot=shot)
        with Image.open(output) as image:
            pixel=image.convert('RGB').getpixel((10,10));assert pixel[0]==0 and abs(pixel[1]-128)<=1 and pixel[2]==0,pixel
    assert preset.read_bytes()==saved_bytes,'Comparison overwrote source preset'
    tool('open_input',colorPath=str(source),effectDir=str(effects),presetPath=str(preset),outputPath=str(output),width=160,height=120)
    assert not tool('get_status')['previewOriginal'],'Transient comparison survived runtime rebuild'
    tool('undo');assert not tool('get_edit_history')['isModified'],'Comparison polluted undo history'
    print('PASS: MCP comparison leaves dirty marker, history, source INI and exported pixels unchanged; rebuild resets display selection and preserves undo.',flush=True)
finally:
    process.stdin.close()
    try:process.wait(timeout=15)
    except subprocess.TimeoutExpired:process.kill();process.wait(timeout=10)
    log.close()
