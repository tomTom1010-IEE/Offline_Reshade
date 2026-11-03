"""Shared FX history and preset semantics through real native/MCP sessions (no UI automation)."""
import argparse, json, queue, struct, subprocess, threading
from pathlib import Path
from PIL import Image

ap=argparse.ArgumentParser();ap.add_argument('--package',required=True);ap.add_argument('--work',required=True);ap.add_argument('--session')
args=ap.parse_args();package=Path(args.package).resolve();work=Path(args.work).resolve();work.mkdir(parents=True,exist_ok=True)
effects=work/'Effects';effects.mkdir(exist_ok=True)
(effects/'History.fx').write_text('''
#ifndef TEST_MODE
#define TEST_MODE 1
#endif
#ifndef SECOND_FLAG
#define SECOND_FLAG 1
#endif
#if TEST_MODE == 99
this_should_fail_compilation
#endif
uniform float Gain = 1.0;
uniform float Tiny = 0.000000000005;
uniform bool2 Flags = bool2(true, false);
uniform int Count = 1;
uniform uint UnsignedCount = 1;
uniform float Clock < source = "timer"; >;
uniform float SessionOnly < nosave = true; > = 0.5;
void VS(uint id : SV_VertexID, out float4 p : SV_Position, out float2 uv : TEXCOORD) {
uv=float2((id<<1)&2,id&2);p=float4(uv*float2(2,-2)+float2(-1,1),0,1);}
float4 PS(float4 p : SV_Position,float2 uv : TEXCOORD) : SV_Target {
return float4(Gain, (Flags.y ? 1 : 0) * Tiny * TEST_MODE * SECOND_FLAG, (Count+UnsignedCount+Clock+SessionOnly)*0.000000000000001, 1);}
technique History {pass {VertexShader=VS;PixelShader=PS;}}
''',encoding='utf-8')
preset=work/'original.ini';preset.write_text('Techniques=History@History.fx\nTechniqueSorting=History@History.fx\n',encoding='utf-8');original=preset.read_bytes()
source=work/'source.png';Image.new('RGB',(48,32),(0,255,0)).save(source)
second=work/'second.png';Image.new('RGB',(80,64),(0,0,255)).save(second)
output=work/'result.png';saved=work/'saved.ini'
base=dict(colorPath=str(source),outputPath=str(output),effectDir=str(effects),presetPath=str(preset),unsavedChanges='discard')
command=[str(package/'Mcp/OfflineReShade.Mcp.exe'),'--native',str(package/'OfflineReShadePrototype.exe')]
if args.session:command+=['--session',args.session]
log=(work/'mcp.log').open('w',encoding='utf-8');p=subprocess.Popen(command,stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=log,text=True,bufsize=1)
lines=queue.Queue();threading.Thread(target=lambda:[lines.put(json.loads(line)) for line in p.stdout],daemon=True).start();sequence=0
def rpc(method,params):
    global sequence
    sequence+=1;p.stdin.write(json.dumps(dict(jsonrpc='2.0',id=sequence,method=method,params=params))+'\n');p.stdin.flush()
    while True:
        result=lines.get(timeout=120)
        if result.get('id')==sequence:
            assert 'error' not in result,result
            return result['result']
def tool(tool_name,error=False,**kw):
    result=rpc('tools/call',dict(name=tool_name,arguments=kw))
    assert bool(result.get('isError'))==error,(tool_name,result)
    return result.get('structuredContent') or json.loads(next(x['text'] for x in result['content'] if x['type']=='text'))
def state():return tool('get_edit_history')
def values():return {x['name']:x['value'] for x in tool('list_effects',effectName='History.fx')['uniforms']}
def definitions():return {x['name']:x['value'] for x in tool('list_effects',effectName='History.fx')['definitions']}
def set_value(name,value,**kw):return tool('set_parameters',updates=[dict(id='History.fx::'+name,value=value)],**kw)
def pixels(red,size=(48,32)):
    tool('export_image',path=str(output),warmupFrames=1,reShadeShot=False)
    with Image.open(output) as image:
        assert image.size==size,image.size
        assert abs(image.convert('RGB').getpixel((2,2))[0]-round(red*255))<=2
try:
    rpc('initialize',dict(protocolVersion='2025-11-25',capabilities={},clientInfo=dict(name='edit-history',version='1')))
    p.stdin.write('{"jsonrpc":"2.0","method":"notifications/initialized"}\n');p.stdin.flush()
    tool('open_input',**base);baseline=state();assert not baseline['isModified'],baseline
    initial=values();assert 4e-12<initial['Tiny'][0]<6e-12,initial
    tiny=struct.unpack('<f',struct.pack('<f',1.23456789e-12))[0]
    tool('set_parameters',updates=[dict(id='History.fx::Gain',value=.4),dict(id='History.fx::Tiny',value=tiny),dict(id='History.fx::Flags',value=[False,True])])
    current=values();assert abs(current['Tiny'][0]-tiny)<1e-20 and current['Flags']==[False,True],current
    tool('undo');assert values()==initial and not state()['isModified'];tool('redo');pixels(.4)
    before=state()['revision'];tool('set_parameters',error=True,updates=[dict(id='History.fx::Gain',value=.9),dict(id='History.fx::missing',value=1)])
    assert values()==current and state()['revision']==before,'Partial failed multi-edit was retained'
    for name,value in [('Gain',1e300),('Count',1e30),('UnsignedCount',-1),('Clock',0),('SessionOnly',0)]:set_value(name,value,error=True)
    assert values()==current,'Invalid values changed native FX state'
    group=tool('begin_edit')['transactionId']
    for gain in [.2,.3,.6]:set_value('Gain',gain,transactionId=group)
    set_value('Gain',.8,error=True);tool('end_edit',transactionId=group)
    tool('undo');pixels(.4);tool('redo');pixels(.6)
    tool('save_preset',path=str(saved));assert not state()['isModified'] and preset.read_bytes()==original
    assert tool('get_status')['appliedInput']['presetPath']==str(saved)
    tool('set_effects_enabled',enabled=False);tool('save_preset');assert 'EffectsEnabled=0' in saved.read_text(encoding='utf-8-sig')
    tool('set_effects_enabled',enabled=True);tool('load_preset',path=str(saved),unsavedChanges='discard')
    assert not tool('get_status')['effectsEnabled'],'Saved global effects state was ignored'
    tool('set_effects_enabled',enabled=True);tool('undo');assert not tool('get_status')['effectsEnabled'];tool('redo')
    tool('reset_parameter',id='History.fx::Gain');pixels(1);tool('undo');pixels(.6)
    current=values();tool('open_input',colorPath=str(second),outputPath=str(output),effectDir=str(effects),presetPath=str(saved),width=40,height=32)
    assert values()==current,'Resolution rebuild lost exact editable values'
    pixels(.6,(40,32));tool('undo');assert not tool('get_status')['effectsEnabled'],'Input change lost undo history';tool('redo')
    tool('load_preset',path=str(preset),error=True)
    revision=state()['revision'];set_value('Gain',.5)
    tool('load_preset',path=str(preset),unsavedChanges='discard',expectedRevision=revision,error=True)
    tool('set_preprocessor_definition',effectName='History.fx',name='TEST_MODE',value='2')
    tool('set_preprocessor_definition',effectName='History.fx',name='SECOND_FLAG',value='3')
    tool('set_preprocessor_definition',effectName='History.fx',name='TEST_MODE',value='')
    assert definitions()['SECOND_FLAG']=='3','Removing one override erased subsequent definitions'
    tool('undo');assert definitions()['TEST_MODE']=='2' and definitions()['SECOND_FLAG']=='3'
    before=state()['revision'];current=values()
    tool('set_preprocessor_definition',effectName='History.fx',name='TEST_MODE',value='99',error=True)
    assert definitions()['TEST_MODE']=='2' and values()==current and state()['revision']==before,'Bad compilation did not restore prior editing state'
    pixels(.5,(40,32))
    tool('load_preset',path=str(preset),unsavedChanges='discard',expectedRevision=state()['revision']);assert not state()['isModified'] and not state()['canUndo']
    assert preset.read_bytes()==original,'Native autosave changed source preset'
    print('PASS: native exact float/bool-vector history, grouped undo, rollback, range validation, source-safe save/as, global state, input rebuild retention, unsaved decisions and preprocessor recovery.',flush=True)
finally:
    p.stdin.close()
    try:p.wait(timeout=12)
    except subprocess.TimeoutExpired:p.terminate();p.wait(timeout=5)
    log.close()
