/// <summary>
/// 本机状态页（http://127.0.0.1:8765/admin/pairing，Windows 与 macOS 共用）：连接状态、这台电脑的检查清单、
/// 新手机连接确认、已允许的手机。只经 8765 回环提供，写操作受 LoopbackOriginGuard 同源校验；
/// 手机名等外部文本一律用 textContent 写入，不拼 HTML。页面只在打开时轮询，接收端不常驻任何界面。
/// </summary>
internal static class ReceiverStatusPage
{
    internal const string Html = """
<!doctype html><html lang="zh-CN"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>言渡 · 电脑接收端</title><style>
:root{color-scheme:light dark;--bg:#fff;--sf:#f5f5f3;--ln:#e2e1dc;--tx:#151515;--mu:#63625c;--ink:#151515;--onink:#fff;--okon:#62c793;--ok:#1e7a4c;--okbg:#e5f2ea;--warn:#8f5400;--warnbg:#faefd9;--rec:#bf3324}
@media (prefers-color-scheme:dark){:root{--bg:#141413;--sf:#1f1f1d;--ln:#34342f;--tx:#f1f0eb;--mu:#a6a59e;--ink:#f1f0eb;--onink:#141413;--ok:#62c793;--okbg:#1b3226;--warn:#e8ae52;--warnbg:#3a2c14;--rec:#f2806f}}
*{box-sizing:border-box}body{margin:0;background:var(--sf);color:var(--tx);font:15px/1.5 -apple-system,"PingFang SC","Microsoft YaHei UI",system-ui,sans-serif}
header{background:var(--bg);border-bottom:1px solid var(--ln)}.top{max-width:1000px;margin:0 auto;padding:16px 24px;display:flex;align-items:center;gap:12px}
.mark{width:36px;height:36px;border-radius:11px;background:var(--ink);color:var(--onink);display:flex;align-items:center;justify-content:center;flex:none}
h1{font-size:18px;margin:0}.sub{font-size:12px;color:var(--mu)}.grow{flex:1;min-width:0}
.chip{display:inline-flex;align-items:center;gap:6px;font-size:12px;padding:4px 10px;border-radius:12px;background:var(--okbg);color:var(--ok)}
.dot{width:7px;height:7px;border-radius:4px;display:inline-block;flex:none}
main{max-width:1000px;margin:0 auto;padding:28px 24px 48px;display:grid;grid-template-columns:minmax(0,1.15fr) minmax(0,1fr);gap:24px;align-items:start}
@media (max-width:760px){main{grid-template-columns:minmax(0,1fr)}}
.col{display:flex;flex-direction:column;gap:16px}
.hero{border-radius:20px;background:var(--ink);color:var(--onink);padding:20px;display:flex;flex-direction:column;gap:6px}
.hero.warn{background:var(--warnbg);color:var(--tx)}.hero .big{font-size:28px;line-height:36px;font-weight:700}.hero .line{font-size:13px;opacity:.8}
.panel{background:var(--bg);border:1px solid var(--ln);border-radius:20px;padding:18px;display:flex;flex-direction:column;gap:12px}
.panel.req{border:2px solid var(--ink)}.sec{font-size:12px;color:var(--mu);margin:0}
.list{border:1px solid var(--ln);border-radius:16px;overflow:hidden}.row{display:flex;align-items:center;gap:12px;padding:12px 14px;min-height:48px}
.row+.row{border-top:1px solid var(--ln)}.ic{width:28px;height:28px;border-radius:14px;display:flex;align-items:center;justify-content:center;flex:none;font-size:14px;font-weight:700}
.ic.ok{background:var(--okbg);color:var(--ok)}.ic.bad{background:var(--warnbg);color:var(--warn)}.ic.ph{background:var(--sf);color:var(--tx)}
.rt{flex:1;min-width:0;display:flex;flex-direction:column}.rt b{font-size:14px;font-weight:500}.rt span{font-size:12px;color:var(--mu)}
button,.btn{font:inherit;font-size:13px;height:36px;padding:0 14px;border-radius:18px;border:1px solid var(--ln);background:var(--bg);color:var(--tx);cursor:pointer;display:inline-flex;align-items:center;text-decoration:none;flex:none}
.ink{background:var(--ink);color:var(--onink);border-color:var(--ink);font-weight:500}.lg{height:48px;border-radius:24px;font-size:15px;flex:1;justify-content:center}
.code{display:flex;gap:10px;justify-content:center}.code span{width:56px;height:68px;border-radius:14px;background:var(--sf);display:flex;align-items:center;justify-content:center;font:700 36px ui-monospace,Menlo,Consolas,monospace}
.center{text-align:center}.hint{font-size:13px;color:var(--mu)}.empty{font-size:14px;color:var(--mu);padding:4px 2px}
</style></head><body>
<header><div class="top"><span class="mark" aria-hidden="true">●</span><div class="grow"><h1>言渡 · 电脑接收端</h1><div class="sub" id="who">正在读取…</div></div><span class="chip" id="run"><span class="dot" style="background:var(--ok)"></span>运行中</span></div></header>
<main>
<div class="col">
<section class="hero" id="hero" aria-live="polite"><span class="line" id="heroState">正在检查</span><div class="big" id="heroTitle">准备连接</div><div class="line" id="heroLine"></div></section>
<section class="panel"><p class="sec" id="checksTitle">这台电脑</p><div class="list" id="checks"></div></section>
</div>
<div class="col">
<section class="panel" id="request" aria-live="assertive"></section>
<section class="panel"><p class="sec">已允许的手机</p><div class="list" id="phones"></div></section>
</div>
</main>
<script>
const $=id=>document.getElementById(id);
const el=(tag,cls,text)=>{const e=document.createElement(tag);if(cls)e.className=cls;if(text!==undefined)e.textContent=text;return e;};
const get=p=>fetch(p).then(r=>r.ok?r.json():null).catch(()=>null);
const post=(p,b)=>fetch(p,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(b||{})}).then(r=>r.json().catch(()=>({})));
function row(kind,title,sub,action){const r=el('div','row');r.append(el('span','ic '+kind,kind==='ok'?'✓':kind==='bad'?'!':'▢'));const t=el('div','rt');t.append(el('b','',title),el('span','',sub||''));r.append(t);if(action)r.append(action);return r;}
function link(text,href,ink){const a=el('a','btn'+(ink?' ink':''),text);a.href=href;if(href.startsWith('http'))a.target='_blank';return a;}
function button(text,fn,ink){const b=el('button',ink?'ink':'',text);b.onclick=fn;return b;}
let shownPairing=null;
async function refresh(){
  const [h,cfg,st,cl,pr]=await Promise.all([get('/api/health'),get('/api/config/desktop'),get('/api/admin/pairing/status'),get('/api/admin/clients'),get('/api/admin/presence')]);
  if(!h){$('run').lastChild.textContent='未响应';return;}
  const mac=h.platform==='macos';
  $('who').textContent=(h.displayName||'')+' · '+(mac?'macOS':'Windows')+' · '+(h.version||'');
  $('checksTitle').textContent=mac?'这台 Mac':'这台电脑';
  const active=(pr&&pr.phones||[]).filter(p=>p.active);
  const audioOk=!!(h.audio&&h.audio.available);const eng=h.voiceEngine||{};const micOk=eng.virtualCableSelected!==false;
  const axOk=!mac||!!(h.input&&h.input.accessibilityTrusted);
  const hero=$('hero');const problems=!audioOk||!micOk||!axOk;
  hero.className='hero'+(problems?' warn':'');
  if(problems){$('heroState').textContent='还差一步';$('heroTitle').textContent=active.length?'手机连上了，但还不能说话':'还不能用手机说话';$('heroLine').textContent='把左下方标出的项目弄好即可。';}
  else if(active.length){$('heroState').textContent=h.audio.streaming?'● 正在接收声音':'● 接收中 · 等待说话';$('heroTitle').textContent='已连接 '+active.length+' 台手机';$('heroLine').textContent=active.map(p=>(p.label||p.device||'手机')+' · '+(p.transport==='usb'?'USB':'Wi-Fi')).join('，');}
  else{$('heroState').textContent='● 设备已就绪';$('heroTitle').textContent='等待手机连接';$('heroLine').textContent='手机打开言渡后会出现在这里。';}
  const checks=$('checks');checks.replaceChildren();
  checks.append(audioOk?row('ok','虚拟声卡',(h.audio.device||'')+' · 48 kHz'):row('bad','没找到虚拟声卡',mac?'需要安装 BlackHole 2ch，并设为 48 kHz':'需要安装 VB-CABLE，装完重启电脑',link('怎么安装',mac?'https://github.com/ExistentialAudio/BlackHole':'https://vb-audio.com/Cable/')));
  const engName=eng.displayName||'语音输入法';
  checks.append(micOk?row('ok','语音输入法',engName+(eng.microphone?' · 麦克风：'+eng.microphone:'')):row('bad',engName+' 的麦克风不是虚拟声卡','在 '+engName+' 设置里把麦克风改成 '+(mac?'BlackHole 2ch':'CABLE Output')));
  if(mac)checks.append(axOk?row('ok','辅助功能权限','已授权，手机可以发按键和输入法快捷键'):row('bad','辅助功能权限','未授权：手机发的按键和输入法快捷键都不会生效',link('打开系统设置','x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility',true)));
  const conn=cfg&&cfg.connection;if(conn){checks.append(row(conn.lanDiscovery?'ok':'bad','局域网发现',conn.lanDiscovery?'已开启 · 同一 Wi-Fi 的手机会看到这台电脑':'已关闭 · 可在手机「多电脑配置」里打开'));checks.append(row(conn.autoStart?'ok':'ph','开机启动',conn.autoStart?'已开启':'未开启 · 可在手机「多电脑配置」里打开'));}
  const req=$('request');const pending=st&&st.pending;
  if(pending&&shownPairing!==st.pairingId){shownPairing=st.pairingId;req.className='panel req';req.replaceChildren();
    const head=el('div','row');head.style.padding='0';head.append(el('span','ic ph','▢'));const ht=el('div','rt');ht.append(el('b','',(pending.clientLabel||'一台手机')+' 想连接'),el('span','',mac?'系统也弹了同样的确认框，任选一处确认':'托盘也会弹出确认窗口，任选一处确认'));head.append(ht);req.append(head);
    req.append(el('p','sec center','校验码'));const code=el('div','code');for(const d of String(st.checkCode||'----'))code.append(el('span','',d));req.append(code);
    req.append(el('div','hint center','手机上显示的数字相同再允许，不一样就拒绝'));
    const acts=el('div','row');acts.style.padding='0';acts.style.gap='12px';const id=st.pairingId;
    const no=button('拒绝',()=>post('/api/admin/pairing/deny',{pairingId:id}).then(refresh));no.className='lg';
    const yes=button('允许',()=>post('/api/admin/pairing/confirm',{pairingId:id}).then(refresh),true);yes.className='ink lg';
    acts.append(no,yes);req.append(acts);req.append(el('p','sec center','',));}
  else if(!pending&&shownPairing!==''){shownPairing='';req.className='panel';req.replaceChildren();req.append(el('p','sec','添加手机'));
    req.append(el('div','','手机和这台电脑连同一个 Wi-Fi，打开言渡，在首页点这台电脑，这里和系统会弹出确认。'));
    req.append(el('div','hint','隔着路由器时，在手机「添加电脑 → 输入地址」填这台电脑的局域网 IP。'));}
  const phones=$('phones');phones.replaceChildren();let count=0;
  for(const c of (cl&&cl.clients)||[]){if(c.revokedAtUtc)continue;count++;
    const live=active.find(p=>p.clientId===c.clientId||(c.legacy&&p.clientId==='legacy-shared'));
    if(c.legacy){phones.append(row('ph','旧版共享令牌','老版本手机或 USB 首次配对在用；全部手机升级后可关闭',button('关闭',()=>{if(confirm('关闭后，仍用旧令牌的手机要重新连接。确定关闭？'))post('/api/admin/legacy/revoke').then(refresh);})));continue;}
    phones.append(row('ph',c.label||c.clientId,(live?'正在连接 · ':'')+'允许于 '+(c.issuedAtUtc||'').replace('T',' ').slice(0,16),button('撤销',()=>{if(confirm('撤销后这台手机要重新连接。确定撤销？'))post('/api/admin/clients/revoke',{clientId:c.clientId}).then(refresh);})));}
  if(!count)phones.append(el('div','empty','还没有允许任何手机。'));
}
refresh();setInterval(refresh,1500);
</script></body></html>
""";
}
