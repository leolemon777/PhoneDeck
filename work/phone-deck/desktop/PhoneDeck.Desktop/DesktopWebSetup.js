// This script runs only in the receiver's loopback settings page.
(() => {
  let activePairing = null, refreshing = false, enabled = false;
  const message = text => { el('webSetupStatus').textContent = text; };
  function closePairing() {
    activePairing = null; el('webPairing').hidden = true; el('webCancel').hidden = true;
    el('webQr').removeAttribute('src'); el('webUrls').replaceChildren();
  }
  action('webPair', async () => {
    const result = await api('/local/web/begin', {});
    activePairing = result.pairingId; el('webPairing').hidden = false; el('webCancel').hidden = false;
    el('webQr').src = 'data:image/png;base64,' + result.qr;
    el('webCheck').textContent = result.checkCode; el('webPending').textContent = '等待手机扫码';
    el('webUrls').replaceChildren();
    for (const url of result.urls) {
      const row = document.createElement('p'), link = document.createElement('a'), copy = document.createElement('button');
      link.href = url; link.textContent = new URL(url).host; link.target = '_blank'; link.rel = 'noreferrer';
      copy.textContent = '复制连接';
      copy.onclick = async () => { try { await navigator.clipboard.writeText(url); copy.textContent = '已复制'; }
        catch { const field = document.createElement('textarea'); field.value = url; field.readOnly = true; field.style.width = '100%'; row.append(field); field.select(); } };
      row.append(link, copy); el('webUrls').append(row);
    }
    await refresh();
  });
  action('webCancel', async () => { await api('/local/web/cancel', {}); closePairing(); message('配对已关闭'); });
  action('webConfirm', async () => { await api('/local/web/confirm', { pairingId: activePairing }); closePairing(); message('已批准这台手机，请在手机继续。连接凭据仍需由手机接收。'); });
  action('webDeny', async () => { await api('/local/web/deny', { pairingId: activePairing }); closePairing(); message('已拒绝本次连接。需要时可重新开启配对。'); });
  let clientsKey = '';
  async function refresh() {
    if (!enabled || refreshing) return;
    refreshing = true;
    try {
      const status = await api('/local/web/status');
      const key = JSON.stringify(status.clients);
      if (key !== clientsKey) {
        clientsKey = key; el('webClients').replaceChildren();
        for (const client of status.clients || []) {
          if (client.revokedAtUtc) continue;
          const row = document.createElement('p'), button = document.createElement('button');
          row.textContent = '已配对网页：' + client.label + ' '; button.textContent = '撤销';
          button.onclick = async () => { button.disabled = true; try { await api('/local/web/revoke', { clientId: client.clientId }); await refresh(); }
            catch (error) { el('error').textContent = error.message; } finally { button.disabled = false; } };
          row.append(button); el('webClients').append(row);
        }
      }
      if (activePairing) {
        const pairing = status.pairing;
        const pending = pairing.pending;
        el('webPending').textContent = pending ? '等待确认：' + pending.clientLabel : '等待手机打开 · ' + (pairing.remainingSeconds ?? 0) + ' 秒';
        el('webConfirm').hidden = !pending; el('webDeny').hidden = !pending;
        if (!pending && !(pairing.remainingSeconds > 0)) { closePairing(); message('配对窗口已结束。已配对的手机可重新打开原网页。'); }
      }
    } catch { message('手机网页状态暂未响应，请确认接收端仍在运行'); }
    finally { refreshing = false; }
  }
  void (async () => {
    try {
      const setup = await api('/local/web/setup'); enabled = true;
      el('webFingerprint').textContent = setup.certificateSha256.match(/.{1,4}/g).join(' ');
      message(setup.urls.length ? '入口已准备好 · ' + setup.urls.map(url => new URL(url).host).join(' / ') : setup.message || '请先将电脑连接到局域网');
      el('webPair').disabled = !setup.urls.length;
      await refresh(); setInterval(refresh, 1200);
    } catch { message('此接收端未启用手机网页入口'); }
  })();
})();
