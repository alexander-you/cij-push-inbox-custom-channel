// Reference PWA for testing the Push + Inbox channel without mobile development environments.
// TEST HARNESS: identifies the recipient by the key typed below, without sign-in.
const T = {
  he: {
    title: 'תיבת הודעות', lang: 'English', appKey: 'מפתח אפליקציה', recipientKey: 'מזהה נמען',
    recipientHint: 'הזינו את אותו ערך שתזינו בשדה הנמען בשליחת הניסיון ב-Customer Insights - Journeys (למשל מספר טלפון), או contact_<מזהה איש קשר> עבור מסעות.',
    enable: 'הפעלת התראות ורישום הדפדפן', refresh: 'רענון', inbox: 'הודעות', empty: 'אין הודעות.',
    registered: 'הדפדפן נרשם לקבלת התראות.', denied: 'ההרשאה להתראות נדחתה.', unsupported: 'הדפדפן אינו תומך בהתראות Push.',
    missing: 'יש להזין מפתח אפליקציה ומזהה נמען.', noApi: 'כתובת ה-API לא הוגדרה.', del: 'מחיקה', open: 'פתיחה', expires: 'בתוקף עד',
  },
  en: {
    title: 'Message inbox', lang: 'עברית', appKey: 'App key', recipientKey: 'Recipient key',
    recipientHint: 'Enter the same value you type as the recipient in the Customer Insights - Journeys test send (for example a phone number), or contact_<contact id> for journeys.',
    enable: 'Enable notifications and register this browser', refresh: 'Refresh', inbox: 'Messages', empty: 'No messages.',
    registered: 'This browser is registered for notifications.', denied: 'Notification permission was denied.', unsupported: 'This browser does not support push notifications.',
    missing: 'Enter an app key and a recipient key.', noApi: 'The API address is not configured.', del: 'Delete', open: 'Open', expires: 'Expires',
  },
};
const qs = new URLSearchParams(location.search);
const api = (qs.get('api') || (window.PUSH_INBOX_CONFIG || {}).apiBaseUrl || '').replace(/\/$/, '');
const $ = (id) => document.getElementById(id);
// The message opened from a notification tap stays highlighted across re-renders.
let focusedId = null;
let lang = localStorage.getItem('lang') || (navigator.language.startsWith('he') ? 'he' : 'en');

function applyLang() {
  document.documentElement.lang = lang;
  document.documentElement.dir = lang === 'he' ? 'rtl' : 'ltr';
  document.querySelectorAll('[data-t]').forEach((el) => { el.textContent = T[lang][el.dataset.t]; });
  $('lang').textContent = T[lang].lang;
}
function status(key, extra) { $('status').textContent = T[lang][key] + (extra ? ' ' + extra : ''); }
function ids() {
  const appKey = $('appKey').value.trim();
  const recipientKey = $('recipientKey').value.trim();
  localStorage.setItem('appKey', appKey);
  localStorage.setItem('recipientKey', recipientKey);
  return { appKey, recipientKey };
}
function installationId() {
  let id = localStorage.getItem('installationId');
  if (!id) { id = 'web-' + crypto.randomUUID(); localStorage.setItem('installationId', id); }
  return id;
}
function b64ToBytes(b64) {
  const s = atob((b64 + '='.repeat((4 - (b64.length % 4)) % 4)).replace(/-/g, '+').replace(/_/g, '/'));
  return Uint8Array.from(s, (c) => c.charCodeAt(0));
}
function bufToB64(buf) {
  return btoa(String.fromCharCode(...new Uint8Array(buf))).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

async function enable() {
  if (!api) return status('noApi');
  const { appKey, recipientKey } = ids();
  if (!appKey || !recipientKey) return status('missing');
  if (!('serviceWorker' in navigator) || !('PushManager' in window)) return status('unsupported');
  if (await Notification.requestPermission() !== 'granted') return status('denied');
  const reg = await navigator.serviceWorker.register('sw.js');
  await navigator.serviceWorker.ready;
  const { vapidPublicKey } = await (await fetch(api + '/config')).json();
  const sub = (await reg.pushManager.getSubscription())
    || await reg.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: b64ToBytes(vapidPublicKey) });
  const res = await fetch(api + '/devices', {
    method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      appKey, recipientKey, installationId: installationId(), endpoint: sub.endpoint,
      p256dh: bufToB64(sub.getKey('p256dh')), auth: bufToB64(sub.getKey('auth')),
    }),
  });
  const body = await res.json();
  res.ok ? status('registered', (body.tags || []).join(', ')) : status('unsupported', JSON.stringify(body));
  load();
}

async function load() {
  const { appKey, recipientKey } = ids();
  if (!api || !appKey || !recipientKey) return;
  const q = `appKey=${encodeURIComponent(appKey)}&recipientKey=${encodeURIComponent(recipientKey)}`;
  const data = await (await fetch(`${api}/inbox?${q}`)).json();
  $('unread').textContent = data.unread || '';
  const list = $('items');
  list.replaceChildren();
  if (!data.items || !data.items.length) { const li = document.createElement('li'); li.textContent = T[lang].empty; list.append(li); return; }
  for (const item of data.items) {
    const li = document.createElement('li');
    li.dir = item.dir || 'auto';
    if (!item.readOn) li.className = 'unread';
    const h = document.createElement('h3'); h.textContent = item.title; li.append(h);
    const p = document.createElement('p'); p.append(...linkify(item.body || '')); li.append(p);
    if (item.imageUrl) { const img = document.createElement('img'); img.src = item.imageUrl; img.alt = ''; li.append(img); }
    const meta = document.createElement('div'); meta.className = 'meta';
    meta.textContent = `${new Date(item.createdOn).toLocaleString(lang)} · ${T[lang].expires} ${new Date(item.expiresOn).toLocaleDateString(lang)}`;
    li.append(meta);
    if (item.actionUrl) {
      const a = document.createElement('a'); a.href = item.actionUrl; a.target = '_blank'; a.rel = 'noopener'; a.textContent = T[lang].open; li.append(a, ' ');
    }
    const del = document.createElement('button'); del.textContent = T[lang].del;
    del.onclick = async (e) => { e.stopPropagation(); await fetch(`${api}/inbox/${encodeURIComponent(item.id)}?${q}`, { method: 'DELETE' }); load(); };
    li.append(del);
    li.onclick = async () => { if (!item.readOn) { await fetch(`${api}/inbox/${encodeURIComponent(item.id)}/read?${q}`, { method: 'POST' }); load(); } };
    li.dataset.id = item.id;
    list.append(li);
  }
  const hashId = new URLSearchParams(location.hash.slice(1)).get('m');
  const requestId = qs.get('msdynmkt_trackingcontext');
  const target = data.items.find((i) => (hashId && i.id === hashId) || (requestId && i.requestId === requestId));
  if (target) {
    focusedId = target.id;
    qs.delete('msdynmkt_trackingcontext');
    history.replaceState(null, '', location.pathname + (qs.toString() ? '?' + qs : ''));
  }
  const focused = focusedId && [...list.children].find((li) => li.dataset.id === focusedId);
  if (focused) {
    focused.classList.add('focus');
    if (target) focused.scrollIntoView({ block: 'center' });
    if (focused.classList.contains('unread')) focused.click();
  }
}

// Body links (CIJ tracked links) become clickable; everything else stays plain text.
function linkify(text) {
  return text.split(/(https:\/\/[^\s<>"']+)/).map((part, i) => {
    if (i % 2 === 0) return document.createTextNode(part);
    const a = document.createElement('a'); a.href = part; a.target = '_blank'; a.rel = 'noopener'; a.textContent = part;
    a.onclick = (e) => e.stopPropagation();
    return a;
  });
}

$('appKey').value = qs.get('appKey') || localStorage.getItem('appKey') || '';
$('recipientKey').value = qs.get('recipientKey') || localStorage.getItem('recipientKey') || '';
$('lang').onclick = () => { lang = lang === 'he' ? 'en' : 'he'; localStorage.setItem('lang', lang); applyLang(); load(); };
$('enable').onclick = () => enable().catch((e) => status('unsupported', e.message));
$('refresh').onclick = () => load();
if ('serviceWorker' in navigator) navigator.serviceWorker.addEventListener('message', (e) => { if (e.data && e.data.type === 'inbox-changed') load(); });
applyLang();
load();
if ('serviceWorker' in navigator) navigator.serviceWorker.getRegistration().then((r) => r && r.update());
