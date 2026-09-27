namespace ChronosTimer.Control;

/// <summary>The browser remote served at <c>/</c>: big display, transport, modes, locate, command line and every setting.</summary>
public static class WebRemotePage
{
    public const string Html = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover">
<meta name="theme-color" content="#0b1416">
<title>Chronos Timer</title>
<style>
:root{--bg:#0b1416;--panel:#12201f;--line:#23403c;--fg:#e8f1ef;--mute:#8aa6a1;--acc:#2fd3b5;--warn:#ffb020;--crit:#ff4d4d;--end:#7a8c89}
*{box-sizing:border-box}
html,body{margin:0;background:var(--bg);color:var(--fg);font:15px/1.4 system-ui,-apple-system,Segoe UI,Roboto,sans-serif}
main{max-width:1100px;margin:0 auto;padding:12px 16px 40px}
header{display:flex;gap:8px;align-items:center;flex-wrap:wrap;color:var(--mute);font-size:13px}
header b{color:var(--fg);font-size:15px;margin-right:auto}
.chip{border:1px solid var(--line);border-radius:999px;padding:2px 10px}
.chip.on{border-color:var(--acc);color:var(--acc)}
#display{font:700 clamp(56px,16vw,190px)/1.05 ui-monospace,SFMono-Regular,Consolas,Menlo,monospace;text-align:center;letter-spacing:.02em;margin:18px 0 4px;font-variant-numeric:tabular-nums;white-space:nowrap;overflow:hidden}
#display.warning{color:var(--warn)}#display.critical,#display.overrun{color:var(--crit)}#display.ended{color:var(--end)}
#display.overrun{animation:blink 1s steps(2,start) infinite}@keyframes blink{to{opacity:.35}}
#detail{text-align:center;color:var(--mute);min-height:1.4em}
#bar{height:6px;background:var(--panel);border-radius:3px;margin:12px 0 18px;overflow:hidden}#bar i{display:block;height:100%;width:0;background:var(--acc);transition:width .2s linear}
.row{display:flex;gap:8px;flex-wrap:wrap;margin:8px 0}
button,select,input{font:inherit;color:var(--fg);background:var(--panel);border:1px solid var(--line);border-radius:10px;padding:10px 14px;min-height:44px}
button{cursor:pointer;flex:1 1 auto}button:hover{border-color:var(--acc)}button:active{transform:translateY(1px)}
button.primary{background:var(--acc);color:#04211b;border-color:var(--acc);font-weight:700}
button.sel{border-color:var(--acc);color:var(--acc)}
input{flex:1 1 180px;min-width:0}
.big button{font-size:18px;padding:14px}
section{background:var(--panel);border:1px solid var(--line);border-radius:14px;padding:12px 14px;margin:14px 0}
h2{font-size:14px;text-transform:uppercase;letter-spacing:.08em;color:var(--mute);margin:0 0 8px}
#out{font:13px/1.45 ui-monospace,Consolas,Menlo,monospace;white-space:pre-wrap;max-height:220px;overflow:auto;color:var(--mute);margin-top:8px}
details summary{cursor:pointer;color:var(--mute);font-weight:600;padding:4px 0}
table{width:100%;border-collapse:collapse}td{padding:6px 4px;border-top:1px solid var(--line);vertical-align:top}
td.n{font-family:ui-monospace,Consolas,Menlo,monospace;white-space:nowrap;width:1%}td.d{color:var(--mute);font-size:13px}
td input,td select{width:100%;min-height:36px;padding:6px 8px}
.k{color:var(--mute);font-size:12px}
@media (max-width:600px){.hide-s{display:none}}
</style>
</head>
<body>
<main>
<header><b>Chronos Timer</b><span id="mode" class="chip">—</span><span id="state" class="chip">—</span><span id="rate" class="chip">—</span><span id="ltc" class="chip">LTC</span><span id="conn" class="chip">offline</span></header>
<div id="display">--:--:--:--</div>
<div id="detail"></div>
<div id="bar"><i></i></div>

<div class="row big">
<button class="primary" data-c="toggle">▶︎ / ❚❚</button><button data-c="stop">■ Stop</button><button data-c="reset">⟲ Reset</button><button data-c="restart">↻ Restart</button>
</div>
<div class="row">
<button data-c="nudge -1s">−1 s</button><button data-c="nudge -1">−1 fr</button><button data-c="nudge +1">+1 fr</button><button data-c="nudge +1s">+1 s</button>
<button data-c="add -1m">−1 min</button><button data-c="add +1m">+1 min</button><button data-c="jam">Jam</button><button data-c="reverse">⇄ Reverse</button>
</div>
<div class="row" id="modes">
<button data-c="mode timecode" data-m="timecode">Timecode</button><button data-c="mode count-up" data-m="count-up">Count up</button><button data-c="mode count-down" data-m="count-down">Countdown</button><button data-c="mode time-of-day" data-m="time-of-day">Time of day</button><button data-c="mode chase" data-m="chase">Chase</button>
</div>
<form class="row" id="locf"><input id="loc" placeholder="Locate: 01:00:00:00 · 5:00 · 90s · 1h30m" autocomplete="off"><button>Locate</button></form>
<form class="row" id="durf"><input id="dur" placeholder="Countdown duration: 10:00 · 45m · 00:30" autocomplete="off"><button>Set duration</button></form>

<section>
<h2>Command</h2>
<form class="row" id="cmdf"><input id="cmd" placeholder="help · status · set level -18 · output on · speed 0.5 · locate 10:00:00:00" autocomplete="off"><button>Send</button></form>
<div id="out"></div>
<div class="k hide-s">Keys: Space play/pause · S stop · R reset · ←/→ ±1 frame · ↑/↓ ±1 s · M next mode · F full screen</div>
</section>

<section>
<details id="setd"><summary>All settings</summary><div id="settings">loading…</div></details>
</section>
</main>
<script>
const $=s=>document.querySelector(s);let ws=null,poll=null,lastStatus=null;
function log(t,ok=true){const o=$('#out');const line=document.createElement('div');line.textContent=t;if(!ok)line.style.color='var(--crit)';o.prepend(line);while(o.childNodes.length>60)o.lastChild.remove()}
async function send(c){if(ws&&ws.readyState===1){ws.send(c);return}try{const r=await fetch('api/command',{method:'POST',body:c});const j=await r.json();log(j.message,j.ok);refreshSettingsSoon()}catch(e){log('offline: '+e,false)}}
function show(s){lastStatus=s;const d=$('#display');d.textContent=s.display;d.className=s.phase;$('#detail').textContent=s.detail;
$('#mode').textContent=s.mode;$('#state').textContent=s.state;$('#state').className='chip'+(s.state==='running'?' on':'');$('#rate').textContent=s.rateName+' fps';
$('#ltc').className='chip'+(s.outputActive?' on':'');$('#ltc').textContent=s.outputActive?'LTC '+s.timecode:'LTC off';
$('#bar i').style.width=s.progress==null?'0':(s.progress*100).toFixed(1)+'%';
document.querySelectorAll('#modes button').forEach(b=>b.classList.toggle('sel',b.dataset.m===s.mode));document.title=s.display+' · Chronos'}
function connect(){try{ws=new WebSocket((location.protocol==='https:'?'wss://':'ws://')+location.host+'/ws')}catch(e){startPoll();return}
ws.onopen=()=>{$('#conn').textContent='live';$('#conn').className='chip on';stopPoll()};
ws.onmessage=e=>{const j=JSON.parse(e.data);if('ok' in j){log(j.message,j.ok);refreshSettingsSoon()}else show(j)};
ws.onclose=()=>{$('#conn').textContent='reconnecting';$('#conn').className='chip';ws=null;startPoll();setTimeout(connect,2000)}}
function startPoll(){if(poll)return;poll=setInterval(async()=>{try{const r=await fetch('api/status');if(r.status===401){location.href='/';return}show(await r.json())}catch(e){}},300)}
function stopPoll(){clearInterval(poll);poll=null}
document.querySelectorAll('button[data-c]').forEach(b=>b.onclick=()=>send(b.dataset.c));
$('#locf').onsubmit=e=>{e.preventDefault();const v=$('#loc').value.trim();if(v)send('locate '+v)};
$('#durf').onsubmit=e=>{e.preventDefault();const v=$('#dur').value.trim();if(v)send('set duration '+v)};
$('#cmdf').onsubmit=e=>{e.preventDefault();const v=$('#cmd').value.trim();if(v){log('› '+v);send(v);$('#cmd').value=''}};
document.addEventListener('keydown',e=>{if(e.target.matches('input,select,textarea')||e.ctrlKey||e.metaKey||e.altKey)return;
const k={' ':'toggle','s':'stop','r':'reset','m':'mode','ArrowLeft':'nudge -1','ArrowRight':'nudge +1','ArrowUp':'nudge +1s','ArrowDown':'nudge -1s'}[e.key];
if(e.key==='f'){document.fullscreenElement?document.exitFullscreen():document.documentElement.requestFullscreen?.();return}
if(k){e.preventDefault();send(k)}});
let setTimer=null;function refreshSettingsSoon(){if(!$('#setd').open)return;clearTimeout(setTimer);setTimer=setTimeout(loadSettings,250)}
async function loadSettings(){const list=await (await fetch('api/settings')).json();const box=$('#settings');box.textContent='';let cat=null,tb=null;
for(const s of list){if(s.category!==cat){cat=s.category;const h=document.createElement('h2');h.textContent=cat;h.style.marginTop='14px';box.append(h);const t=document.createElement('table');tb=document.createElement('tbody');t.append(tb);box.append(t)}
const tr=document.createElement('tr');const n=document.createElement('td');n.className='n';n.textContent=s.name;const v=document.createElement('td');const d=document.createElement('td');d.className='d';d.textContent=s.description+(s.unit?' ('+s.unit+')':'');
let ed;if(s.kind==='choice'||(s.options.length&&s.kind==='text')){ed=document.createElement('select');for(const o of s.options){const op=document.createElement('option');op.value=o.value;op.textContent=o.value+(o.description?' — '+o.description:'');ed.append(op)}if(![...ed.options].some(o=>o.value===s.value)){const op=document.createElement('option');op.value=s.value;op.textContent=s.value;ed.prepend(op)}ed.value=s.value}
else if(s.kind==='toggle'){ed=document.createElement('select');for(const o of ['on','off']){const op=document.createElement('option');op.value=o;op.textContent=o;ed.append(op)}ed.value=s.value}
else{ed=document.createElement('input');ed.value=s.value;ed.placeholder=s.accepts}
ed.title=s.accepts;ed.onchange=()=>send('set '+s.name+' "'+ed.value+'"');v.append(ed);tr.append(n,v,d);tb.append(tr)}}
$('#setd').ontoggle=()=>{if($('#setd').open)loadSettings()};
connect();startPoll();
</script>
</body>
</html>
""";
}
