// Presentation only. Network state, capture, and target confirmation belong to app.js.
const $ = id => document.getElementById(id);
const activeStates = new Set(['starting', 'recording', 'sharing', 'stopping']);
const layouts = new Set(['center', 'dock', 'panel']);
const designThemes = { center: 'chat', dock: 'grok', panel: 'green' };
const modes = new Set(['tap', 'hold', 'shared']);
let initialized = false;
let haptics = true;
let toastTimer;
let lastFocus;
let meterFrame = 0;
let meterLevel = 0;
let lastMeterAnnouncement = 0;
let devicesSignature = null;
let sharedDevicesSignature = null;
let pairingKind = 'peer';
const deviceContent = device => [device.id, device.name, !!device.online, !!device.audioReady, !!device.streaming, !!device.recording, typeof device.error === 'string' ? device.error : ''];
const emit = (name, detail) => window.dispatchEvent(new CustomEvent(`yandu:${name}`, { detail }));
const readPreference = key => { try { return localStorage.getItem(`yandu.phone.${key}`); } catch { return null; } };
const savePreference = (key, value) => { try { localStorage.setItem(`yandu.phone.${key}`, value); } catch { /* Private browsing may not persist preferences. */ } };
const text = (id, value) => { if (value !== undefined && value !== null && $(id).textContent !== String(value)) $(id).textContent = String(value); };

function svg(name) {
  const icon = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
  icon.setAttribute('aria-hidden', 'true');
  const use = document.createElementNS('http://www.w3.org/2000/svg', 'use');
  use.setAttribute('href', `#i-${name}`);
  icon.append(use);
  return icon;
}
function element(tag, className, content) {
  const node = document.createElement(tag);
  if (className) node.className = className;
  if (content !== undefined) node.textContent = content;
  return node;
}
function showToast(message) {
  clearTimeout(toastTimer);
  text('ui-toast', message);
  $('ui-toast').hidden = false;
  toastTimer = setTimeout(() => { $('ui-toast').hidden = true; }, 2700);
}
export function pulse(kind = 'light') {
  if (!haptics || document.hidden || typeof navigator.vibrate !== 'function') return;
  try { navigator.vibrate(kind === 'error' ? [14, 45, 14] : kind === 'success' ? 12 : 7); } catch { /* Haptics are optional. */ }
}
export function openDialog(id) {
  const dialog = $(id);
  if (!dialog || typeof dialog.showModal !== 'function') return;
  const alreadyOpen = document.querySelector('dialog[open]');
  if (alreadyOpen === dialog) return;
  if (!alreadyOpen) lastFocus = document.activeElement;
  if (alreadyOpen) alreadyOpen.close();
  dialog.showModal();
  document.body.classList.add('is-dialog-open');
  // Avoid bringing up a mobile keyboard before the user chooses the text field.
  const close = dialog.querySelector('[data-close-dialog]');
  if (close) close.focus({ preventScroll: true });
}
export function closeDialog(id) {
  const dialog = id ? $(id) : document.querySelector('dialog[open]');
  if (dialog?.open) dialog.close();
}
function setAppearance(value) {
  if (!layouts.has(value)) return;
  document.body.dataset.layout = value;
  document.body.dataset.theme = designThemes[value];
  document.querySelectorAll('[data-layout-option]').forEach(button => button.setAttribute('aria-pressed', String(button.dataset.layoutOption === value)));
  savePreference('style', value);
  const scheme = getComputedStyle(document.body);
  document.querySelector('meta[name="theme-color"]').content = scheme.getPropertyValue('--bg').trim();
  document.querySelector('meta[name="apple-mobile-web-app-status-bar-style"]').content = scheme.colorScheme === 'dark' ? 'black-translucent' : 'default';
}
export function initUI() {
  if (initialized) return;
  initialized = true;
  // Retain old preferences for rollback; old colors never override a complete design.
  const style = readPreference('style');
  const legacyLayout = readPreference('layout');
  setAppearance(layouts.has(style) ? style : layouts.has(legacyLayout) ? legacyLayout : 'center');
  haptics = readPreference('haptics') !== 'off';
  $('haptics-toggle').checked = haptics;
  if (typeof navigator.vibrate !== 'function') {
    text('haptic-support', '当前浏览器不支持振动，按压动画仍可使用');
    $('haptics-toggle').disabled = true;
  }
  $('haptics-toggle').addEventListener('change', event => {
    haptics = event.target.checked;
    savePreference('haptics', haptics ? 'on' : 'off');
    if (haptics) pulse();
  });
  document.addEventListener('click', event => {
    const button = event.target.closest('button');
    if (!button || button.disabled) return;
    const open = button.dataset.openDialog;
    if (open) { pulse(); openDialog(open); return; }
    if (button.hasAttribute('data-close-dialog')) { pulse(); closeDialog(button.closest('dialog').id); return; }
    if (button.dataset.layoutOption) {
      if (activeStates.has(document.body.dataset.state)) { showToast('先结束语音，再换一个界面。'); return; }
      pulse(); setAppearance(button.dataset.layoutOption); return;
    }
    if (button.dataset.modeButton) { pulse(); emit('mode', { mode: button.dataset.modeButton }); return; }
    if (button.dataset.action) { pulse(); emit('action', { action: button.dataset.action }); return; }
    if (button.dataset.targetId) { pulse(); emit('target', { id: button.dataset.targetId }); return; }
    if (button.dataset.removeId) { pulse(); emit('remove', { id: button.dataset.removeId }); }
  });
  document.querySelectorAll('dialog').forEach(dialog => {
    dialog.addEventListener('close', () => {
      if (!document.querySelector('dialog[open]')) {
        document.body.classList.remove('is-dialog-open');
        if (lastFocus?.isConnected) lastFocus.focus({ preventScroll: true });
      }
    });
    dialog.addEventListener('click', event => {
      if (event.target !== dialog) return;
      const bounds = dialog.getBoundingClientRect();
      if (event.clientX < bounds.left || event.clientX > bounds.right || event.clientY < bounds.top || event.clientY > bounds.bottom) dialog.close();
    });
  });
  $('devices-list').addEventListener('change', event => {
    if (!event.target.matches('[data-shared-id]')) return;
    // The checkbox changes before the controller confirms. A rejected change must redraw it.
    devicesSignature = null;
    pulse();
    const ids = [...$('devices-list').querySelectorAll('[data-shared-id]:checked')].map(input => input.dataset.sharedId);
    emit('shared-selection', { ids });
  });
  $('pairing-form').addEventListener('submit', event => {
    event.preventDefault();
    if ($('pairing-submit').disabled) return;
    emit('pair', { qrPayload: $('pairing-code').value.trim(), host: pairingKind === 'gateway' ? '' : $('pairing-host').value.trim(), kind: pairingKind });
  });
  $('notice-action').addEventListener('click', () => emit('notice-action', { action: $('notice-action').dataset.actionName }));
  // A header link must never discard a live microphone session.
  document.querySelector('.wordmark').addEventListener('click', event => {
    event.preventDefault();
    if (document.querySelector('dialog[open]')) closeDialog();
    else window.scrollTo({ top: 0, behavior: 'smooth' });
  });
}
export function setConnection({ name, label, state = 'offline', pending = false } = {}) {
  text('target-name', name ?? '连接你的电脑');
  text('connection-label', label ?? (state === 'online' ? '已连接' : '配对后，即可开始说话'));
  $('connection-dot').dataset.status = pending ? 'pending' : state;
  $('target-button').setAttribute('aria-busy', String(pending));
}
export function setVoiceState({ state, mode, title, hint, status, detail, level, canRecord, canAct } = {}) {
  if (state) document.body.dataset.state = state;
  if (modes.has(mode)) document.body.dataset.mode = mode;
  const currentState = document.body.dataset.state;
  const currentMode = document.body.dataset.mode;
  const active = activeStates.has(currentState);
  const busy = currentState === 'starting' || currentState === 'stopping';
  const defaults = {
    unpaired: ['先连接一台电脑', '把声音，送到你正在工作的地方。', '等待连接', '麦克风尚未开启'],
    idle: currentMode === 'hold' ? ['按住说话', '按下开始，松开结束', '准备好了', ''] : currentMode === 'shared' ? ['开启共享', '开启后，用各台电脑的快捷键说话', '共享尚未开启', ''] : ['开始说话', '轻点开始，再点结束', '准备好了', ''],
    starting: ['正在连接声音', '准备好了，就可以说话。轻点可取消。', '正在开始', '正在等待电脑确认'],
    recording: [currentMode === 'hold' ? '松开结束' : '停止说话', '电脑端停止也会同步结束', '正在说话', ''],
    sharing: ['关闭共享', '先在电脑结束本段；关闭共享会取消未结束的转写', '共享已就绪，等待电脑触发', '手机正在供音' ],
    stopping: ['这句话，说完了', '正在把最后一点声音送到电脑。', '正在结束', '本机麦克风已关闭'],
    error: ['暂时没有连上', '检查连接后，再试一次。', '需要留意', '麦克风已关闭']
  }[currentState] ?? ['准备说话', '', '', ''];
  text('mic-title', title ?? defaults[0]);
  text('mic-hint', hint ?? defaults[1]);
  text('voice-status-text', status ?? defaults[2]);
  text('voice-detail', detail ?? defaults[3]);
  const recordEnabled = canRecord ?? currentState !== 'unpaired';
  $('mic-button').disabled = !recordEnabled || currentState === 'stopping';
  $('mic-button').setAttribute('aria-busy', String(busy));
  $('mic-button').setAttribute('aria-label', !recordEnabled ? (currentState === 'unpaired' ? '请先连接电脑' : '当前无法开始说话') : currentState === 'starting' ? '取消开始说话' : currentState === 'sharing' ? '关闭共享麦克风' : currentState === 'recording' ? (currentMode === 'hold' ? '正在录音，松开结束' : '停止说话') : currentMode === 'hold' ? '按住开始说话，松开停止' : currentMode === 'shared' ? '开启共享麦克风' : '开始说话');
  document.querySelectorAll('[data-mode-button]').forEach(button => {
    button.setAttribute('aria-pressed', String(button.dataset.modeButton === currentMode));
    button.disabled = active;
  });
  document.querySelectorAll('[data-action]').forEach(button => { button.disabled = !(canAct ?? currentState !== 'unpaired'); });
  $('shared-section').hidden = currentMode !== 'shared';
  if (level !== undefined) setLevel(level);
  else if (!active || busy) setLevel(0);
}
export function setLevel(level) {
  meterLevel = Math.max(0, Math.min(1, Number(level) || 0));
  if (meterFrame) return;
  meterFrame = requestAnimationFrame(() => {
    meterFrame = 0;
    const bars = $('level-meter').children;
    const center = (bars.length - 1) / 2;
    for (let i = 0; i < bars.length; i++) {
      const shape = .32 + .68 * (1 - Math.abs(i - center) / (center + 1));
      const texture = .64 + ((i * 7) % 11) / 30;
      bars[i].style.height = `${3 + 18 * meterLevel * shape * texture}px`;
      bars[i].style.opacity = meterLevel > .015 ? String(.48 + shape * .52) : '.55';
    }
    // Accessibility state updates are bounded; the visual meter remains smooth.
    if (performance.now() - lastMeterAnnouncement > 500 || meterLevel === 0) {
      $('level-meter').setAttribute('aria-valuenow', String(Math.round(meterLevel * 100)));
      lastMeterAnnouncement = performance.now();
    }
  });
}
export function renderDevices(devices = [], { targetId, sharedIds = [], pendingId, busy = false, localTargetId } = {}) {
  const signature = JSON.stringify([devices.map(deviceContent), targetId, [...sharedIds].sort(), pendingId, busy, localTargetId]);
  if (signature === devicesSignature) return;
  devicesSignature = signature;
  const list = $('devices-list');
  const focus = document.activeElement;
  const focusId = focus?.dataset?.targetId ?? focus?.dataset?.sharedId;
  const focusShared = focus?.hasAttribute?.('data-shared-id');
  const sharedSet = new Set(sharedIds);
  const fragment = document.createDocumentFragment();
  if (!devices.length) {
    const empty = element('div', 'empty-state');
    const icon = element('span', 'empty-icon'); icon.append(svg('computer'));
    empty.append(icon, element('h3', '', '还没有连接电脑'), element('p', '', '先在电脑打开言渡，扫码或粘贴手机连接链接。'));
    fragment.append(empty);
  }
  for (const device of devices) {
    const row = element('div', 'device-row');
    const button = element('button', 'device-target');
    button.type = 'button'; button.dataset.targetId = device.id;
    button.setAttribute('aria-pressed', String(device.id === targetId));
    button.setAttribute('aria-busy', String(device.id === pendingId));
    button.disabled = busy || !!pendingId || !device.online;
    const icon = element('span', 'target-icon'); icon.append(svg('computer'));
    const copy = element('span', 'device-target-copy');
    const status = element('small');
    const dot = element('span', 'status-dot'); dot.dataset.status = device.online ? 'online' : 'offline';
    const statusText = device.id === pendingId ? '正在等待电脑确认…' : device.error ? String(device.error) : device.recording ? '正在转写' : !device.online ? '离线 · 检查电脑是否打开' : !device.audioReady ? '在线 · 语音尚未就绪' : device.id === targetId ? '当前输入目标' : '已连接 · 轻点切换';
    status.append(dot, document.createTextNode(statusText));
    copy.append(element('strong', '', device.name || '未命名电脑'), status);
    const check = element('span', 'device-check'); check.append(svg('check'));
    button.append(icon, copy, check);
    const shared = element('label', 'device-shared');
    const input = document.createElement('input'); input.type = 'checkbox'; input.dataset.sharedId = device.id;
    input.checked = sharedSet.has(device.id); input.disabled = busy;
    input.setAttribute('aria-label', `将${device.name || '这台电脑'}加入共享组`);
    shared.append(element('span', '', '加入共享组'), input);
    const footer = element('div', 'device-footer');
    footer.append(shared);
    if (localTargetId && device.id !== localTargetId) {
      const remove = element('button', 'device-remove', '移除');
      remove.type = 'button'; remove.dataset.removeId = device.id;
      remove.disabled = busy || !!pendingId;
      remove.setAttribute('aria-label', `从本网页移除${device.name || '这台电脑'}`);
      footer.append(remove);
    }
    row.append(button, footer); fragment.append(row);
  }
  list.replaceChildren(fragment);
  if (focusId) {
    // Keep keyboard/VoiceOver focus if a health update redraws the open sheet.
    const selector = focusShared ? '[data-shared-id]' : '[data-target-id]';
    const replacement = [...list.querySelectorAll(selector)].find(node => (focusShared ? node.dataset.sharedId : node.dataset.targetId) === focusId);
    replacement?.focus({ preventScroll: true });
  }
}
export function renderSharedDevices(devices = []) {
  const signature = JSON.stringify(devices.map(deviceContent));
  if (signature === sharedDevicesSignature) return;
  sharedDevicesSignature = signature;
  text('shared-count', devices.length);
  const fragment = document.createDocumentFragment();
  for (const device of devices) {
    const card = element('div', 'shared-device');
    card.dataset.recording = String(!!device.recording);
    card.dataset.streaming = String(!!device.streaming);
    const status = element('span', 'shared-device-state');
    status.append(element('span', 'status-dot'), document.createTextNode(device.error ? '连接需要检查' : device.recording ? '正在转写' : device.streaming ? '供音中 · 待命' : device.online ? '尚未供音' : '暂时离线'));
    const name = element('span', 'shared-device-name', device.name || '未命名电脑');
    name.title = name.textContent;
    card.append(name, status, element('span', 'shared-device-detail', device.error ? String(device.error) : device.recording ? '仅结束本机' : device.streaming ? '快捷键开始' : device.online ? '等待开启共享' : '检查电脑连接'));
    fragment.append(card);
  }
  if (!devices.length) fragment.append(element('p', 'shared-empty', '选择共享组，然后手动开启麦克风。'));
  $('shared-devices').replaceChildren(fragment);
}
export function setNotice(message, { tone = 'info', actionLabel = '', action = '' } = {}) {
  $('app-notice').hidden = !message;
  text('notice-message', message || '');
  $('app-notice').dataset.tone = tone;
  text('notice-action', actionLabel);
  $('notice-action').hidden = !actionLabel;
  $('notice-action').dataset.actionName = action;
}
export function setPairingKind(kind) {
  if (!['gateway', 'peer'].includes(kind) || kind === pairingKind) return;
  pairingKind = kind;
  const gateway = kind === 'gateway';
  text('pairing-eyebrow', gateway ? '连接你的工作空间' : '再多一个工作空间');
  text('pairing-heading', gateway ? '连接这台手机' : '添加另一台电脑');
  text('pairing-description', gateway
    ? '在电脑的「iPhone / 手机网页」点击「连接手机网页」，将完整连接链接粘贴到这里，然后在电脑确认。'
    : '在另一台电脑的言渡控制页打开配对，将配对资料复制到这里，随后在那台电脑确认。');
  text('pairing-code-label', gateway ? '手机连接链接' : '配对资料');
  $('pairing-dialog').querySelector('[data-close-dialog]').setAttribute('aria-label', gateway ? '关闭手机连接' : '关闭添加电脑');
  $('pairing-host-field').hidden = gateway;
  $('pairing-host').required = !gateway;
  $('pairing-code').placeholder = gateway ? '粘贴电脑提供的完整手机连接链接…' : '粘贴电脑提供的配对资料…';
  $('pairing-code').value = '';
  $('pairing-host').value = '';
  text('pairing-message', '');
  $('pairing-message').dataset.error = 'false';
  if (!$('pairing-submit').disabled) text('pairing-submit', gateway ? '请求连接手机 ↗' : '请求连接 ↗');
  document.querySelectorAll('[data-open-dialog="pairing-dialog"]').forEach(button => {
    button.replaceChildren(svg('plus'), document.createTextNode(gateway ? '连接这台手机' : '添加另一台电脑'));
  });
}
export function setPairingState({ busy = false, error = '', message = '' } = {}) {
  $('pairing-submit').disabled = busy;
  $('pairing-code').disabled = busy;
  $('pairing-host').disabled = busy;
  text('pairing-submit', busy ? '等待电脑确认…' : pairingKind === 'gateway' ? '请求连接手机 ↗' : '请求连接 ↗');
  text('pairing-message', error || message);
  $('pairing-message').dataset.error = String(!!error);
  $('pairing-form').setAttribute('aria-busy', String(busy));
}
