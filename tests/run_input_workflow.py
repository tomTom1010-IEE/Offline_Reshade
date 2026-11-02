"""Exercise missing-file listening and source transitions through real MCP/native hosts."""
import argparse, json, queue, struct, subprocess, threading, time
from pathlib import Path
from PIL import Image

ap=argparse.ArgumentParser();ap.add_argument('--package',required=True);ap.add_argument('--work',required=True);ap.add_argument('--session')
args=ap.parse_args();package=Path(args.package).resolve();work=Path(args.work).resolve();work.mkdir(parents=True,exist_ok=True)
effects=work/'Effects';effects.mkdir(exist_ok=True)
(effects/'Input.fx').write_text('''uniform float Gain < ui_min=0.0; ui_max=1.0; > = 1.0;
void VS(uint id : SV_VertexID, out float4 p : SV_Position, out float2 uv : TEXCOORD) {
uv=float2((id<<1)&2,id&2);p=float4(uv*float2(2,-2)+float2(-1,1),0,1);
}
float4 PS(float4 p : SV_Position,float2 uv : TEXCOORD) : SV_Target {return float4(Gain,0,0,1);}
technique Input {pass {VertexShader=VS;PixelShader=PS;}}
''',encoding='utf-8')
preset=work/'input.ini';preset.write_text('Techniques=Input@Input.fx\nTechniqueSorting=Input@Input.fx\n',encoding='utf-8')
original=work/'original.png';Image.new('RGB',(64,48),(0,255,0)).save(original)
target=work/'coloroutput.png';depth=work/'depthoutput.rfloat';output=work/'result.png'
base=dict(colorPath=str(original),effectDir=str(effects),outputPath=str(output),presetPath=str(preset))
waiting=dict(base,colorPath=str(target),depthPath=str(depth))
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
def wait_for(predicate):
    until=time.monotonic()+60
    while time.monotonic()<until:
        status=tool('get_operation_status')
        if predicate(status):return status
        time.sleep(.1)
    raise TimeoutError(status)
def exported(size):
    tool('export_image',path=str(output),warmupFrames=2)
    image=Image.open(output).convert('RGB')
    assert image.size==size and 95<=image.getpixel((4,4))[0]<=110,(image.size,image.getpixel((4,4)))
try:
    rpc('initialize',{'protocolVersion':'2025-11-25','capabilities':{},'clientInfo':{'name':'input-workflow','version':'1'}})
    p.stdin.write('{"jsonrpc":"2.0","method":"notifications/initialized"}\n');p.stdin.flush()
    tool('listen_input',**waiting,unsavedChanges='discard')
    status=tool('get_operation_status');assert status['waitingInput'] and status['showEmpty'],status
    tool('stop_listening')
    tool('open_input',**base,unsavedChanges='discard')
    tool('render_preview',warmupFrames=1) # Wait for effect metadata before setting the uniform.
    tool('set_parameters',updates=[{'id':'Input.fx::Gain','value':.4}]);exported((64,48))
    previous=output.read_bytes()
    tool('listen_input',**waiting)
    blocked=tool('export_image',error=True,path=str(output),warmupFrames=1)
    assert blocked['operation']['status']=='rejected' and output.read_bytes()==previous,blocked
    target.write_bytes(b'broken');time.sleep(.7)
    status=tool('get_operation_status');assert status['waitingInput']['state']=='waiting',status
    depth.write_bytes(struct.pack('<f',.5)*(96*64))
    invalid=wait_for(lambda s:s['waitingInput'] and s['waitingInput']['state']=='invalid')
    assert invalid['appliedInput']['colorPath']==str(original) and invalid['runtime']['error'],invalid
    failed_id=invalid['last']['id'];time.sleep(.8)
    assert tool('get_operation_status')['last']['id']==failed_id,'Damaged files were retried without changing'
    tool('stop_listening');exported((64,48))
    tool('listen_input',**waiting)
    Image.new('RGB',(96,64),(0,0,255)).save(work/'replacement.png');(work/'replacement.png').replace(target)
    applied=wait_for(lambda s:s['waitingInput'] is None and s['appliedInput'] and s['appliedInput']['colorPath']==str(target) and s['active'] is None)
    assert applied['appliedInput']['watchInput'] and not applied['showEmpty'],applied
    exported((96,64))
    tool('stop_listening');assert not tool('get_status')['appliedInput']['watchInput']
    future=dict(waiting,colorPath=str(work/'future.png'),depthPath='')
    tool('listen_input',**future);tool('open_input',**base)
    Image.new('RGB',(16,16),(0,0,0)).save(work/'future.png');time.sleep(.8)
    state=tool('get_operation_status');assert state['waitingInput'] is None and state['appliedInput']['colorPath']==str(original),state
    exported((64,48))
    print('PASS: missing-pair listening, stale-output rejection, damaged-pair rollback, change-only retry, preserved parameters, stopping and superseding listeners through real MCP.',flush=True)
finally:
    p.stdin.close()
    try:p.wait(timeout=12)
    except subprocess.TimeoutExpired:p.terminate();p.wait(timeout=5)
    log.close()
