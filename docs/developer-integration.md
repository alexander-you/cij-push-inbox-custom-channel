# Developer integration (customer apps)

For developers of the customer's own web, Android or iOS app. To receive Push + Inbox messages from Customer Insights - Journeys, your app does three things:

1. Gets a push token from the browser or OS push service and **registers the device** with the backend, together with the recipient key and the app key.
2. **Reads the inbox** and marks items as read or deletes them, through the Inbox API.
3. **Opens the tracked action link on tap**, so CIJ counts the click; see [step 3](#3-how-to-report-clicks).

**Reference implementation:** the PWA in [`src/web`](../src/web) (`app.js` and `sw.js`). Everything in this guide for the web is taken from that code, which has been tested end to end in Microsoft Edge. Android and iOS are **not implemented** in the backend yet; those sections show what is needed, based on the platform vendors' public docs, and are labelled accordingly.

> **The Inbox API in this repository is a test harness.** It is anonymous: the caller identifies itself with `appKey` + `recipientKey` and no sign-in. Don't ship an app against it unchanged. See [What must change for production](#what-must-change-for-production).

## Identifiers

| Value | Where it comes from | Must match |
|---|---|---|
| `apiBaseUrl` | The `API_BASE_URL` output of `azd up` (`https://<function-app>.azurewebsites.net/api`) | — |
| `appKey` | The **App key** on the channel instance in CIJ ([configuration](configuration.md)) | The app key of the channel instance that sends the message |
| `recipientKey` | Your app's identifier for the signed-in customer, usually the phone number used as the CIJ recipient | The CIJ `To` value. Both sides are normalized the same way: lowercase, `+` and spaces removed, other characters outside `[a-z0-9_@#.:-]` removed. Hyphens are **kept**, so use the same phone format on both sides. |
| `installationId` | Generated once per device by your app and persisted | Stable for the device |

## 1. Register the device

### Web (implemented, reference: `src/web/app.js`)

The browser flow uses the standard Push API with VAPID ([Push API](https://developer.mozilla.org/docs/Web/API/Push_API), [PushManager.subscribe](https://developer.mozilla.org/docs/Web/API/PushManager/subscribe), [PushSubscription.getKey](https://developer.mozilla.org/docs/Web/API/PushSubscription/getKey)). The backend registers the subscription as a Notification Hubs browser installation ([Notification Hubs browser push](https://learn.microsoft.com/azure/notification-hubs/browser-push)).

```js
// 1. Permission and service worker
if (await Notification.requestPermission() !== 'granted') return;
const reg = await navigator.serviceWorker.register('sw.js');
await navigator.serviceWorker.ready;

// 2. VAPID public key from the backend
const { vapidPublicKey } = await (await fetch(`${apiBaseUrl}/config`)).json();

// 3. Push subscription (b64ToBytes: base64url -> Uint8Array, see app.js)
const sub = (await reg.pushManager.getSubscription())
  || await reg.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: b64ToBytes(vapidPublicKey) });

// 4. Register with the backend (bufToB64: ArrayBuffer -> base64url, see app.js)
await fetch(`${apiBaseUrl}/devices`, {
  method: 'POST',
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({
    appKey, recipientKey, installationId,
    endpoint: sub.endpoint,
    p256dh: bufToB64(sub.getKey('p256dh')),
    auth: bufToB64(sub.getKey('auth')),
  }),
});
```

`POST /devices` returns `200 { recipientKey, appKey, tags }`, or `400 { reason: "InvalidRegistration" }` if a field is missing or the endpoint isn't HTTPS.

- **Tags:** the backend sets them; the client never does. The recipient tag is a SHA-256 hash of the recipient key ([security, SEC-12](security.md#sec-12-personal-data-in-notification-hubs-tags)).
- **When to call it:**
  - on first opt-in;
  - whenever the subscription or the signed-in user changes.
  - Calling it again with the same `installationId` updates the installation.

**Service worker:** shows the notification and opens the action URL (`src/web/sw.js`). The push payload is JSON:

| Field | Meaning |
|---|---|
| `title`, `body` | Message text |
| `image` | Image URL, optional |
| `url` | Action URL, optional. A CIJ tracked link when the message has one ([section 3](#3-how-to-report-clicks)). |
| `messageId` | Message ID; used as the notification `tag` |
| `dir`, `lang` | `rtl`/`he` when the text contains Hebrew or Arabic, otherwise `ltr`/`en` |

### Android (planned, not implemented)

**Backend change required:** `POST /devices` accepts only browser subscriptions today. Supporting Android means registering an FCM v1 installation in Notification Hubs ([Installations](https://learn.microsoft.com/azure/notification-hubs/notification-hubs-push-notification-registration-management), [FCM v1 in Notification Hubs](https://learn.microsoft.com/azure/notification-hubs/notification-hubs-gcm-to-fcm)). The request shape below is a **proposal**.

App side, per the [Firebase Android client guide](https://firebase.google.com/docs/cloud-messaging/android/client):

- Get the token with `FirebaseMessaging.getInstance().token`.
- Handle rotation in `FirebaseMessagingService.onNewToken`. Firebase recommends always retrieving the latest token.
- On Android 13 and later, request the runtime `POST_NOTIFICATIONS` permission ([notification permission](https://developer.android.com/develop/ui/views/notifications/notification-permission)).

```kotlin
// From the Firebase guide; not tested with this backend.
FirebaseMessaging.getInstance().token.addOnCompleteListener { task ->
    if (!task.isSuccessful) return@addOnCompleteListener
    val token = task.result
    // Proposed: POST {apiBaseUrl}/devices { appKey, recipientKey, installationId, platform: "fcmv1", pushChannel: token }
}
```

### iOS (planned, not implemented)

**Backend change required:** register an APNs installation in Notification Hubs ([Installations](https://learn.microsoft.com/azure/notification-hubs/notification-hubs-push-notification-registration-management)). The request shape is a **proposal**.

App side, per Apple's docs:

- Ask for permission with `UNUserNotificationCenter.requestAuthorization` ([Asking permission](https://developer.apple.com/documentation/usernotifications/asking-permission-to-use-notifications)).
- Call `UIApplication.registerForRemoteNotifications()`.
- Receive the device token in `application(_:didRegisterForRemoteNotificationsWithDeviceToken:)` and forward it to your server ([Registering your app with APNs](https://developer.apple.com/documentation/usernotifications/registering-your-app-with-apns)).

```swift
// Based on Apple's documentation; not tested with this backend.
func application(_ application: UIApplication,
                 didRegisterForRemoteNotificationsWithDeviceToken deviceToken: Data) {
    let token = deviceToken.map { String(format: "%02x", $0) }.joined()
    // Proposed: POST {apiBaseUrl}/devices { appKey, recipientKey, installationId, platform: "apns", pushChannel: token }
}
```

The hex encoding of the token is common practice. **Unverified** against the format Notification Hubs expects for `pushChannel`; confirm it in the Notification Hubs installation docs before you build it.

## 2. Read the inbox

All routes take `appKey` and `recipientKey` as query parameters in the test harness. They are implemented in `src/PushInbox.Functions/InboxApi.cs`.

| Method and route | Result |
|---|---|
| `GET /inbox?appKey=…&recipientKey=…` | `200 { recipientKey, unread, items[] }`. Up to 100 items for that recipient and app key, newest first. |
| `POST /inbox/{id}/read?appKey=…&recipientKey=…` | `204`. Sets `readOn`; logs **Read** in the message log the first time. `404` if the item doesn't exist or belongs to another app key. |
| `POST /inbox/{id}/click?appKey=…&recipientKey=…` | `204`. Logs a **Clicked** row in the message log (one per call). Call it when the app opens from a tracked link. `404` as above. |
| `DELETE /inbox/{id}?appKey=…&recipientKey=…` | `204`. `404` as above. |

**Item fields:**
- `id`, `messageId`, `requestId` (the CIJ RequestId; matches `msdynmkt_trackingcontext`)
- `title`, `body`
- `imageUrl`, `actionUrl` (optional). `imageUrl` is the public CDN URL of a CIJ asset-library image; it needs no authentication and allows cross-origin loading.
- `dir`
- `createdOn`, `expiresOn`
- `readOn` (null when unread)

**Expiry:** items expire automatically (Cosmos DB TTL, set by the channel instance's default expiry days, 1–180). An expired item simply stops appearing.

**Refresh:** refresh the list when a push arrives. The reference service worker posts `{ type: 'inbox-changed' }` to open windows, and `app.js` reloads on that message. Also refresh on app start, because **Inbox only** messages send no push.

## 3. How to report clicks

Clicks are counted by **CIJ link tracking**: your app only opens the tracked link that CIJ put in the message. Separately, when the app opens from a tracked link, it should call `POST /inbox/{id}/click` so the solution's message log records a **Clicked** row ([data-model §8](data-model.md#8-message-log-elastic-table-alex_pushmessagelog)). That call doesn't affect CIJ analytics.

### How it works (verified end to end with the PWA in a test environment, November 2026)

1. CIJ rewrites `https://` links in custom-channel message parts into short tracked links, for example `https://eur.tx.ms/…`. This applies to both the **Tap action link** (`actionUrl`) and links in the **body**. Links without a scheme, such as `www.example.com`, are **not** rewritten, and so aren't tracked. Tracking depends on the contact's tracking consent ([Link tracking mechanics](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-link-tracking-mechanics), [Troubleshoot link tracking](https://learn.microsoft.com/troubleshoot/dynamics-365/customer-insights/journeys/tracking/troubleshoot-link-tracking)).
2. The tracked link returns an HTML page (HTTP 200) that runs CIJ bot detection and then redirects to the target URL. It is **not** an HTTP 3xx redirect, so a URL fragment (`#…`) added to the tracked link is lost. The target URL gets `msdynmkt_trackingcontext=<RequestId>` appended, observed both as a query parameter (`?msdynmkt_trackingcontext=…`) and as a fragment (`#msdynmkt_trackingcontext=…`, seen for a bare app URL). Read both. This is observed, not documented; the rule for which one CIJ uses is **unverified**.
3. CIJ records a click with its **Target URL** and **Link context**, and shows it under *Unique clicks / Total clicks* in the tile's *Delivery and interaction details*. Clicks appear after a delay of tens of minutes.

**Recommended pattern: a tap opens the app, and external links go in the body.**
- Set the **Tap action link** to your app's own URL (the PWA URL, or an Android App Link / iOS Universal Link). A tap counts as a CIJ click and lands in the app.
- **Keep the Tap action link recipient-agnostic**, for example `https://<your-app>/`. The same message goes to every recipient, so never put a recipient key or contact ID in it. The app already knows its recipient from device registration (the reference PWA keeps `appKey` and `recipientKey` in `localStorage`; a native app keeps them with its login or installation).
- Use `msdynmkt_trackingcontext` to open the message that was tapped: it equals the inbox item's `requestId`. Then call `POST /inbox/{id}/click`.
- Put external links in the body as full `https://` URLs. They're tracked too, and appear as separate click rows with their own Target URL.

**Branching.** After a custom-channel message, the *If/then: previous message gets an interaction* tile offers exactly **Custom Channel Message Delivered**, **Custom Channel Message Link Clicked** and **Custom Channel Message Bounced** (verified in the journey designer). There is no per-link selection: a tap and a body-link click are both *Link Clicked*. The per-link distinction exists only in analytics (Target URL). Which delivery report status, if any, produces *Bounced* is **unverified**. If you need to branch on "opened the app" specifically, raise a custom trigger from your backend instead ([Custom triggers](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-custom-triggers), [Wait for trigger](https://learn.microsoft.com/dynamics365/customer-insights/journeys/add-action)); this is not implemented.

### Web / PWA (implemented, `src/web/sw.js` and `app.js`)

```js
// sw.js: the push payload's `url` is the tracked actionUrl.
self.addEventListener('notificationclick', (event) => {
  event.notification.close();
  const data = event.notification.data || {};
  // No action URL: open the app directly; the fragment survives because there is no redirect.
  const url = data.url || self.registration.scope + '#m=' + encodeURIComponent(data.messageId);
  event.waitUntil(self.clients.openWindow(url));
});

// app.js: after loading the inbox, open the tapped message.
const key = 'msdynmkt_trackingcontext';
const requestId = new URLSearchParams(location.search).get(key)
  || new URLSearchParams(location.hash.slice(1)).get(key);
const item = items.find((i) => i.requestId === requestId);   // or i.id === #m
// POST /inbox/{id}/click (message log), then show it and POST /inbox/{id}/read
```

- In the inbox, render body links as `<a>` elements so they're clickable. The reference app's `linkify` matches only `https://` URLs and builds DOM nodes, never HTML strings.
- **Without an action URL**, a tap opens the app and **no click is counted** in CIJ.
- Sources: [notificationclick](https://developer.mozilla.org/docs/Web/API/ServiceWorkerGlobalScope/notificationclick_event), [Clients.openWindow](https://developer.mozilla.org/docs/Web/API/Clients/openWindow).

### Android (not implemented, untested)

- Put `url` in the FCM message's **data** payload.
- If the app handles the message in `FirebaseMessagingService.onMessageReceived`, build the notification with a `contentIntent` (PendingIntent) that opens the tracked `url` with `Intent.ACTION_VIEW` ([Set the tap action](https://developer.android.com/develop/ui/views/notifications/build-notification)).
- For *notification* messages received while the app is in the background, Android shows the notification itself. A tap launches the launcher activity with the data payload in the intent extras; read `url` there and open it ([Receive messages](https://firebase.google.com/docs/cloud-messaging/android/receive-messages)).
- To land in the app, make the action URL an Android App Link to your app ([App Links](https://developer.android.com/training/app-links)).
- **Unverified:** whether the browser opens the app when CIJ's tracking page redirects to an App Link from JavaScript. Test this before you rely on it.

### iOS (not implemented, untested)

- Put `url` in the APNs payload as a custom key.
- In `userNotificationCenter(_:didReceive:withCompletionHandler:)`, read it from `response.notification.request.content.userInfo` and open it ([UNUserNotificationCenterDelegate](https://developer.apple.com/documentation/usernotifications/unusernotificationcenterdelegate/usernotificationcenter(_:didreceive:withcompletionhandler:))).
- To land in the app, use a Universal Link ([Supporting universal links](https://developer.apple.com/documentation/xcode/supporting-universal-links-in-your-app)).
- **Unverified:** the same redirect caveat as Android. Opening the tracked link in an in-app browser and then routing internally is an alternative.

## What must change for production

In priority order. Details and sources are in [security considerations](security.md).

1. **Authenticated Inbox API.** Replace `appKey` + `recipientKey` query parameters with a signed-in end user.
   - Use your existing identity provider, or Microsoft Entra External ID ([overview](https://learn.microsoft.com/entra/external-id/customers/overview-customers-ciam)). Validate the token in the API, for example with App Service authentication ([overview](https://learn.microsoft.com/azure/app-service/overview-authentication-authorization)).
   - Derive `recipientKey` from a verified claim, never from the request.
   - Device registration must be authenticated the same way, otherwise anyone can subscribe to another person's messages ([SEC-10](security.md#sec-10-inbox-api-end-user-authentication-and-authorization)).
2. **Native platforms.** Add FCM v1 and APNs installations to `POST /devices` (section 1).
3. **Execute privileges on the custom APIs.** Restrict `alex_PushOutbound` and `alex_PushDeliveryReport` to a dedicated role ([SEC-07](security.md#sec-07-least-privilege-in-dataverse)).
4. **Plug-in to Azure authentication.** Replace the function key with a managed identity / Entra ID token ([SEC-04](security.md#sec-04-authentication-from-dataverse-to-azure)).
5. **Rate limiting and a gateway.** Put Azure API Management in front of the APIs, with per-key rate limits ([rate-limit-by-key](https://learn.microsoft.com/azure/api-management/rate-limit-by-key-policy), [SEC-16](security.md#sec-16-idempotency-replay-abuse-and-rate-limiting)).
6. **Keyed tag hashing.** Move from a plain SHA-256 to a keyed hash (HMAC) for recipient tags, and remove the transitional plain-tag match in the Dispatcher ([SEC-12](security.md#sec-12-personal-data-in-notification-hubs-tags)).
7. **Clicks on native apps** (section 3), and a custom trigger if you need to branch on app opens specifically.
8. **Production tiers** ([production sizing](production-sizing.md)).
