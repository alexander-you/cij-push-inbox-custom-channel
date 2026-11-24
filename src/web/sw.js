// Service worker: shows Web Push notifications (RTL-aware) and opens the action URL on click.
self.addEventListener('install', () => self.skipWaiting());
self.addEventListener('activate', (event) => event.waitUntil(self.clients.claim()));

self.addEventListener('push', (event) => {
  let data = {};
  try { data = event.data ? event.data.json() : {}; } catch { data = { title: event.data && event.data.text() }; }
  const options = {
    body: data.body || '',
    dir: data.dir || 'auto',
    lang: data.lang || '',
    icon: 'icon.svg',
    image: data.image || undefined,
    tag: data.messageId || undefined,
    data: { url: data.url || null, messageId: data.messageId || null },
  };
  event.waitUntil(Promise.all([
    self.registration.showNotification(data.title || 'Push Inbox', options),
    self.clients.matchAll({ type: 'window' }).then((cs) => cs.forEach((c) => c.postMessage({ type: 'inbox-changed' }))),
  ]));
});

self.addEventListener('notificationclick', (event) => {
  event.notification.close();
  const url = (event.notification.data && event.notification.data.url) || self.registration.scope;
  event.waitUntil(self.clients.openWindow(url));
});
