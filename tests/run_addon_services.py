"""Real MCP -> native -> independent Add-on ABI -> GPU output, with no UI automation."""
import argparse,json,queue,shutil,subprocess,threading,time
from pathlib import Path
from PIL import Image
ap=argparse.ArgumentParser();ap.add_argument('--package',required=True);ap.add_argument('--work',required=True);ap.add_argument('--addon',required=True);args=ap.parse_args()
package=Path(args.package).resolve();work=Path(args.work).resolve();effects=work/'Effects';(effects/'Addons').mkdir(parents=True,exist_ok=True)
shutil.copy2(args.addon,effects/'Addons/ServiceFixture.addon64')
(effects/'ServiceFixture.fx').write_text('''uniform float Gain < ui_type="slider"; ui_min=0.0; ui_max=1.0; ui_tooltip="Fixture gain"; ui_units="ratio"; > = 1.0;
uniform bool Trigger < ui_type="button"; > = false;
uniform float3 Tint < ui_type="color"; > = float3(1,1,1);
void VS(uint id : SV_VertexID, out float4 p : SV_Position, out float2 uv : TEXCOORD) { uv=float2((id<<1)&2,id&2);p=float4(uv*float2(2,-2)+float2(-1,1),0,1); }
float4 PS(float4 p:SV_Position,float2 uv:TEXCOORD):SV_Target { return float4(0,Gain,0,1); }
technique ServiceFixture { pass { VertexShader=VS; PixelShader=PS; } }
''')
color=work/'input.png';Image.new('RGB',(320,240),(255,0,0)).save(color)
preset=work/'preset.ini';preset.write_text('Techniques=ServiceFixture@ServiceFixture.fx\n')
log=open(work/'mcp.log','w',encoding='utf-8')
p=subprocess.Popen([str(package/'Mcp/OfflineReShade.Mcp.exe'),'--native',str(package/'OfflineReShadePrototype.exe')],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=log,text=True,bufsize=1,creationflags=subprocess.CREATE_NO_WINDOW)
q=queue.Queue();ident=0
def reader():
 for line in p.stdout:q.put(json.loads(line))
threading.Thread(target=reader,daemon=True).start()
def rpc(method,params):
 global ident
 ident+=1;p.stdin.write(json.dumps({'jsonrpc':'2.0','id':ident,'method':method,'params':params})+'\n');p.stdin.flush()
 while True:
  r=q.get(timeout=90)
  if r.get('id')==ident:
   assert 'error' not in r,r
   return r['result']
def tool(name,fail=False,**kw):
 r=rpc('tools/call',{'name':name,'arguments':kw})
 assert bool(r.get('isError'))==fail,(name,r)
 if fail:return r
 return r.get('structuredContent') or json.loads(next(c['text'] for c in r['content'] if c['type']=='text'))
try:
 rpc('initialize',{'protocolVersion':'2025-11-25','capabilities':{},'clientInfo':{'name':'Addon service regression','version':'1'}})
 p.stdin.write(json.dumps({'jsonrpc':'2.0','method':'notifications/initialized'})+'\n');p.stdin.flush()
 names={t['name'] for t in rpc('tools/list',{})['tools']}
 assert {'list_addons','list_addon_services','set_addon_parameter','invoke_addon_action','press_parameter'}<=names
 tool('open_input',colorPath=str(color),effectDir=str(effects),presetPath=str(preset),outputPath=str(work/'output.png'))
 deadline=time.monotonic()+60
 while tool('get_status')['loading']:
  assert time.monotonic()<deadline;time.sleep(.1)
 addon=next(x for x in tool('list_addons')['addons'] if x['name']=='Independent Service Fixture')
 assert addon['businessApiVersion']==1 and not addon['hasEventOverlay'] and not addon['hasSettingsOverlay']
 services=tool('list_addon_services');(work/'services.json').write_text(json.dumps(services,indent=2));assert len(services['services'])==1 and services['services'][0]['schema']['parameters'][0]['id']=='gain',services
 meta=tool('list_effects',effectName='ServiceFixture.fx',limit=10)
 gain=next(u for u in meta['uniforms'] if u['name']=='Gain');assert gain['ui_tooltip']=='Fixture gain' and gain['ui_units']=='ratio',gain
 tool('set_addon_parameter',addon=addon['name'],id='gain',value=[.25])
 tool('export_image',path=str(work/'quarter.png'),warmupFrames=3)
 assert 60<=Image.open(work/'quarter.png').getpixel((8,8))[1]<=68
 tool('invoke_addon_action',addon=addon['name'],id='reset',arguments={})
 tool('export_image',path=str(work/'reset.png'),warmupFrames=3)
 assert Image.open(work/'reset.png').getpixel((8,8))[1]>250
 tool('set_addon_parameter',fail=True,addon=addon['name'],id='missing',value=[.5])
 tool('set_addon_parameter',fail=True,addon=addon['name'],id='gain',value=[2])
 tool('set_addon_parameter',fail=True,addon=addon['name'],id='gain',value=['bad'])
 tool('press_parameter',id='ServiceFixture.fx::Trigger')
 after=tool('list_effects',effectName='ServiceFixture.fx',limit=10)
 assert next(u for u in after['uniforms'] if u['name']=='Trigger')['value']==[False]
 (work/'result.json').write_text(json.dumps({'passed':True,'tools':sorted(names),'independentSchema':services},indent=2))
 print('PASS: independent Add-on without UI, MCP read/write/action, GPU pixel changes, invalid writes rejected, FX metadata and momentary button',flush=True)
finally:
 p.stdin.close()
 try:p.wait(timeout=15)
 except subprocess.TimeoutExpired:p.terminate();p.wait(timeout=10)
 log.close()
