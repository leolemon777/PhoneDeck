const VERSION = 'yandu-phone-v1';
const SHELL = ['./', './style.css', './app.js', './ui.js', './session.js', './audio.js', './pcm-worklet.js', './manifest.webmanifest', './icons/icon.svg', './icons/icon-192.png', './icons/icon-512.png', './icons/apple-touch-icon.png'];
self.addEventListener('install', event => event.waitUntil(caches.open(VERSION).then(cache => cache.addAll(SHELL))));
self.addEventListener('activate', event => event.waitUntil((async () => {
  for (const name of await caches.keys()) if (name.startsWith('yandu-phone-') && name !== VERSION) await caches.delete(name);
  await self.clients.claim();
})()));
self.addEventListener('message', event => { if (event.data === 'activate') event.waitUntil(self.skipWaiting()); });
self.addEventListener('fetch', event => {
  const url = new URL(event.request.url);
  if (event.request.method !== 'GET' || url.origin !== self.location.origin || !url.pathname.startsWith('/phone/')
      || url.pathname.startsWith('/phone/api/') || url.pathname === '/phone/socket' || url.pathname === '/phone/pair') return;
  event.respondWith((async () => {
    try {
      const response = await fetch(event.request);
      // Only the fixed public shell is cached; credentials, state and audio never are.
      const allowed = SHELL.some(path => new URL(path, self.registration.scope).pathname === url.pathname);
      if (response.ok && allowed) (await caches.open(VERSION)).put(event.request, response.clone());
      return response;
    } catch {
      return await caches.match(event.request) || new Response('电脑暂时离线，请恢复连接后重新打开。', { status: 503, headers: { 'Content-Type': 'text/plain; charset=utf-8' } });
    }
  })());
});
