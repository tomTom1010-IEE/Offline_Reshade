"""Optional local-vendor regression. No computer use, ImGui activation or capture.

Vendor assets remain outside the repository. Supply a licensed local install and
two existing LUT reference presets (tile 0), with their PNG/BMP library.
"""
import argparse, hashlib, json, queue, shutil, struct, subprocess, threading, time
from pathlib import Path
from PIL import Image, ImageChops, ImageStat

ap=argparse.ArgumentParser()
for name in ('package','source','work','neutral-preset','warm-preset','lut-directory'): ap.add_argument('--'+name,required=True)
args=ap.parse_args()
package=Path(args.package).resolve();source=Path(args.source).resolve();work=Path(args.work).resolve();work.mkdir(parents=True,exist_ok=True)
def manifest():return {str(p.relative_to(source)):hashlib.sha256(p.read_bytes()).hexdigest() for d in ('Effects','Textures','Addons') for p in (source/d).rglob('*') if p.is_file()}
original=manifest();effects=work/'Effects'
shutil.copytree(source/'Effects',effects,dirs_exist_ok=True);shutil.copytree(source/'Textures',work/'Textures',dirs_exist_ok=True)
(effects/'Addons').mkdir(exist_ok=True)
for addon in (source/'Addons').glob('*.addon64'):shutil.copy2(addon,effects/'Addons'/addon.name)
w,h=640,360;color=work/'color.png';depth=work/'depth.rfloat'
im=Image.new('RGB',(w,h));im.putdata([(x*255//(w-1),y*255//(h-1),80) for y in range(h) for x in range(w)]);im.save(color)
depth.write_bytes(b''.join(struct.pack('<f',.3+.65*y/(h-1)) for y in range(h) for x in range(w)))
def seed(name,techniques):
 p=work/(name+'.ini');p.write_text('Techniques='+techniques+'\nTechniqueSorting='+techniques+'\n',encoding='utf-8');return p
curves=seed('curves','MartysMods_ReGradePlus@MartysMods_REGRADE+.fx')
lights=seed('lights','MartysMods_Launchpad@MartysMods_LAUNCHPAD.fx,MartysMods_RELIGHT@MartysMods_RELIGHT.fx')
lut=seed('lut','MartysMods_LutManager@MartysMods_LUTMANAGER.fx')
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
  response=q.get(timeout=180)
  if response.get('id')==ident:
   assert 'error' not in response,response
   return response['result']
def tool(name,fail=False,**kw):
 r=rpc('tools/call',{'name':name,'arguments':kw})
 assert bool(r.get('isError'))==fail,(name,r)
 if fail:return r
 return r.get('structuredContent') or json.loads(next(c['text'] for c in r['content'] if c['type']=='text'))
def action(addon,id,arguments,fail=False):return tool('invoke_addon_action',fail=fail,addon='MARTYSMODS '+addon,id=id,arguments=arguments)
def export(name):
 path=work/(name+'.png');tool('export_image',path=str(path),warmupFrames=10);return path
def diff(a,b):return ImageStat.Stat(ImageChops.difference(Image.open(a).convert('RGB'),Image.open(b).convert('RGB'))).mean
results={}
try:
 rpc('initialize',{'protocolVersion':'2025-11-25','capabilities':{},'clientInfo':{'name':'Marty private interface regression','version':'1'}})
 p.stdin.write(json.dumps({'jsonrpc':'2.0','method':'notifications/initialized'})+'\n');p.stdin.flush()
 tool('open_input',colorPath=str(color),depthPath=str(depth),effectDir=str(effects),presetPath=str(curves),outputPath=str(work/'output.png'))
 while tool('get_status')['loading']:time.sleep(.1)
 services=tool('list_addon_services');(work/'services.json').write_text(json.dumps(services,indent=2),encoding='utf-8')
 assert len(services['services'])==3 and all(s['schema']['privateControls'] for s in services['services']),services
 a=export('curves-before')
 action('ReGrade+','set_curve',{'channel':'RGB','points':[[0,0],[.5,.82],[1,1]]})
 b=export('curves-after');results['curvesDifference']=diff(a,b);assert min(results['curvesDifference'])>5,results
 action('ReGrade+','set_curve',{'channel':'RGB','points':[[0,0],[1,1]]})
 c=export('curves-restored');assert max(diff(a,c))<1,diff(a,c)
 for channel in ('R','G','B'):
  action('ReGrade+','set_curve',{'channel':channel,'points':[[0,0],[.5,.75],[1,1]]})
  ch=export('curve-'+channel);delta=diff(c,ch);idx='RGB'.index(channel)
  assert delta[idx]>5 and all(d<1 for i,d in enumerate(delta) if i!=idx),(channel,delta)
  action('ReGrade+','set_curve',{'channel':channel,'points':[[0,0],[1,1]]})
 action('ReGrade+','set_curve',{'channel':'RGB','points':[[0,0],[.0001,.8],[1,1]]},fail=True)
 print('PASS: composite and individual curves render correctly; invalid quantized points rejected',flush=True)
 tool('load_preset',path=str(lights),unsavedChanges='discard')
 a=export('lights-before');light={'slot':0,'position':[.5,.5,.02],'radius':.1,'intensity':1,'temperatureTint':[0,0]}
 action('ReLight','set_light',light);b=export('lights-after');results['lightsDifference']=diff(a,b);assert max(results['lightsDifference'])>1,results
 light['intensity']=0;action('ReLight','set_light',light);c=export('lights-disabled');assert max(diff(a,c))<3,diff(a,c)
 action('ReLight','set_light',dict(light,slot=64),fail=True)
 tool('export_preset_copy',path=str(work/'lights-saved.ini'))
 print('PASS: sphere light changes GPU output, disable and invalid slot handling',flush=True)
 tool('load_preset',path=str(lut),unsavedChanges='discard')
 for name,reference in [('neutral',args.neutral_preset),('warm',args.warm_preset)]:
  action('LUT-Manager','select_lut_from_preset',{'presetPath':str(Path(reference).resolve()),'lutDirectory':str(Path(args.lut_directory).resolve())})
  export('lut-'+name)
 results['lutDifference']=diff(work/'lut-neutral.png',work/'lut-warm.png');assert results['lutDifference'][0]>5 and results['lutDifference'][2]>5,results
 bad=work/'bad-lut.ini';bad.write_text('[MartysMods_LUTMANAGER.fx]\nLUT_MANAGER_SOURCE_ATLAS=123,456\nLUT_MANAGER_ATLAS_IDX=0\nLUT_MANAGER_ATLAS_TILE_SIZE=32\nLUT_MANAGER_ATLAS_TILE_AMT=32\n',encoding='utf-8')
 action('LUT-Manager','select_lut_from_preset',{'presetPath':str(bad),'lutDirectory':str(Path(args.lut_directory).resolve())},fail=True)
 restored=export('lut-after-rejected');assert max(diff(work/'lut-warm.png',restored))<1,diff(work/'lut-warm.png',restored)
 print('PASS: saved LUT selection changes GPU output; unknown atlas fails and restores prior output/library',flush=True)
 fx=next(effects.rglob('MartysMods_REGRADE+.fx'));contents=fx.read_bytes()
 try:
  fx.write_bytes(contents+b'\n// deliberately unverified revision\n')
  gated=next(s for s in tool('list_addon_services')['services'] if s['addon']=='MARTYSMODS ReGrade+')['schema']
  assert not gated['privateControls'] and not gated['actions'] and gated['privateAdapterError'],gated
 finally:fx.write_bytes(contents)
 assert manifest()==original,'Original vendor resources changed'
 results.update(passed=True,originalVendorFilesUnchanged=True,noComputerUse=True)
 (work/'result.json').write_text(json.dumps(results,indent=2),encoding='utf-8');print('PASS: version gate; original assets unchanged',flush=True)
finally:
 p.stdin.close()
 try:p.wait(timeout=15)
 except subprocess.TimeoutExpired:p.terminate();p.wait(timeout=10)
 log.close()
