/// <summary>
/// The phone controller web page (served at GET /). Self-contained: no
/// external scripts, fonts or CDNs. Uses only single quotes so it can live
/// in a C# verbatim string. Designed for landscape; in portrait the whole UI
/// is rotated 90 degrees with CSS (touches are resolved per element, so hit
/// testing follows the rotation). The first tap requests fullscreen and a
/// landscape orientation lock where the browser supports it (Android Chrome).
/// </summary>
public static class ControllerPage
{
    public const string Html = @"<!DOCTYPE html>
<html lang='en'>
<head>
<meta charset='utf-8'>
<meta name='viewport' content='width=device-width,initial-scale=1,maximum-scale=1,user-scalable=no,viewport-fit=cover'>
<meta name='apple-mobile-web-app-capable' content='yes'>
<meta name='mobile-web-app-capable' content='yes'>
<meta name='screen-orientation' content='landscape'>
<title>Kart Controller</title>
<style>
:root{--pc:#888;--bg:#15171c;--panel:#23262e;--text:#f2f2f2}
*{box-sizing:border-box;-webkit-user-select:none;user-select:none;-webkit-touch-callout:none;-webkit-tap-highlight-color:transparent;touch-action:none}
html,body{margin:0;height:100%;width:100%;background:radial-gradient(circle at 50% 30%,#2a2f45 0%,#12141c 70%);color:var(--text);font-family:'Arial Black','Segoe UI Black',Impact,system-ui,sans-serif;letter-spacing:.5px;overflow:hidden;overscroll-behavior:none;position:fixed;inset:0}
#app{position:fixed;left:0;top:0;width:100vw;height:100vh;display:flex;flex-direction:column}
#app.rot{width:100vh;height:100vw;transform:rotate(90deg) translateY(-100%);transform-origin:top left}
#bar{height:44px;flex:0 0 44px;display:flex;align-items:center;gap:10px;padding:0 12px;background:linear-gradient(#1b1e29,#0d0e12);border-bottom:4px solid var(--pc);box-shadow:0 2px 12px var(--pc);position:relative}
#dot{width:12px;height:12px;border-radius:50%;background:#fb0;flex:0 0 12px}
#status{font-size:13px;opacity:.85;flex:1;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
#badge{font-weight:800;font-size:18px;padding:2px 10px;border-radius:8px;background:var(--pc);color:#111;display:none}
#hud{font-weight:800;font-size:16px;flex:1;text-align:right;white-space:nowrap}
#held{position:absolute;left:50%;top:2px;transform:translateX(-50%);height:38px;min-width:120px;padding:0 12px;border-radius:10px;background:#2a2d36;border:2px solid #555;display:flex;align-items:center;justify-content:center;gap:6px;font-weight:800;font-size:14px}
#heldicon{font-size:26px;line-height:1}
#held.has{border-color:#ffd447;background:#3a3420}
.screen{flex:1;display:none;min-height:0}
.screen.on{display:flex}
button{font:inherit;color:inherit;border:0;cursor:pointer}
#lobby{flex-direction:row;align-items:center;justify-content:center;gap:14px;padding:10px}
#card{display:flex;align-items:center;gap:14px;background:var(--panel);border-radius:18px;padding:12px;border:3px solid var(--pc);flex:1.4;max-width:620px;height:100%;max-height:300px}
#cimgwrap{width:48%;height:100%;background:#2f333d;border-radius:12px;display:flex;align-items:center;justify-content:center;overflow:hidden}
#cimg{width:100%;height:100%;object-fit:contain}
#cinfo{flex:1}
#cname{font-size:22px;font-weight:800;margin-bottom:8px}
.stat{display:flex;align-items:center;gap:8px;font-size:13px;margin:4px 0}
.stat span{width:70px;opacity:.8}
.bars{display:flex;gap:3px}.bars i{width:16px;height:10px;border-radius:3px;background:#444}.bars i.f{background:var(--pc)}
#lobbyctl{flex:1;display:flex;flex-direction:column;gap:10px;max-width:360px;height:100%;max-height:300px}
.row{display:flex;gap:10px;flex:1}
.big{flex:1;border-radius:16px;font-size:22px;font-weight:900;background:#343844;border:3px solid #4a5060;border-bottom-width:7px;text-shadow:0 2px 0 #0008}
.big:active{filter:brightness(1.4)}
#ready.r{background:#2e9d46}
#start{background:#d9822b;display:none}
#tip{font-size:12px;opacity:.7;text-align:center}
#race{gap:10px;padding:10px}
#steer{flex:1;display:flex;gap:10px}
#pedals{flex:1.15;display:grid;grid-template-columns:1fr 1.35fr;grid-template-rows:1fr 1fr 1fr;gap:10px}
.pad{border-radius:22px;background:#2b2f39;display:flex;align-items:center;justify-content:center;font-size:24px;font-weight:900;border:3px solid #3a3f4b;border-bottom-width:8px;text-align:center;text-shadow:0 2px 0 #0008;box-shadow:0 6px 14px #0006}
#steer .pad{flex:1;font-size:64px}
.pad.on{background:var(--pc)!important;color:#111;border-color:#fff;border-bottom-width:3px;transform:translateY(4px);box-shadow:0 0 22px var(--pc)}
#gas{grid-column:2;grid-row:1/4;background:#27432c;font-size:34px}
#item{grid-column:1;grid-row:1;background:#4a4220;flex-direction:column;gap:2px;font-size:15px}
#itemicon{font-size:30px;line-height:1}
#hop{grid-column:1;grid-row:2;background:#3d3552;font-size:17px;line-height:1.1}
#brake{grid-column:1;grid-row:3;background:#4a2a2a;font-size:20px}
#results{flex-direction:column;align-items:center;justify-content:center;gap:16px;text-align:center;padding:16px}
#resmsg{font-size:30px;font-weight:900}
#start2{background:#d9822b;display:none;max-width:420px;width:100%;flex:0 0 64px}
#countdown{position:fixed;left:0;right:0;top:40%;text-align:center;font-size:90px;font-weight:900;pointer-events:none;text-shadow:0 4px 12px #000;z-index:6}
#fsbtn{position:absolute;right:8px;bottom:8px;font-size:12px;padding:6px 10px;border-radius:8px;background:#333a;display:none}
</style>
</head>
<body>
<div id='app'>
<div id='bar'><div id='dot'></div><div id='badge'>P?</div><div id='status'>Connecting...</div>
  <div id='held'><span id='heldicon'>&#8212;</span><span id='heldname'>NO ITEM</span></div>
  <div id='hud'></div></div>

<div id='lobby' class='screen on'>
  <div id='card'>
    <div id='cimgwrap'><img id='cimg' alt=''></div>
    <div id='cinfo'>
      <div id='cname'>...</div>
      <div class='stat'><span>Speed</span><div class='bars' id='bs'></div></div>
      <div class='stat'><span>Accel</span><div class='bars' id='ba'></div></div>
      <div class='stat'><span>Handling</span><div class='bars' id='bh'></div></div>
    </div>
  </div>
  <div id='lobbyctl'>
    <div class='row'><button class='big' id='prev'>&#9664;</button><button class='big' id='next'>&#9654;</button></div>
    <div class='row'><button class='big' id='ready'>READY</button></div>
    <div class='row' id='startrow'><button class='big' id='start'>START RACE</button></div>
    <div id='tip'>Pick a racer, press READY. Tap HOP to jump, hold it while steering to drift.</div>
  </div>
</div>

<div id='race' class='screen'>
  <div id='steer'><div class='pad' data-k='l'>&#9664;</div><div class='pad' data-k='r'>&#9654;</div></div>
  <div id='pedals'>
    <div class='pad' id='item' data-k='i'><span id='itemicon'>&#8212;</span><span id='itemname'>ITEM</span></div>
    <div class='pad' id='hop' data-k='d'>HOP<br><small>hold = DRIFT</small></div>
    <div class='pad' id='brake' data-k='b'>BRAKE</div>
    <div class='pad' id='gas' data-k='g'>GAS</div>
  </div>
</div>

<div id='results' class='screen'>
  <div id='resmsg'>Race over</div>
  <button class='big' id='start2'>BACK TO LOBBY</button>
  <div id='tip2' style='opacity:.7'>Waiting for player 1...</div>
</div>
<div id='countdown'></div>
</div>

<script>
(function(){
var $=function(id){return document.getElementById(id);};
var ws=null,slot=-1,host=false,phase='lobby',ready=false,lastSent='',joined=false;
var keys={l:0,r:0,g:0,b:0,d:0,i:0};
var ICONS={banana:'🍌',turbo:'⚡',rocket:'🚀',shield:'🛡️'};
var NAMES={banana:'BANANA',turbo:'TURBO',rocket:'ROCKET',shield:'SHIELD'};
var ORDER=['banana','turbo','rocket','shield'],rollTimer=null,rollIndex=0;
var pid=null;
try{pid=localStorage.getItem('kartPid');}catch(e){}
if(!pid){pid=Math.random().toString(36).slice(2,10)+Date.now().toString(36);try{localStorage.setItem('kartPid',pid);}catch(e){}}

function buzz(ms){if(navigator.vibrate){try{navigator.vibrate(ms);}catch(e){}}}

// ---- landscape: fullscreen + orientation lock on first tap, CSS rotation fallback ----
function layout(){var portrait=window.innerHeight>window.innerWidth;$('app').classList.toggle('rot',portrait);}
window.addEventListener('resize',layout);
window.addEventListener('orientationchange',function(){setTimeout(layout,200);});
layout();
var triedLock=false;
function goLandscape(){
  if(triedLock)return;triedLock=true;
  var el=document.documentElement,req=el.requestFullscreen||el.webkitRequestFullscreen;
  var lock=function(){try{if(screen.orientation&&screen.orientation.lock){screen.orientation.lock('landscape').catch(function(){});}}catch(e){}setTimeout(layout,300);};
  try{
    if(req){var p=req.call(el);if(p&&p.then){p.then(lock).catch(lock);}else{lock();}}
    else lock();
  }catch(e){lock();}
}
document.addEventListener('touchstart',goLandscape,{passive:true});
document.addEventListener('mousedown',goLandscape);

function setStatus(t,c){$('status').textContent=t;$('dot').style.background=c;}
function send(t){if(ws&&ws.readyState===1){try{ws.send(t);}catch(e){}}}
function connect(){
  setStatus('Connecting...','#fb0');
  try{ws=new WebSocket('ws://'+location.host+'/ws');}catch(e){setTimeout(connect,1000);return;}
  ws.onopen=function(){setStatus('Connected','#3c3');lastSent='';send('hello|'+pid);};
  ws.onmessage=function(e){handle(e.data);};
  ws.onclose=function(){ws=null;joined=false;setStatus('Reconnecting...','#e33');setTimeout(connect,1000);};
  ws.onerror=function(){try{ws.close();}catch(e){}};
}
function bars(id,n){var h='';for(var i=1;i<=5;i++)h+='<i class='+(i<=n?'f':'')+'></i>';$(id).innerHTML=h;}
function show(){
  var s=(phase==='race'||phase==='countdown')?'race':(phase==='results'?'results':'lobby');
  ['lobby','race','results'].forEach(function(id){$(id).classList.toggle('on',id===s);});
  $('start').style.display=(host&&phase==='lobby')?'block':'none';
  $('start2').style.display=(host&&phase==='results')?'block':'none';
  $('tip2').style.display=(host&&phase==='results')?'none':'block';
  $('held').style.display=s==='race'?'flex':'none';
  if(s!=='race'){keys={l:0,r:0,g:0,b:0,d:0,i:0};paint();}
}
function showItem(type,rolling){
  if(rollTimer){clearInterval(rollTimer);rollTimer=null;}
  if(rolling){
    $('itemname').textContent='...';$('heldname').textContent='...';$('held').classList.add('has');
    rollTimer=setInterval(function(){rollIndex=(rollIndex+1)%4;var ic=ICONS[ORDER[rollIndex]];$('itemicon').textContent=ic;$('heldicon').textContent=ic;},80);
    buzz(15);
    return;
  }
  var icon=ICONS[type]||'—',name=NAMES[type]||'ITEM';
  $('itemicon').textContent=icon;$('itemname').textContent=name;
  $('heldicon').textContent=icon;$('heldname').textContent=ICONS[type]?name:'NO ITEM';
  $('held').classList.toggle('has',!!ICONS[type]);
  if(ICONS[type])buzz(40);
}
function handle(msg){
  var p=msg.split('|');
  switch(p[0]){
    case 'joined':
      slot=+p[1];host=p[3]==='1';joined=true;
      document.documentElement.style.setProperty('--pc',p[2]);
      $('badge').style.display='block';$('badge').textContent='P'+(slot+1);
      setStatus(host?'Connected - host':'Connected','#3c3');show();break;
    case 'full':setStatus('Lobby is full (4 players)','#e33');break;
    case 'wait':setStatus('Race in progress - join in the lobby','#fb0');break;
    case 'phase':phase=p[1];show();if(phase!=='countdown')$('countdown').textContent='';break;
    case 'count':$('countdown').textContent=p[1];if(p[1]==='GO!')setTimeout(function(){$('countdown').textContent='';},900);break;
    case 'pick':
      $('cimg').style.visibility='visible';$('cimg').src='/char/'+p[1]+'.png';
      ready=p[2]==='1';$('ready').classList.toggle('r',ready);$('ready').textContent=ready?'READY!':'READY';
      $('cname').textContent=p[3];bars('bs',+p[4]);bars('ba',+p[5]);bars('bh',+p[6]);break;
    case 'hud':$('hud').textContent=p[1]+' | Lap '+p[2];break;
    case 'result':$('resmsg').textContent=p[1];break;
    case 'item':showItem(p[1],p[2]==='1');break;
    case 'buzz':buzz(+p[1]||120);break;
  }
}
$('cimg').onerror=function(){this.style.visibility='hidden';};
function tap(id,fn){$(id).addEventListener('click',function(e){e.preventDefault();fn();});}
tap('prev',function(){send('pick|-1');});
tap('next',function(){send('pick|1');});
tap('ready',function(){send('ready|'+(ready?0:1));});
tap('start',function(){send('start');});
tap('start2',function(){send('start');});

function paint(){document.querySelectorAll('[data-k]').forEach(function(el){el.classList.toggle('on',!!keys[el.dataset.k]);});}
function sendState(force){
  var steer=keys.r-keys.l,thr=keys.g-keys.b,m='s|'+steer+'|'+thr+'|'+keys.d+'|'+keys.i;
  if(force||m!==lastSent){send(m);lastSent=m;}
}
// Touches are resolved to buttons with elementFromPoint, so this works with the CSS-rotated layout.
var race=$('race');
function touches(e){
  e.preventDefault();
  var next={l:0,r:0,g:0,b:0,d:0,i:0};
  for(var i=0;i<e.touches.length;i++){
    var t=e.touches[i],el=document.elementFromPoint(t.clientX,t.clientY),k=el&&el.closest?el.closest('[data-k]'):null;
    if(k)next[k.dataset.k]=1;
  }
  for(var key in next){if(next[key]&&!keys[key])buzz(8);}
  keys=next;paint();sendState(false);
}
['touchstart','touchmove','touchend','touchcancel'].forEach(function(ev){race.addEventListener(ev,touches,{passive:false});});
race.addEventListener('mousedown',function(e){var k=e.target.closest('[data-k]');if(k){keys[k.dataset.k]=1;paint();sendState(false);}});
window.addEventListener('mouseup',function(){keys={l:0,r:0,g:0,b:0,d:0,i:0};paint();sendState(false);});
document.addEventListener('visibilitychange',function(){if(document.hidden){keys={l:0,r:0,g:0,b:0,d:0,i:0};paint();sendState(true);}});
document.addEventListener('touchmove',function(e){e.preventDefault();},{passive:false});
document.addEventListener('gesturestart',function(e){e.preventDefault();});
document.addEventListener('contextmenu',function(e){e.preventDefault();});
document.addEventListener('dblclick',function(e){e.preventDefault();});
setInterval(function(){if(joined&&(phase==='race'||phase==='countdown'))sendState(true);},50);
setInterval(function(){if(joined&&phase!=='race'&&phase!=='countdown')send('ping');},2000);
connect();
})();
</script>
</body>
</html>";
}
