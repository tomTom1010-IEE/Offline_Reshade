"""Real capture callbacks, conflict policies, apply/export and failure preservation."""
import argparse, json, queue, shutil, subprocess, threading, time
from pathlib import Path
from PIL import Image

ap=argparse.ArgumentParser();ap.add_argument('--package',required=True);ap.add_argument('--work',required=True);ap.add_argument('--session')
ap.add_argument('--addon',default=str(Path(__file__).resolve().parents[1]/'bin/x64/Release/OfflineReShade.CaptureContract.addon64'))
args=ap.parse_args();package=Path(args.package).resolve();work=Path(args.work).resolve();work.mkdir(parents=True,exist_ok=True)
effects=work/'Effects';effects.mkdir(exist_ok=True);addons=effects/'Addons';addons.mkdir(exist_ok=True)
addon=addons/'CaptureContract.addon64';shutil.copy2(args.addon,addon);events=Path(str(addon)+'.events')
(effects/'Export.fx').write_text('''uniform float Gain < ui_min=0.0; ui_max=1.0; > = 1.0;
void VS(uint id : SV_VertexID, out float4 p : SV_Position, out float2 uv : TEXCOORD) {
uv=float2((id<<1)&2,id&2);p=float4(uv*float2(2,-2)+float2(-1,1),0,1);}
float4 PS(float4 p : SV_Position,float2 uv : TEXCOORD) : SV_Target {return float4(Gain,0,0,1);}
technique Export {pass {VertexShader=VS;PixelShader=PS;}}
''',encoding='utf-8')
preset=work/'export.ini';preset.write_text('Techniques=Export@Export.fx\nTechniqueSorting=Export@Export.fx\n',encoding='utf-8')
source=work/'source.png';Image.new('RGB',(64,48),(0,255,0)).save(source)
next_source=work/'next.png';Image.new('RGB',(93,61),(0,0,255)).save(next_source)
broken=work/'broken.png';broken.write_bytes(b'not a PNG')
output=work/'result.png';base=dict(colorPath=str(source),effectDir=str(effects),outputPath=str(output),presetPath=str(preset))
log=open(work/'mcp.log','w',encoding='utf-8')
command=[str(package/'Mcp/OfflineReShade.Mcp.exe'),'--native',str(package/'OfflineReShadePrototype.exe')]
if args.session:command+=['--session',args.session]
p=subprocess.Popen(command,stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=log,text=True,bufsize=1)
messages=queue.Queue();sequence=0
def reader():
    for line in p.stdout:messages.put(json.loads(line))
threading.Thread(target=reader,daemon=True).start()
def rpc(method,params):
    global sequence
    sequence+=1;p.stdin.write(json.dumps({'jsonrpc':'2.0','id':sequence,'method':method,'params':params})+'\n');p.stdin.flush()
    while True:
        result=messages.get(timeout=90)
        if result.get('id')==sequence:
            assert 'error' not in result,result
            return result['result']
def tool(name,error=False,**kw):
    result=rpc('tools/call',{'name':name,'arguments':kw})
    assert bool(result.get('isError'))==error,(name,result)
    return result.get('structuredContent') or json.loads(next(x['text'] for x in result['content'] if x['type']=='text'))
def count():return events.read_text().count('screenshot') if events.exists() else 0
def wait_count(expected):
    deadline=time.monotonic()+5
    while count()<expected and time.monotonic()<deadline:time.sleep(.05)
    assert count()==expected,(count(),expected)
def pixels(path,size):
    with Image.open(path) as image:
        image=image.convert('RGB');assert image.size==size,image.size
        red,green,blue=image.getextrema()
        assert 98<=red[0]<=red[1]<=106 and green==(0,0) and blue==(0,0),(red,green,blue)
try:
    rpc('initialize',{'protocolVersion':'2025-11-25','capabilities':{},'clientInfo':{'name':'export-workflow','version':'1'}})
    p.stdin.write('{"jsonrpc":"2.0","method":"notifications/initialized"}\n');p.stdin.flush()
    tool('open_input',**base);tool('render_preview',warmupFrames=1)
    tool('set_parameters',updates=[{'id':'Export.fx::Gain','value':.4}])
    assert count()==0,'Preview triggered screenshot events'
    result=tool('export_image',path=str(output),warmupFrames=1)
    assert result['path']==str(output);pixels(output,(64,48));wait_count(1)
    tool('export_image',path=str(output),warmupFrames=1,reShadeShot=False)
    pixels(output,(64,48));time.sleep(.2);assert count()==1,'Direct PNG triggered screenshot events'
    previous=output.read_bytes()
    tool('export_image',error=True,path=str(output),conflictPolicy='fail')
    assert output.read_bytes()==previous and count()==1
    renamed=tool('export_image',path=str(output),warmupFrames=1,conflictPolicy='rename')
    assert renamed['path']==str(work/'result (1).png') and output.read_bytes()==previous,renamed
    assert renamed['operation']['targetPath']==renamed['path'],renamed
    pixels(renamed['path'],(64,48));wait_count(2)
    applied=tool('get_status')['appliedInput']
    next_input=dict(applied,colorPath=str(next_source),width=32,height=24)
    switched=tool('export_image',path=str(output),warmupFrames=1,applyInput=next_input,expectedInput=applied)
    assert switched['operation']['sourcePath']==str(next_source),switched
    pixels(output,(32,24));wait_count(3);previous=output.read_bytes()
    tool('export_image',error=True,path=str(output),expectedInput=applied)
    assert output.read_bytes()==previous and count()==3,'Stale source binding exported a changed input'
    failed=tool('export_image',error=True,path=str(output),applyInput=dict(next_input,colorPath=str(broken)))
    assert failed['operation']['status']=='failed' and output.read_bytes()==previous,failed
    tool('export_image',path=str(output),warmupFrames=1,reShadeShot=False);pixels(output,(32,24));assert count()==3
    assert not list(work.glob('.OfflineReShade-export-*')),'Temporary output leaked'
    print('PASS: default screenshot callback, direct-PNG event exclusion, atomic conflict policies, full-frame pixels, explicit apply/export, stale-input rejection and damaged-input rollback through real MCP.',flush=True)
finally:
    p.stdin.close()
    try:p.wait(timeout=12)
    except subprocess.TimeoutExpired:p.terminate();p.wait(timeout=5)
    log.close()
