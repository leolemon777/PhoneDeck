import { initUI, setVoiceState, setConnection, renderDevices, renderSharedDevices, setNotice, setPairingState, setPairingKind, closeDialog, pulse, setLevel } from './ui.js';
import { AudioCapture } from './audio.js';
import { VoiceSession, PhoneSocket, alternateEntries } from './session.js';

const read = (key, fallback) => { try { return JSON.parse(localStorage.getItem('yandu.web.' + key)) ?? fallback; } catch { return fallback; } };
const save = (key, value) => { try { localStorage.setItem('yandu.web.' + key, JSON.stringify(value)); } catch { } };
let mode = read('mode', 'tap'); if (!['tap', 'hold', 'shared'].includes(mode)) mode = 'tap';
let sharedIds = read('sharedIds', []); if (!Array.isArray(sharedIds)) sharedIds = [];
let snapshot = { targets: [], selectedTargetId: null }, connected = false, paired = false, pairing = false;
let pendingTarget = null, notice = '', level = 0, holdPointer = null, holdKey = null, wakeLock = null;
let wakeRequest = null, wakeGeneration = 0;
let authGeneration = 0;
let authKnown = false, authRequest = null, authRetryTimer = null, authRetries = 0, authError = false;
let applyingUpdate = false;
const pendingActions = new Set();
// 主电脑持续离线时，提示改用其他电脑自己的手机入口（各入口独立配对，不转发凭据）。
const ENTRY_FALLBACK_DELAY_MS = 6000;
let entryFallbackTimer = null;
const mic = document.getElementById('mic-button');
initUI();

const secure = window.isSecureContext && !!navigator.mediaDevices?.getUserMedia && !!window.AudioWorkletNode;
const capture = new AudioCapture();
const transport = new PhoneSocket({
  url: `${location.protocol === 'https:' ? 'wss:' : 'ws:'}//${location.host}/phone/socket`,
  state: state => {
    snapshot = state; paired = authKnown = true;
    // 记住其他电脑的手机入口：主电脑离线时页面仍可从缓存打开并提示改用它们。
    if (Array.isArray(state.entries)) save('entries', state.entries.filter(entry => typeof entry?.url === 'string'
      && new URL(entry.url).origin !== location.origin));
    render();
  },
  stopped: event => voice.remoteStopped(event.sessionId, event.reason || '电脑端已停止，手机已同步停录'),
  connection: state => {
    connected = state === 'connected';
    if (!connected && voice.busy) voice.fail(voice.current, '电脑连接中断，手机已停止录音');
    if (state === 'disconnected') void checkAuthorization({ connect: false });
    scheduleEntryFallback();
    render();
  }
});
const voice = new VoiceSession({ capture, transport,
  changed: operation => { if (operation?.phase === 'recording' || operation?.phase === 'sharing') void keepAwake(); else if (!operation) releaseAwake(); render(); },
  interrupted: message => { notice = message; setNotice(message, { tone: 'info' }); },
  level: value => { level = value; setLevel(value); }
});

function scheduleEntryFallback() {
  clearTimeout(entryFallbackTimer); entryFallbackTimer = null;
  if (connected) return;
  entryFallbackTimer = setTimeout(() => {
    const entries = alternateEntries(read('entries', []), location.origin);
    if (connected || voice.busy || !entries.length) return;
    setNotice(`主电脑暂时离线。可改用「${entries[0].name || '另一台电脑'}」的手机入口，首次需在那台电脑确认`,
      { tone: 'info', actionLabel: '打开备用入口', action: 'open-entry' });
  }, ENTRY_FALLBACK_DELAY_MS);
}
window.addEventListener('yandu:notice-action', event => {
  if (event.detail.action !== 'open-entry') return;
  const [entry] = alternateEntries(read('entries', []), location.origin);
  if (entry) location.assign(entry.url);
});

async function api(path, body, timeout = 10000) {
  const abort = new AbortController(), timer = setTimeout(() => abort.abort(), timeout);
  try {
    const response = await fetch('/phone/' + path, { method: body === undefined ? 'GET' : 'POST', credentials: 'same-origin',
      headers: body === undefined ? {} : { 'Content-Type': 'application/json' }, body: body === undefined ? undefined : JSON.stringify(body), signal: abort.signal, cache: 'no-store' });
    let value; try { value = await response.json(); } catch { value = {}; }
    if (!response.ok || value.ok === false) { const error = new Error(value.error || (response.status === 401 ? '请在电脑上重新连接这台手机' : '操作未完成，请重试')); error.status = response.status; throw error; }
    return value;
  } catch (error) { if (error.name === 'AbortError') throw new Error('连接超时，请检查电脑和网络后重试'); throw error; }
  finally { clearTimeout(timer); }
}

function render() {
  const targets = connected ? (snapshot.targets || []) : (snapshot.targets || []).map(item => ({ ...item,
    online: false, streaming: false, recording: false, error: '与主电脑断开，状态未知' }));
  const target = targets.find(item => item.id === snapshot.selectedTargetId);
  const operation = voice.current;
  const selectedShared = targets.filter(item => sharedIds.includes(item.id));
  const streaming = targets.filter(item => operation?.targetIds.includes(item.id) && item.streaming);
  const recording = streaming.filter(item => item.recording);
  const available = connected && paired && secure;
  const ready = mode === 'shared' ? selectedShared.some(item => item.online && item.audioReady) : target?.online && target?.audioReady;
  let title = mode === 'hold' ? '按住说话' : mode === 'shared' ? '开启共享' : '开始说话';
  let hint = mode === 'hold' ? '按下开始，松开结束' : mode === 'shared' ? '开启后，用各台电脑的快捷键说话' : '轻点开始，再点结束';
  let status = !authKnown ? '正在连接主电脑' : !paired ? '等待配对' : !connected ? '正在连接电脑' : target?.online ? '准备好了' : '选择一台在线电脑';
  let detail = target ? '' : '在电脑打开言渡，连接这台手机';
  if (operation) {
    if (operation.phase === 'starting') { title = '正在准备'; hint = mode === 'hold' ? '松开取消本次说话' : '再次点击可取消'; status = '麦克风与电脑连接中'; }
    else if (operation.phase === 'stopping') { title = '正在结束'; hint = '手机已停录，等待电脑确认'; status = '正在收尾'; }
    else if (operation.mode === 'shared') {
      title = '关闭共享'; hint = '先在电脑结束本段，再关闭手机供音'; status = recording.length ? recording.map(item => item.name).join('、') + ' 正在听写' : '共享已就绪，等待电脑触发';
      detail = `正在向 ${streaming.length}/${operation.targetIds.length} 台电脑供音`;
    } else { title = mode === 'hold' ? '松开结束' : '停止说话'; hint = '电脑端停止也会同步结束'; status = '正在说话'; }
  } else if (paired && connected && target?.online && !target?.audioReady && mode !== 'shared') {
    status = '电脑尚未准备好'; detail = target.error || '请检查电脑的语音引擎与音频设备';
  }
  if (!secure) { status = '需要安全连接'; detail = '请按电脑上的手机网页指引建立信任，再使用 Safari 或 Chrome 打开'; }
  setVoiceState({ state: operation?.phase || (!paired ? 'unpaired' : 'idle'), mode, title, hint, status, detail, level,
    canRecord: !!(operation ? operation.phase !== 'stopping' : available && ready && !pendingTarget && !applyingUpdate), canAct: !!(connected && target?.online && !pendingTarget && !applyingUpdate) });
  for (const button of document.querySelectorAll('[data-action]')) {
    if (pendingActions.has(button.dataset.action)) button.disabled = true;
    button.setAttribute('aria-busy', String(pendingActions.has(button.dataset.action)));
  }
  setPairingKind(paired ? 'peer' : 'gateway');
  setConnection({ name: target?.name || '连接你的电脑', label: pairing ? '等待电脑确认' : !authKnown ? '正在检查连接' : !paired ? '从电脑扫码连接' : !connected ? '重新连接中' : target?.online ? '已连接 · 局域网' : '当前电脑离线',
    state: connected && target?.online ? 'online' : 'offline', pending: !!pendingTarget });
  renderDevices(targets, { targetId: snapshot.selectedTargetId, localTargetId: snapshot.gatewayId, sharedIds, pendingId: pendingTarget, busy: voice.busy || !connected });
  renderSharedDevices(targets.filter(item => (operation?.mode === 'shared' ? operation.targetIds : sharedIds).includes(item.id)));
}

function start() {
  if (voice.busy || applyingUpdate) return;
  notice = ''; setNotice('');
  if (!connected || !paired || !secure) { setNotice('请先连接电脑并允许麦克风', { tone: 'error' }); return; }
  const targets = mode === 'shared' ? sharedIds : [snapshot.selectedTargetId];
  if (!targets.length || targets.some(id => !id)) { setNotice('先选择接收声音的电脑', { tone: 'info' }); return; }
  void voice.start(mode === 'shared' ? 'shared' : 'managed', targets);
}
mic.addEventListener('pointerdown', event => {
  if (mode !== 'hold' || event.button !== 0 || mic.disabled || voice.busy) return;
  event.preventDefault(); holdPointer = event.pointerId; mic.setPointerCapture(event.pointerId); start();
});
function releasePointer(event) {
  if (holdPointer !== event.pointerId) return;
  holdPointer = null; void voice.stop({ cancel: event.type === 'pointercancel' });
}
mic.addEventListener('pointerup', releasePointer);
mic.addEventListener('pointercancel', releasePointer);
mic.addEventListener('lostpointercapture', releasePointer);
mic.addEventListener('click', event => {
  if (mode === 'hold' && event.detail !== 0) return;
  if (voice.busy) void voice.stop(); else start();
});
mic.addEventListener('keydown', event => {
  if (mode !== 'hold' || ![' ', 'Enter'].includes(event.key)) return;
  event.preventDefault(); if (event.repeat || holdKey || voice.busy) return; holdKey = event.key; start();
});
mic.addEventListener('keyup', event => { if (holdKey !== event.key) return; event.preventDefault(); holdKey = null; void voice.stop(); });
mic.addEventListener('contextmenu', event => event.preventDefault());
window.addEventListener('blur', () => { if (holdPointer !== null || holdKey) { holdPointer = null; holdKey = null; void voice.stop({ cancel: true }); } });

window.addEventListener('yandu:mode', event => {
  if (voice.busy) { setNotice('先结束当前语音，再切换方式'); return; }
  if (!['tap', 'hold', 'shared'].includes(event.detail.mode)) return;
  mode = event.detail.mode; save('mode', mode); render();
});
window.addEventListener('yandu:target', async event => {
  if (voice.busy || pendingTarget) { setNotice('先结束当前语音，再切换电脑'); return; }
  pendingTarget = event.detail.id; render();
  try { const result = await transport.request('select', { targetId: pendingTarget }); snapshot.selectedTargetId = result.targetId; closeDialog('devices-dialog'); pulse('success'); }
  catch (error) { setNotice(error.message, { tone: 'error' }); }
  finally { pendingTarget = null; render(); }
});
window.addEventListener('yandu:shared-selection', event => {
  if (voice.busy) { setNotice('关闭共享后再修改共享电脑'); render(); return; }
  sharedIds = [...new Set(event.detail.ids)].filter(id => snapshot.targets.some(item => item.id === id)); save('sharedIds', sharedIds); render();
});
window.addEventListener('yandu:pair', async event => {
  if (voice.busy) { setPairingState({ error: '先结束语音，再添加电脑' }); return; }
  if (!paired) {
    setPairingState({ busy: true, message: '正在核对手机网页链接' });
    try {
      const url = new URL(event.detail.qrPayload || event.detail.code);
      if (url.origin !== location.origin || url.pathname !== '/phone/')
        throw new Error('链接地址与这个主屏幕入口不同。请用电脑生成的新链接打开网页，再添加到主屏幕');
      const raw = new URLSearchParams(url.hash.slice(1)).get('pair');
      if (!raw) throw new Error('请复制电脑「手机网页」生成的完整连接链接');
      await pairGateway(JSON.parse(raw));
      await checkAuthorization(); closeDialog('pairing-dialog');
      setPairingState({ busy: false, message: '手机已连接' });
    } catch (error) { setPairingState({ busy: false, error: error.message }); }
    return;
  }
  setPairingState({ busy: true, message: '请在那台电脑确认连接' });
  try {
    await api('api/peers', { qrPayload: event.detail.qrPayload || event.detail.code, host: event.detail.host }, 40000);
    snapshot = await api('api/state'); setPairingState({ busy: false, message: '电脑已添加，请选择输入目标或加入共享组' });
    closeDialog('pairing-dialog'); render(); pulse('success');
  } catch (error) { setPairingState({ busy: false, error: error.message }); }
});
window.addEventListener('yandu:remove', async event => {
  if (voice.busy || pendingTarget) { setNotice('先结束语音，再移除电脑'); return; }
  const id = event.detail.id;
  if (!id || id === snapshot.gatewayId) return;
  pendingTarget = id; render();
  try {
    snapshot = await api('api/peers/remove', { targetId: id });
    sharedIds = sharedIds.filter(value => value !== id); save('sharedIds', sharedIds);
    setNotice('电脑已从这台手机的列表移除'); pulse('success');
  } catch (error) { setNotice(error.message, { tone: 'error' }); }
  finally { pendingTarget = null; render(); }
});
window.addEventListener('yandu:action', async event => {
  const action = event.detail.action;
  if (!['goal', 'backspace', 'enter'].includes(action) || pendingActions.has(action) || !connected || pendingTarget) return;
  const button = document.querySelector(`[data-action="${action}"]`);
  pendingActions.add(action); if (button) delete button.dataset.feedback; render();
  try { await transport.request('input', { targetId: snapshot.selectedTargetId, action }); if (button) button.dataset.feedback = 'success'; pulse('success'); }
  catch (error) { if (button) button.dataset.feedback = 'error'; setNotice(error.message, { tone: 'error' }); pulse('error'); }
  finally { pendingActions.delete(action); render(); setTimeout(() => { if (button) delete button.dataset.feedback; }, 900); }
});

async function keepAwake() {
  if (wakeLock || wakeRequest || !navigator.wakeLock || document.visibilityState !== 'visible') return;
  const generation = wakeGeneration;
  const request = navigator.wakeLock.request('screen'); wakeRequest = request;
  try {
    const lock = await request;
    if (generation !== wakeGeneration || !voice.busy || document.visibilityState !== 'visible') { await lock.release(); return; }
    wakeLock = lock;
    lock.addEventListener('release', () => { if (wakeLock === lock) wakeLock = null; });
  } catch { }
  finally { if (wakeRequest === request) wakeRequest = null; }
}
function releaseAwake() { wakeGeneration++; const lock = wakeLock; wakeLock = null; wakeRequest = null; if (lock) void lock.release().catch(() => {}); }
async function checkAuthorization({ connect = true } = {}) {
  if (pairing || authRequest?.generation === authGeneration) return;
  clearTimeout(authRetryTimer); authRetryTimer = null;
  const generation = authGeneration;
  const request = { generation }; authRequest = request;
  try {
    const state = await api('api/state'); if (generation !== authGeneration) return;
    if (!transport.connected) snapshot = state; paired = authKnown = true; authRetries = 0;
    if (authError) { authError = false; setNotice('连接已恢复，点击话筒即可开始'); }
    if (connect && document.visibilityState === 'visible') { transport.enabled = true; transport.connect(); }
  }
  catch (error) {
    if (generation !== authGeneration) return;
    if (error.status === 401) {
      paired = false; authKnown = true; authRetries = 0; authError = false;
      snapshot = { targets: [], selectedTargetId: null }; transport.disconnect({ reconnect: false });
      setNotice('在电脑打开「手机网页」，扫码连接；主屏幕入口也可粘贴连接链接');
    } else {
      authError = true; setNotice('主电脑暂未连接，恢复后会自动重试', { actionLabel: '重试', action: 'retry' });
      if (document.visibilityState === 'visible') authRetryTimer = setTimeout(() => {
        authRetryTimer = null; void checkAuthorization({ connect });
      }, Math.min(8000, 500 * 2 ** authRetries++));
    }
  }
  finally { if (authRequest === request) authRequest = null; }
  render();
}
async function pairGateway(material) {
  if (!material || typeof material !== 'object' || !material.pairingId || !material.oneTimeMaterial)
    throw new Error('手机网页链接不完整，请从电脑重新复制');
  authGeneration++; clearTimeout(authRetryTimer); authRetryTimer = null; pairing = true; render();
  try {
    let clientId = read('clientId', null);
    if (!clientId || !/^[0-9a-f-]{36}$/i.test(clientId)) { clientId = crypto.randomUUID(); save('clientId', clientId); }
    const message = '请在电脑确认这台手机' + (material.checkCode ? `，核对码 ${material.checkCode}` : '');
    setNotice(message); setPairingState({ busy: true, message });
    await api('pair', { ...material, clientId, clientLabel: /iPhone|iPad/.test(navigator.userAgent) ? 'iPhone / iPad 网页' : '手机网页' }, 40000);
    paired = authKnown = true; authError = false; setNotice('连接成功，选择一种方式开始说话');
  } finally { pairing = false; setPairingState({ busy: false }); render(); }
}
async function boot() {
  render();
  const fragment = new URLSearchParams(location.hash.slice(1)), raw = fragment.get('pair');
  if (raw) {
    history.replaceState(null, '', location.pathname + location.search);
    try {
      await pairGateway(JSON.parse(raw));
    } catch (error) { setNotice(error.message, { tone: 'error' }); }
  }
  await checkAuthorization();
  if ('serviceWorker' in navigator && window.isSecureContext) {
    try {
      const registration = await navigator.serviceWorker.register('./sw.js', { scope: '/phone/' });
      const showUpdate = () => { if (registration.waiting) setNotice('新版界面已准备好，结束语音后可更新', { actionLabel: '更新', action: 'update' }); };
      showUpdate(); registration.addEventListener('updatefound', () => registration.installing?.addEventListener('statechange', showUpdate));
      navigator.serviceWorker.addEventListener('controllerchange', () => { if (applyingUpdate && !voice.busy) location.reload(); });
      window.addEventListener('yandu:notice-action', event => {
        if (event.detail.action !== 'update') return;
        if (voice.busy) { setNotice('先结束语音，再更新界面', { actionLabel: '更新', action: 'update' }); return; }
        if (registration.waiting) {
          applyingUpdate = true; setNotice('正在更新界面…'); render(); registration.waiting.postMessage('activate');
          setTimeout(() => { if (applyingUpdate) { applyingUpdate = false; showUpdate(); render(); } }, 5000);
        }
      });
    } catch { /* Live page remains usable; never claim that offline installation succeeded. */ }
  }
}
document.addEventListener('visibilitychange', () => {
  if (document.visibilityState === 'hidden') { clearTimeout(authRetryTimer); authRetryTimer = null; holdPointer = null; holdKey = null; void voice.stop({ cancel: true }); releaseAwake(); }
  else { void checkAuthorization(); }
});
window.addEventListener('pagehide', () => { clearTimeout(authRetryTimer); authRetryTimer = null; void voice.stop({ cancel: true }); transport.disconnect({ reconnect: false }); releaseAwake(); });
window.addEventListener('pageshow', event => { if (event.persisted) void checkAuthorization(); });
window.addEventListener('online', () => { if (document.visibilityState === 'visible') void checkAuthorization(); });
window.addEventListener('yandu:notice-action', event => { if (event.detail.action === 'retry') void checkAuthorization(); });
void boot();
