/// <summary>
/// The phone controller web page (served at GET /). Self-contained: no external scripts, fonts
/// or CDNs. Uses only single quotes so it can live in a C# verbatim string.
///
/// Layout: the page is always laid out in landscape. #app is sized in JS from the VISIBLE
/// viewport (window.visualViewport, so Safari's toolbars are excluded) and, when the phone is
/// held in portrait (or ?rotate=1), rotated 90 degrees with CSS. All sizes derive from one unit
/// (--u, set from the app's width/height), so every screen scales to fit without scrolling.
/// Safe-area insets are applied per side (mapped through the rotation).
///
/// Mock mode for testing without Unity (no WebSocket, fake state), e.g. opened as a local file:
///   ?mock=lobby&amp;leader=1   ?mock=lobby   ?mock=race&amp;item=rocket   ?mock=pause&amp;leader=1   ?mock=pause
///   ?mock=flyover   ?mock=intro   ?mock=results&amp;leader=1   ?mock=confirm   ?mock=disconnected
///   add &amp;rotate=1 to force the portrait CSS-rotation path.
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
<title>Kart Controller</title>
<style>
:root{--pc:#888;--panel:#23262e;--text:#f2f2f2;--u:4px}
*{box-sizing:border-box;-webkit-user-select:none;user-select:none;-webkit-touch-callout:none;-webkit-tap-highlight-color:transparent;touch-action:none}
html,body{margin:0;padding:0;width:100%;height:100%;overflow:hidden;overscroll-behavior:none;background:#12141c;color:var(--text);font-family:'Arial Black','Segoe UI Black',Impact,system-ui,sans-serif;letter-spacing:.3px}
body{position:fixed;inset:0}
#app{position:fixed;left:0;top:0;width:100px;height:100px;display:flex;flex-direction:column;overflow:hidden;font-size:calc(var(--u)*3.4);
  background:radial-gradient(circle at 50% 30%,#2a2f45 0%,#12141c 75%);
  padding:max(env(safe-area-inset-top),4px) max(env(safe-area-inset-right),4px) max(env(safe-area-inset-bottom),4px) max(env(safe-area-inset-left),4px)}
/* Rotated 90deg clockwise: the app's top edge is the screen's right edge, etc. */
#app.rot{transform-origin:top left;transform:rotate(90deg) translateY(-100%);
  padding:max(env(safe-area-inset-right),4px) max(env(safe-area-inset-bottom),4px) max(env(safe-area-inset-left),4px) max(env(safe-area-inset-top),4px)}
#probe{position:fixed;left:0;top:0;width:0;height:0;visibility:hidden;padding:env(safe-area-inset-top) env(safe-area-inset-right) env(safe-area-inset-bottom) env(safe-area-inset-left)}
.dbg button,.dbg .pad,.dbg .arr{outline:2px dashed #0f0;outline-offset:-2px}
.dbg .bad{outline:3px solid #f00!important}
#fsbtn{display:none;flex:0 0 auto;height:max(calc(var(--u)*6.2),26px);padding:0 calc(var(--u)*1.6);border-radius:calc(var(--u)*1.4);background:#343844;border:calc(var(--u)*.4) solid #666;font-size:calc(var(--u)*2.4)}
#frame{position:relative;flex:1 1 0;min-height:0;display:flex;flex-direction:column;overflow:hidden}
button{font:inherit;color:inherit;border:0;cursor:pointer;padding:0;min-width:0;min-height:0}
#bar{flex:0 0 max(calc(var(--u)*8),32px);display:flex;align-items:center;gap:calc(var(--u)*1.6);padding:0 calc(var(--u)*2);background:linear-gradient(#1b1e29,#0d0e12);border-bottom:calc(var(--u)*.6) solid var(--pc);position:relative;min-height:0}
#dot{width:calc(var(--u)*2.2);height:calc(var(--u)*2.2);border-radius:50%;background:#fb0;flex:0 0 auto}
#badge{font-size:calc(var(--u)*3.6);padding:0 calc(var(--u)*1.4);border-radius:calc(var(--u)*1.2);background:var(--pc);color:#111;display:none;flex:0 0 auto}
#status{font-size:calc(var(--u)*2.5);opacity:.85;flex:1 1 0;min-width:0;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
#hud{font-size:calc(var(--u)*3);flex:0 1 auto;white-space:nowrap}
#held{position:absolute;left:50%;top:50%;transform:translate(-50%,-50%);height:calc(var(--u)*6.4);padding:0 calc(var(--u)*2);border-radius:calc(var(--u)*1.4);background:#2a2d36;border:calc(var(--u)*.4) solid #555;display:none;align-items:center;gap:calc(var(--u)*1);font-size:calc(var(--u)*2.6);white-space:nowrap}
#heldicon{font-size:calc(var(--u)*4.4);line-height:1}
#held.has{border-color:#ffd447;background:#3a3420}
#pausebtn{display:none;flex:0 0 auto;width:max(calc(var(--u)*8),30px);height:max(calc(var(--u)*6.2),26px);border-radius:calc(var(--u)*1.4);background:#343844;border:calc(var(--u)*.4) solid #666;font-size:calc(var(--u)*3)}
.screen{flex:1 1 0;min-height:0;display:none}
.screen.on{display:flex}
.big{border-radius:calc(var(--u)*2.4);font-size:calc(var(--u)*4);background:#343844;border:calc(var(--u)*.5) solid #4a5060;border-bottom-width:calc(var(--u)*1.2);text-shadow:0 2px 0 #0008;min-height:0}
.big:active,.arr:active{filter:brightness(1.4)}
.arr{flex:0 0 max(calc(var(--u)*6),26px);align-self:stretch;border-radius:calc(var(--u)*1.6);background:#343844;border:calc(var(--u)*.4) solid #4a5060;font-size:calc(var(--u)*3.6)}
.card{background:var(--panel);border-radius:calc(var(--u)*2.4);border:calc(var(--u)*.4) solid #3a3f4b;display:flex;align-items:center;gap:calc(var(--u)*1);padding:calc(var(--u)*1);min-height:0;min-width:0}
.lbl{font-size:calc(var(--u)*2.2);opacity:.7;white-space:nowrap}
.val{font-size:calc(var(--u)*3.4);white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.sub{font-size:calc(var(--u)*2.3);opacity:.8;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.info{flex:1 1 0;min-width:0;overflow:hidden;display:flex;flex-direction:column;justify-content:center;gap:calc(var(--u)*.6)}
/* ---- lobby: left = racer + READY, right = settings + START/EXIT ---- */
#lobby{flex-direction:row;gap:calc(var(--u)*2);padding:calc(var(--u)*2)}
.col{flex:1 1 0;min-width:0;min-height:0;display:flex;flex-direction:column;gap:calc(var(--u)*1.6)}
#racer{flex:1 1 0;border-color:var(--pc)}
#cname{font-size:calc(var(--u)*2.6)}
#cimgwrap{flex:0 1 auto;height:70%;aspect-ratio:1;max-width:30%;background:#2f333d;border-radius:calc(var(--u)*1.6);overflow:hidden;display:flex;align-items:center;justify-content:center}
#cimg{width:100%;height:100%;object-fit:contain}
.stat{display:flex;flex-direction:column;align-items:flex-start;gap:calc(var(--u)*.3);font-size:calc(var(--u)*1.9)}
.stat span{opacity:.8;line-height:1}
.bars{display:flex;gap:calc(var(--u)*.5)}.bars i{width:calc(var(--u)*2.4);height:calc(var(--u)*1.3);border-radius:calc(var(--u)*.5);background:#444}.bars i.f{background:var(--pc)}
#ready{flex:0 0 26%}
#ready.r{background:#2e9d46}
#trackset{flex:1 1 0}
#timgwrap{flex:0 1 auto;height:100%;max-height:calc(var(--u)*17);max-width:30%;aspect-ratio:1.6;border-radius:calc(var(--u)*1.2);overflow:hidden;background:#2f333d}
#timg{width:100%;height:100%;object-fit:cover;display:block}
#cpuset{flex:0 0 calc(var(--u)*11)}
#tname,#cname{white-space:normal;display:-webkit-box;-webkit-line-clamp:2;-webkit-box-orient:vertical;line-height:1.1;word-break:break-word}
#tname{font-size:calc(var(--u)*2.8)}
#hostrow{flex:0 0 26%;display:flex;gap:calc(var(--u)*1.6)}
#start{flex:1 1 0;background:#d9822b;display:none}
#exitbtn{flex:0 0 calc(var(--u)*16);background:#5a2630;border-color:#8a3a48;font-size:calc(var(--u)*2.8);display:none}
#tip{flex:1 1 0;font-size:calc(var(--u)*2.2);opacity:.65;display:flex;align-items:center;text-align:center;justify-content:center}
.host .arr.h{display:block}
.arr.h{display:none}
/* ---- race ---- */
#race{gap:calc(var(--u)*1.8);padding:calc(var(--u)*1.8)}
#steer{flex:1 1 0;display:flex;gap:calc(var(--u)*1.8);min-width:0}
#pedals{flex:1.15 1 0;display:grid;grid-template-columns:1fr 1.35fr;grid-template-rows:1fr 1fr 1fr;gap:calc(var(--u)*1.8);min-width:0;min-height:0}
.pad{border-radius:calc(var(--u)*3.6);background:#2b2f39;display:flex;align-items:center;justify-content:center;font-size:calc(var(--u)*4);border:calc(var(--u)*.5) solid #3a3f4b;border-bottom-width:calc(var(--u)*1.4);text-align:center;text-shadow:0 2px 0 #0008;min-height:0;min-width:0;overflow:hidden}
#steer .pad{flex:1 1 0;font-size:calc(var(--u)*10)}
.pad.on{background:var(--pc)!important;color:#111;border-color:#fff;border-bottom-width:calc(var(--u)*.5);transform:translateY(calc(var(--u)*.8))}
#gas{grid-column:2;grid-row:1/4;background:#27432c;font-size:calc(var(--u)*6)}
#item{grid-column:1;grid-row:1;background:#4a4220;flex-direction:column;font-size:calc(var(--u)*2.6)}
#itemicon{font-size:calc(var(--u)*5);line-height:1}
#hop{grid-column:1;grid-row:2;background:#3d3552;font-size:calc(var(--u)*3);line-height:1.1}
#hop small{font-size:calc(var(--u)*2)}
#brake{grid-column:1;grid-row:3;background:#4a2a2a;font-size:calc(var(--u)*3.4)}
/* ---- results ---- */
#results{flex-direction:column;align-items:center;justify-content:center;gap:calc(var(--u)*3);text-align:center;padding:calc(var(--u)*3)}
#resmsg{font-size:calc(var(--u)*6)}
#start2{display:none;background:#d9822b;width:60%;flex:0 0 calc(var(--u)*12)}
#tip2{font-size:calc(var(--u)*2.8);opacity:.7}
#countdown{position:absolute;left:0;right:0;top:40%;text-align:center;font-size:calc(var(--u)*16);pointer-events:none;text-shadow:0 4px 12px #000;z-index:6}
/* ---- overlays (inside #app, below the bar) ---- */
.over{position:absolute;left:0;right:0;bottom:0;top:max(calc(var(--u)*8),32px);display:none;flex-direction:column;align-items:center;justify-content:center;gap:calc(var(--u)*2);background:#0d0f16ee;z-index:8;text-align:center;padding:calc(var(--u)*2.5)}
.over.on{display:flex}
.otitle{font-size:calc(var(--u)*6.4);color:#ffd447;text-shadow:0 3px 0 #000}
.osub{font-size:calc(var(--u)*2.8);opacity:.85}
.obtns{display:flex;gap:calc(var(--u)*2);width:70%;flex:0 0 calc(var(--u)*12)}
.obtns .big{flex:1 1 0}
#pmenu{display:flex;gap:calc(var(--u)*2);width:80%;flex:0 1 calc(var(--u)*38);min-height:0}
#plist{flex:1.5 1 0;display:flex;flex-direction:column;gap:calc(var(--u)*1)}
.pitem{flex:1 1 0;border-radius:calc(var(--u)*1.8);background:#2b2f39;border:calc(var(--u)*.4) solid #444a58;font-size:calc(var(--u)*3);min-height:0}
.pitem.sel{background:#ffd447;color:#111;border-color:#fff}
#pnav{flex:1 1 0;display:grid;grid-template-rows:1fr 1fr 1fr;gap:calc(var(--u)*1)}
#toast{position:absolute;left:50%;top:calc(var(--u)*9.5);transform:translateX(-50%);background:#5a2630;padding:calc(var(--u)*1) calc(var(--u)*2.4);border-radius:calc(var(--u)*1.6);font-size:calc(var(--u)*2.6);display:none;z-index:9;white-space:nowrap}
#mocktag{position:absolute;right:calc(var(--u)*1);bottom:calc(var(--u)*.6);font-size:calc(var(--u)*1.8);opacity:.5;z-index:10;display:none}
</style>
</head>
<body>
<div id='probe'></div>
<div id='app'><div id='frame'>
<div id='bar'><div id='dot'></div><div id='badge'>P?</div><div id='status'>Connecting...</div>
  <div id='held'><span id='heldicon'>&#8212;</span><span id='heldname'>NO ITEM</span></div>
  <div id='hud'></div><button id='fsbtn'>FULLSCREEN</button><button id='pausebtn' aria-label='Pause'>&#10074;&#10074;</button></div>

<div id='lobby' class='screen on'>
  <div class='col'>
    <div class='card' id='racer'>
      <button class='arr' id='prev'>&#9664;</button>
      <div id='cimgwrap'><img id='cimg' alt=''></div>
      <div class='info'>
        <div class='val' id='cname'>...</div>
        <div class='stat'><span>Speed</span><div class='bars' id='bs'></div></div>
        <div class='stat'><span>Accel</span><div class='bars' id='ba'></div></div>
        <div class='stat'><span>Handling</span><div class='bars' id='bh'></div></div>
      </div>
      <button class='arr' id='next'>&#9654;</button>
    </div>
    <button class='big' id='ready'>READY</button>
  </div>
  <div class='col' id='settings'>
    <div class='card' id='trackset'>
      <button class='arr h' id='tprev'>&#9664;</button>
      <div id='timgwrap'><img id='timg' alt=''></div>
      <div class='info'><div class='lbl'>TRACK</div><div class='val' id='tname'>...</div><div class='sub' id='tsub'></div></div>
      <button class='arr h' id='tnext'>&#9654;</button>
    </div>
    <div class='card' id='cpuset'>
      <button class='arr h' id='cprev'>&#9664;</button>
      <div class='info' style='align-items:center'><div class='lbl'>CPU RACERS</div><div class='val' id='cname2'>FILL TO 6</div></div>
      <button class='arr h' id='cnext'>&#9654;</button>
    </div>
    <div id='hostrow'><button class='big' id='start'>START RACE</button><button class='big' id='exitbtn'>EXIT</button>
      <div id='tip'>Pick a racer and press READY. The leader picks the track.</div></div>
  </div>
</div>

<div id='race' class='screen'>
  <div id='steer'><div class='pad' data-k='l'>&#9664;</div><div class='pad' data-k='r'>&#9654;</div></div>
  <div id='pedals'>
    <div class='pad' id='item' data-k='i'><span id='itemicon'>&#8212;</span><span id='itemname'>ITEM</span></div>
    <div class='pad' id='hop' data-k='d'><div>HOP<br><small>hold = DRIFT</small></div></div>
    <div class='pad' id='brake' data-k='b'>BRAKE</div>
    <div class='pad' id='gas' data-k='g'>GAS</div>
  </div>
</div>

<div id='results' class='screen'>
  <div id='resmsg'>Race over</div>
  <button class='big' id='start2'>BACK TO LOBBY</button>
  <div id='tip2'>Waiting for the leader...</div>
</div>
<div id='countdown'></div>

<div class='over' id='introover'><div class='otitle'>KART PARTY</div><div class='osub'>Starting...</div>
  <div class='obtns'><button class='big' id='skip1' style='display:none'>SKIP</button></div></div>
<div class='over' id='flyover'><div class='otitle'>Get ready!</div><div class='osub' id='flyname'></div>
  <div class='obtns'><button class='big' id='skip2' style='display:none'>SKIP</button></div></div>
<div class='over' id='confirm'><div class='otitle'>Quit the game?</div><div class='osub'>This closes Kart Party on the PC for everyone.</div>
  <div class='obtns'><button class='big' id='qno'>NO</button><button class='big' id='qyes' style='background:#8a3a48'>YES, QUIT</button></div></div>
<div class='over' id='pauseover'><div class='otitle' id='ptitle'>PAUSED</div><div class='osub' id='pby'></div>
  <div id='pmenu'><div id='plist'></div>
    <div id='pnav'><button class='big' id='pup'>&#9650;</button><button class='big' id='pok' style='background:#2e9d46'>SELECT</button><button class='big' id='pdown'>&#9660;</button></div></div></div>
<div class='over' id='discover'><div class='otitle' id='dtitle'>Connecting...</div><div class='osub'>Same Wi-Fi as the PC? Retrying every second.</div></div>
<div id='toast'></div>
<div id='mocktag'>MOCK</div>
</div></div>

<script>
(function(){
var $=function(id){return document.getElementById(id);};
var Q={};location.search.replace(/^\?/,'').split('&').forEach(function(kv){if(!kv)return;var p=kv.split('=');Q[decodeURIComponent(p[0])]=decodeURIComponent(p[1]||'1');});
var MOCK=Q.mock||null,FORCEROT=Q.rotate==='1';
var ws=null,slot=-1,host=false,phase='lobby',ready=false,lastSent='',joined=false,paused=false,intro=false,fly=false,connected=false;
var keys={l:0,r:0,g:0,b:0,d:0,i:0};
var ICONS={banana:'🍌',turbo:'⚡',rocket:'🚀',shield:'🛡️'};
var NAMES={banana:'BANANA',turbo:'TURBO',rocket:'ROCKET',shield:'SHIELD'};
var ORDER=['banana','turbo','rocket','shield'],rollTimer=null,rollIndex=0;
var PITEMS=['RESUME','RESTART RACE','BACK TO LOBBY','QUIT GAME'];
var pid=null;
try{pid=localStorage.getItem('kartPid');}catch(e){}
if(!pid){pid=Math.random().toString(36).slice(2,10)+Date.now().toString(36);try{localStorage.setItem('kartPid',pid);}catch(e){}}

function buzz(ms){if(navigator.vibrate){try{navigator.vibrate(ms);}catch(e){}}}

// ---- fit the VISIBLE viewport (visualViewport excludes Safari's bars), rotate in portrait ----
function layout(){
  var vv=window.visualViewport;
  var w=vv?vv.width:window.innerWidth,h=vv?vv.height:window.innerHeight;
  var ox=vv?vv.offsetLeft:0,oy=vv?vv.offsetTop:0;
  var rot=FORCEROT||h>w;
  var app=$('app'),W=rot?h:w,H=rot?w:h;
  app.classList.toggle('rot',rot);
  app.style.left=ox+'px';app.style.top=oy+'px';
  app.style.width=W+'px';app.style.height=H+'px';
  // One size unit: 100 units across the long side, ~56 across the short side (whichever is tighter).
  var u=Math.max(2.2,Math.min(W/100,H/56));
  document.documentElement.style.setProperty('--u',u.toFixed(2)+'px');
  window.scrollTo(0,0);
}
// Browsers report the new visible size late (toolbar animations, rotation): lay out now and again shortly after.
function relayout(){layout();requestAnimationFrame(layout);setTimeout(layout,120);setTimeout(layout,400);}
window.addEventListener('resize',relayout);
window.addEventListener('orientationchange',relayout);
if(window.visualViewport){window.visualViewport.addEventListener('resize',relayout);window.visualViewport.addEventListener('scroll',layout);}
setInterval(layout,1000); // cheap safety net: toolbars can change without events
layout();
var triedLock=false;
function goLandscape(){
  if(triedLock||MOCK)return;triedLock=true;
  var el=document.documentElement,req=el.requestFullscreen||el.webkitRequestFullscreen;
  var lock=function(){try{if(screen.orientation&&screen.orientation.lock){screen.orientation.lock('landscape').catch(function(){});}}catch(e){}setTimeout(layout,300);};
  try{if(req){var p=req.call(el);if(p&&p.then){p.then(lock).catch(lock);}else{lock();}}else lock();}catch(e){lock();}
}
document.addEventListener('touchstart',goLandscape,{passive:true});
document.addEventListener('mousedown',goLandscape);
function isFs(){return !!(document.fullscreenElement||document.webkitFullscreenElement);}
var canFs=!!(document.documentElement.requestFullscreen||document.documentElement.webkitRequestFullscreen);
function fsChanged(){
  // Entering/leaving fullscreen (Android back button) resizes the viewport: lay out again, re-offer the button.
  layout();setTimeout(layout,300);
  if(!isFs()){triedLock=false;if(canFs&&!MOCK)toast('Tap FULLSCREEN for more room');}
  $('fsbtn').style.display=(canFs&&!isFs()&&(!MOCK||Q.debug==='1'))?'block':'none';
}
document.addEventListener('fullscreenchange',fsChanged);
document.addEventListener('webkitfullscreenchange',fsChanged);
$('fsbtn').style.display=(canFs&&!isFs()&&(!MOCK||Q.debug==='1'))?'block':'none';
$('fsbtn').addEventListener('click',function(e){e.preventDefault();triedLock=false;goLandscape();});

function setStatus(t,c){$('status').textContent=t;$('dot').style.background=c;}
function send(t){if(MOCK){mockSend(t);return;}if(ws&&ws.readyState===1){try{ws.send(t);}catch(e){}}}
function toast(t){var el=$('toast');el.textContent=t;el.style.display='block';clearTimeout(toast.h);toast.h=setTimeout(function(){el.style.display='none';},1600);}
function connect(){
  setStatus('Connecting...','#fb0');
  try{ws=new WebSocket('ws://'+location.host+'/ws');}catch(e){setTimeout(connect,1000);return;}
  ws.onopen=function(){connected=true;setStatus('Connected','#3c3');lastSent='';send('hello|'+pid);show();};
  ws.onmessage=function(e){handle(e.data);};
  ws.onclose=function(){ws=null;joined=false;connected=false;setStatus('Reconnecting...','#e33');show();setTimeout(connect,1000);};
  ws.onerror=function(){try{ws.close();}catch(e){}};
}
function bars(id,n){var h='';for(var i=1;i<=5;i++)h+='<i class='+(i<=n?'f':'')+'></i>';$(id).innerHTML=h;}
function show(){
  var s=(phase==='race'||phase==='countdown')?'race':(phase==='results'?'results':'lobby');
  ['lobby','race','results'].forEach(function(id){$(id).classList.toggle('on',id===s);});
  $('settings').classList.toggle('host',host);
  $('start').style.display=(host&&phase==='lobby')?'block':'none';
  $('exitbtn').style.display=(host&&phase==='lobby')?'block':'none';
  $('tip').style.display=host?'none':'flex';
  $('start2').style.display=(host&&phase==='results')?'block':'none';
  $('tip2').style.display=(host&&phase==='results')?'none':'block';
  $('held').style.display=s==='race'?'flex':'none';
  $('status').style.visibility=s==='race'&&connected?'hidden':'visible'; // the item pill sits there while racing
  $('pausebtn').style.display=(host&&phase!=='lobby'&&!paused)?'block':'none';
  $('introover').classList.toggle('on',intro);
  $('skip1').style.display=host?'block':'none';
  $('flyover').classList.toggle('on',fly&&!paused);
  $('skip2').style.display=host?'block':'none';
  if(phase!=='lobby')$('confirm').classList.remove('on');
  $('discover').classList.toggle('on',!connected);
  $('dtitle').textContent=joined||lastSent?'Reconnecting...':'Connecting...';
  if(s!=='race'||paused){keys={l:0,r:0,g:0,b:0,d:0,i:0};paint();}
}
function showItem(type,rolling){
  if(rollTimer){clearInterval(rollTimer);rollTimer=null;}
  if(rolling){
    $('itemname').textContent='...';$('heldname').textContent='...';$('held').classList.add('has');
    rollTimer=setInterval(function(){rollIndex=(rollIndex+1)%4;var ic=ICONS[ORDER[rollIndex]];$('itemicon').textContent=ic;$('heldicon').textContent=ic;},80);
    buzz(15);return;
  }
  var icon=ICONS[type]||'—',name=NAMES[type]||'ITEM';
  $('itemicon').textContent=icon;$('itemname').textContent=name;
  $('heldicon').textContent=icon;$('heldname').textContent=ICONS[type]?name:'NO ITEM';
  $('held').classList.toggle('has',!!ICONS[type]);
  if(ICONS[type])buzz(40);
}
function showPause(p){
  paused=p[1]==='1';
  $('pauseover').classList.toggle('on',paused);
  if(paused){
    var lead=p[3]==='1',sel=+p[4],confirm=p[5]==='1',csel=+p[6];
    var items=confirm?['NO, GO BACK','YES, QUIT']:PITEMS,cur=confirm?csel:sel;
    $('ptitle').textContent=confirm?'Quit the game?':'PAUSED';
    $('pby').textContent=lead?(confirm?'The game closes on the PC.':'You are the leader: choose'):'PAUSED by '+p[2]+' - waiting for the leader';
    $('pmenu').style.display=lead?'flex':'none';
    var h='';for(var i=0;i<items.length;i++)h+='<button class=\'pitem'+(i===cur?' sel':'')+'\' data-i=\''+i+'\'>'+items[i]+'</button>';
    $('plist').innerHTML=h;
    Array.prototype.forEach.call($('plist').children,function(b){b.addEventListener('click',function(e){e.preventDefault();
      if(confirm){send(+b.dataset.i===1?'menu|pick|1':'menu|back');}else send('menu|pick|'+b.dataset.i);});});
    buzz(20);
  }
  show();
}
function handle(msg){
  var p=msg.split('|');
  switch(p[0]){
    case 'joined':
      slot=+p[1];host=p[3]==='1';joined=true;
      document.documentElement.style.setProperty('--pc',p[2]);
      $('badge').style.display='block';$('badge').textContent='P'+(slot+1);
      setStatus(host?'Connected - leader':'Connected','#3c3');show();break;
    case 'full':setStatus('Lobby is full (4 players)','#e33');break;
    case 'wait':setStatus('Race in progress - join in the lobby','#fb0');break;
    case 'phase':phase=p[1];if(phase!=='countdown'){$('countdown').textContent='';fly=false;}show();break;
    case 'count':$('countdown').textContent=p[1];if(p[1]==='GO!')setTimeout(function(){$('countdown').textContent='';},900);break;
    case 'pick':
      $('cimg').style.visibility='visible';$('cimg').src=MOCK?placeholder('#'+(+p[1]*37%9)+'8c',p[3].charAt(0)):'/char/'+p[1]+'.png';
      ready=p[2]==='1';$('ready').classList.toggle('r',ready);$('ready').textContent=ready?'READY!':'READY';
      $('cname').textContent=p[3];bars('bs',+p[4]);bars('ba',+p[5]);bars('bh',+p[6]);break;
    case 'track':
      var rnd=p[3]==='RANDOM';
      $('tname').textContent=p[3];
      $('tsub').textContent=rnd?'picked at the start':(p[4]+' m - '+p[6]+' laps - '+'★★★'.slice(0,+p[5])+'☆☆☆'.slice(0,3-(+p[5])));
      $('timg').style.visibility=rnd?'hidden':'visible';
      if(!rnd)$('timg').src=MOCK?placeholder('#6a4a2a','T'+(+p[1]+1)):'/track/'+p[1]+'.png';break;
    case 'cpu':$('cname2').textContent=p[2];break;
    case 'intro':intro=p[1]==='1';show();break;
    case 'flyover':fly=p[1]==='1';if(fly)$('flyname').textContent=p[2]+' - '+p[3]+' laps';show();break;
    case 'pause':showPause(p);break;
    case 'denied':toast('Only the leader can do that');break;
    case 'hud':$('hud').textContent=p[1]+' | Lap '+p[2];break;
    case 'result':$('resmsg').textContent=p[1];break;
    case 'item':showItem(p[1],p[2]==='1');break;
    case 'buzz':buzz(+p[1]||120);break;
  }
}
$('cimg').onerror=function(){this.style.visibility='hidden';};
$('timg').onerror=function(){this.style.visibility='hidden';};
function tap(id,fn){$(id).addEventListener('click',function(e){e.preventDefault();fn();});}
tap('prev',function(){send('pick|-1');});
tap('next',function(){send('pick|1');});
tap('ready',function(){send('ready|'+(ready?0:1));});
tap('start',function(){send('start');});
tap('start2',function(){send('start');});
tap('tprev',function(){send('track|-1');});
tap('tnext',function(){send('track|1');});
tap('cprev',function(){send('cpu|-1');});
tap('cnext',function(){send('cpu|1');});
tap('exitbtn',function(){$('confirm').classList.add('on');});
tap('qno',function(){$('confirm').classList.remove('on');});
tap('qyes',function(){$('confirm').classList.remove('on');send('quit|yes');});
tap('pausebtn',function(){send('pause');});
tap('pup',function(){send('menu|up');});
tap('pdown',function(){send('menu|down');});
tap('pok',function(){send('menu|ok');});
tap('skip1',function(){send('skip');});
tap('skip2',function(){send('skip');});

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
  if(paused){next={l:0,r:0,g:0,b:0,d:0,i:0};}
  keys=next;paint();sendState(false);
}
['touchstart','touchmove','touchend','touchcancel'].forEach(function(ev){race.addEventListener(ev,touches,{passive:false});});
race.addEventListener('mousedown',function(e){var k=e.target.closest('[data-k]');if(k&&!paused){keys[k.dataset.k]=1;paint();sendState(false);}});
window.addEventListener('mouseup',function(){keys={l:0,r:0,g:0,b:0,d:0,i:0};paint();sendState(false);});
document.addEventListener('visibilitychange',function(){if(document.hidden){keys={l:0,r:0,g:0,b:0,d:0,i:0};paint();sendState(true);}});
document.addEventListener('touchmove',function(e){e.preventDefault();},{passive:false});
document.addEventListener('gesturestart',function(e){e.preventDefault();});
document.addEventListener('contextmenu',function(e){e.preventDefault();});
document.addEventListener('dblclick',function(e){e.preventDefault();});

// ---- mock mode: fake state, no WebSocket (open the page as a local file with ?mock=...) ----
function placeholder(col,text){
  var c=document.createElement('canvas');c.width=160;c.height=100;var g=c.getContext('2d');
  g.fillStyle=col;g.fillRect(0,0,160,100);g.strokeStyle='#ffd447';g.lineWidth=8;g.strokeRect(24,18,112,64);
  g.fillStyle='#fff';g.font='bold 28px sans-serif';g.textAlign='center';g.fillText(text,80,62);return c.toDataURL();
}
var mockTrack=1,mockCpu=3,mockSel=0,mockConfirm=false,mockCsel=0;
var TRACKS=[['Night Circuit',809,2],['Sunset Grand Prix',1164,1],['Toy Box Hills',838,2],['Neon Night Loop',805,3]],CPUS=['OFF','2','4','FILL TO 6'];
function mockTrackMsg(){handle(mockTrack>=4?'track|4|5|RANDOM|0|0|0':'track|'+mockTrack+'|5|'+TRACKS[mockTrack][0]+'|'+TRACKS[mockTrack][1]+'|'+TRACKS[mockTrack][2]+'|3');}
function mockPause(){handle('pause|1|P1|'+(host?1:0)+'|'+mockSel+'|'+(mockConfirm?1:0)+'|'+mockCsel);}
function mockSend(t){
  var p=t.split('|');
  if(p[0]==='ready'){handle('pick|7|'+p[1]+'|Nova Nitro|4|3|3');}
  else if(p[0]==='track'){mockTrack=(mockTrack+(+p[1])+5)%5;mockTrackMsg();}
  else if(p[0]==='cpu'){mockCpu=(mockCpu+(+p[1])+4)%4;handle('cpu|'+mockCpu+'|'+CPUS[mockCpu]);}
  else if(p[0]==='pause'){mockSel=0;mockConfirm=false;mockPause();}
  else if(p[0]==='menu'){
    if(p[1]==='up'||p[1]==='down'){if(mockConfirm)mockCsel=p[1]==='down'?1:0;else mockSel=(mockSel+(p[1]==='down'?1:3))%4;}
    else if(p[1]==='back'){mockConfirm=false;}
    else{var i=p[1]==='pick'?+p[2]:(mockConfirm?mockCsel:mockSel);
      if(mockConfirm){if(i===1){toast('(mock) quit');}mockConfirm=false;}
      else if(i===3){mockConfirm=true;mockCsel=0;}else{handle('pause|0');show();toast('(mock) '+PITEMS[i]);return;}}
    mockPause();
  }
  else if(p[0]==='skip'){handle('intro|0');handle('flyover|0');}
  else if(p[0]!=='s')toast('(mock) sent '+t);
}
function startMock(){
  $('mocktag').style.display='block';
  host=Q.leader==='1'||Q.mock==='race';
  if(Q.leader==='0')host=false;
  connected=MOCK!=='disconnected';
  if(!connected){setStatus('Reconnecting...','#e33');show();return;}
  handle('joined|0|#E85550|'+(host?1:0));
  handle('pick|7|0|Nova Nitro|4|3|3');mockTrackMsg();handle('cpu|3|FILL TO 6');
  switch(MOCK){
    case 'race':handle('phase|race');handle('hud|2nd/6|2/3');handle('item|'+(Q.item||'rocket')+'|0');break;
    case 'countdown':handle('phase|countdown');handle('count|3');break;
    case 'flyover':handle('phase|countdown');handle('flyover|1|Sunset Grand Prix|3');break;
    case 'intro':handle('intro|1');break;
    case 'pause':handle('phase|race');handle('hud|2nd/6|2/3');handle('item|banana|0');mockPause();break;
    case 'results':handle('phase|results');handle('result|You finished 2nd!');break;
    case 'confirm':handle('phase|lobby');$('confirm').classList.add('on');break;
    default:handle('phase|lobby');
  }
}
// ---- ?debug=1: outline buttons, report any button outside the visible viewport minus insets ----
function insets(){var cs=getComputedStyle($('probe'));return {t:parseFloat(cs.paddingTop)||0,r:parseFloat(cs.paddingRight)||0,b:parseFloat(cs.paddingBottom)||0,l:parseFloat(cs.paddingLeft)||0};}
window.kartCheck=function(){
  var vv=window.visualViewport,w=vv?vv.width:innerWidth,h=vv?vv.height:innerHeight,ox=vv?vv.offsetLeft:0,oy=vv?vv.offsetTop:0,ins=insets(),m=2,bad=[];
  var els=document.querySelectorAll('button,.pad,.arr');
  for(var i=0;i<els.length;i++){
    var el=els[i];el.classList.remove('bad');
    var r=el.getBoundingClientRect();
    if(r.width<1||r.height<1||getComputedStyle(el).visibility==='hidden')continue; // not shown on this screen
    var ok=r.left>=ox+ins.l+m-0.5&&r.top>=oy+ins.t+m-0.5&&r.right<=ox+w-ins.r-m+0.5&&r.bottom<=oy+h-ins.b-m+0.5&&r.width>=20&&r.height>=20;
    if(!ok){el.classList.add('bad');bad.push((el.id||el.textContent.trim().slice(0,12)||el.className)+' ['+Math.round(r.left)+','+Math.round(r.top)+','+Math.round(r.right)+','+Math.round(r.bottom)+']');}
  }
  var sc=document.scrollingElement;var scroll=sc&&(sc.scrollHeight>sc.clientHeight+1||sc.scrollWidth>sc.clientWidth+1);
  if(scroll)bad.push('page can scroll');
  var res=(bad.length?'FAIL ':'OK ')+Math.round(w)+'x'+Math.round(h)+(document.getElementById('app').classList.contains('rot')?' rotated':'')+' mock='+MOCK+(bad.length?': '+bad.join('; '):'');
  if(bad.length)console.warn('[kartCheck] '+res);else console.log('[kartCheck] '+res);
  document.title=res;return res;
};
if(Q.debug==='1'){$('app').classList.add('dbg');setInterval(window.kartCheck,1000);if(window.visualViewport)window.visualViewport.addEventListener('resize',function(){setTimeout(window.kartCheck,50);});}
if(MOCK){startMock();}
else{
  setInterval(function(){if(joined&&!paused&&(phase==='race'||phase==='countdown'))sendState(true);},50);
  setInterval(function(){if(joined&&(paused||(phase!=='race'&&phase!=='countdown')))send('ping');},2000);
  show();connect();
}
})();
</script>
</body>
</html>";
}
