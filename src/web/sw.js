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
    data: { url: data.url || null, messageId: data.messageId || null, inboxItemId: data.inboxItemId || data.messageId || null },
  };
  event.waitUntil(Promise.all([
    self.registration.showNotification(data.title || 'Push Inbox', options),
    self.clients.matchAll({ type: 'window' }).then((cs) => cs.forEach((c) => c.postMessage({ type: 'inbox-changed' }))),
  ]));
});

// A tap opens the action URL. When that URL is a CIJ tracked link to this app, CIJ counts the click and redirects here
// with ?msdynmkt_trackingcontext=<RequestId>, which the app uses to open that message. Without a URL, the app opens
// directly and #m=<inboxItemId> identifies the message.
self.addEventListener('notificationclick', (event) => {
  event.notification.close();
  const data = event.notification.data || {};
  let url = data.url;
  if (!url) url = self.registration.scope + (data.inboxItemId ? '#m=' + encodeURIComponent(data.inboxItemId) : '');
  event.waitUntil(self.clients.openWindow(url));
});
