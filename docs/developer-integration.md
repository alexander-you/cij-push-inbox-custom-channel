# Developer integration (customer apps)

For developers of the customer's own web, Android or iOS app. To receive Push + Inbox messages from Customer Insights - Journeys, your app does three things:

1. Gets a push token from the browser or OS push service and **registers the device** with the backend, together with the recipient key and the app key.
2. **Reads the inbox** and marks items as read or deletes them, through the Inbox API.
3. **Opens the tracked action link on tap**, so CIJ counts the click; see [step 3](#3-how-to-report-clicks).

**Reference implementation:** the PWA in [`src/web`](../src/web) (`app.js` and `sw.js`). Everything in this guide for the web is taken from that code, which has been tested end to end in Microsoft Edge.

**Android and iOS:** the backend registers FCM v1 and APNs devices and sends to them (`InboxApi.cs`, `Dispatcher.cs`, `PushPlatforms.cs`). There is no reference native app in this repository. The client snippets below are based on the platform vendors' public docs and have **not been tested on a device**. What was verified without devices: registration creates the right Notification Hubs installation, the per-platform payloads (unit tests), and that a hub without FCM v1 or APNs credentials skips that platform without failing the message. **Unverified:** delivery to a real Android or iOS device.

> **The Inbox API in this repository is a test harness.** It is anonymous: the caller identifies itself with `appKey` + `recipientKey` and no sign-in. Don't ship an app against it unchanged. See [What must change for production](#what-must-change-for-production).

## Identifiers

| Value | Where it comes from | Must match |
|---|---|---|
| `apiBaseUrl` | The `API_BASE_URL` output of `azd up` (`https://<function-app>.azurewebsites.net/api`) | — |
| `appKey` | The **App key** on the channel instance in CIJ ([configuration](configuration.md)) | The app key of the channel instance that sends the message |
| `recipientKey` | The CIJ record of the signed-in customer: **`contact_<contactid>`** (or `lead_<leadid>` for lead journeys). Your backend gives it to the app after sign-in; see [How your app gets the recipient key](#how-your-app-gets-the-recipient-key). | **Journey sends:** `lower(UserEntityType + "_" + UserId)` from the request's `MarketingAppContext`, for example `contact_6f1c…`. **Test sends only:** `MarketingAppContext.UserId` and `UserEntityType` are null ([Custom channel custom APIs](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-custom-channel-custom-api)), so the backend uses the value typed in the test-send box (`To`). Never use a phone number as the recipient key of a production app. |
| `installationId` | Generated once per device by your app and persisted | Stable for the device |

### How the backend builds the recipient key (`ChannelApi.cs`)

1. If `MarketingAppContext.UserId` and `UserEntityType` are both present (a journey send), the key is `{UserEntityType}_{UserId}`.
2. Otherwise (a test send), the key is the `To` value.
3. Either way it's normalized with `Keys.Recipient` (`Models.cs`): lowercase, trimmed, every character outside `[a-z0-9_@#.:-]` removed (so `+` and spaces go; hyphens stay), and cut to 100 characters. `POST /devices` applies the same function, so `contact_6F1C…` and `contact_6f1c…` match.

A journey send to a contact whose app registered with a phone number, or a test send typed as `contact_<id>` when the app registered with a phone number, reaches nobody (`NoDevice`). **Verified:** journey sends to `contact_<id>` registrations, and test sends to a phone-number registration, in a test environment.

### How your app gets the recipient key

The app must get `contact_<contactid>` from **your backend after sign-in**, never from a URL, a deep link or user input:

1. The customer signs in to your app with your identity provider.
2. Your backend maps that user to the Dataverse contact. Options, depending on what you already have:
   - your identity system already stores the contact ID (for example as a user attribute or token claim set at sign-up);
   - you keep your own customer number on the contact as an **alternate key** and look up the contact ID with it ([Define alternate keys](https://learn.microsoft.com/power-apps/developer/data-platform/define-alternate-keys-entity), [Retrieve a record using an alternate key](https://learn.microsoft.com/power-apps/developer/data-platform/webapi/retrieve-entity-using-web-api#retrieve-record-using-an-alternate-key));
   - you query contacts by a **verified** attribute such as a verified e-mail ([Query data using the Web API](https://learn.microsoft.com/power-apps/developer/data-platform/webapi/query/overview)). Don't map on unverified input.
3. Your backend returns `contact_<contactid>` (lowercase GUID) to the app, and the app registers the device with it and keeps it with its login state.
4. If a journey targets leads, map to `lead_<leadid>` the same way.

This mapping isn't part of this repository; it belongs to your app's backend. In production it should sit behind the authenticated Inbox API, which derives the recipient key from the signed-in user ([What must change for production](#what-must-change-for-production)).

**The reference PWA is a test harness and doesn't do this.** It takes `recipientKey` from the form field, which is pre-filled from the `?recipientKey=` query parameter or `localStorage` (`app.js`), and keeps it in `localStorage`. That's convenient for testing and is not a pattern to copy.

## 1. Register the device

### Web (implemented, reference: `src/web/app.js`)

The browser flow uses the standard Push API with VAPID ([Push API](https://developer.mozilla.org/docs/Web/API/Push_API), [PushManager.subscribe](https://developer.mozilla.org/docs/Web/API/PushManager/subscribe), [PushSubscription.getKey](https://developer.mozilla.org/docs/Web/API/PushSubscription/getKey)). The backend registers the subscription as a Notification Hubs browser installation ([Notification Hubs browser push](https://learn.microsoft.com/azure/notification-hubs/browser-push)).

The reference PWA reads `recipientKey` from its form (pre-filled from `?recipientKey=` or `localStorage`) for testing. A production web app sends the `contact_<contactid>` its backend returned after sign-in ([How your app gets the recipient key](#how-your-app-gets-the-recipient-key)).

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
| `inboxItemId` | Inbox item `id`, for `/inbox/{id}/read` and `#m=` |
| `dir`, `lang` | `rtl`/`he` when the text contains Hebrew or Arabic, otherwise `ltr`/`en` |

### Native apps: registration contract (backend implemented; clients untested)

`POST {apiBaseUrl}/devices` takes the same `appKey`, `recipientKey` and `installationId`, plus `platform` and `pushToken`:

`recipientKey` is the `contact_<contactid>` your backend returned after sign-in ([How your app gets the recipient key](#how-your-app-gets-the-recipient-key)), never a phone number or a value from a link.

```json
{ "platform": "fcmv1", "appKey": "…", "recipientKey": "…", "installationId": "…", "pushToken": "<FCM registration token>" }
{ "platform": "apns",  "appKey": "…", "recipientKey": "…", "installationId": "…", "pushToken": "<APNs device token, hex>" }
```

| `platform` | Required fields | Notification Hubs installation |
|---|---|---|
| `browser` (default when omitted) | `endpoint` (https), `p256dh`, `auth` | `platform: browser` |
| `fcmv1` | `pushToken`: the FCM registration token, no whitespace, up to 4096 characters | `platform: fcmv1`, `pushChannel` = token |
| `apns` | `pushToken`: the device token as hex, 64–200 characters | `platform: apns`, `pushChannel` = token |

- Returns `200 { recipientKey, appKey, platform, tags }`, or `400 { reason: "InvalidRegistration" }`. Tags are set by the backend, exactly as for the web.
- **Verified** against a hub: the installation is stored as `FcmV1` / `Apns` with the backend's tags. Notification Hubs stores the APNs hex token in upper case.
- Re-register when the token rotates (FCM `onNewToken`; iOS calls `didRegisterForRemoteNotificationsWithDeviceToken` on every launch where you register) and when the signed-in user changes.
- Sources: [Create or overwrite an installation](https://learn.microsoft.com/rest/api/notificationhubs/create-overwrite-installation), [FCM v1 in Notification Hubs](https://learn.microsoft.com/azure/notification-hubs/notification-hubs-gcm-to-fcm), [Registration management](https://learn.microsoft.com/azure/notification-hubs/notification-hubs-push-notification-registration-management).

**The hub needs credentials for each native platform.** The customer adds them in their own Notification Hubs namespace; see [installation](installation.md#44-native-push-credentials-android-and-ios). Without them, sends to that platform are skipped.

### Android (backend implemented; client untested)

App side, per the [Firebase Android client guide](https://firebase.google.com/docs/cloud-messaging/android/client):

- Get the token with `FirebaseMessaging.getInstance().token`.
- Handle rotation in `FirebaseMessagingService.onNewToken`. Firebase recommends always retrieving the latest token.
- On Android 13 and later, request the runtime `POST_NOTIFICATIONS` permission ([notification permission](https://developer.android.com/develop/ui/views/notifications/notification-permission)).

```kotlin
// Based on the Firebase guide; not tested on a device with this backend.
FirebaseMessaging.getInstance().token.addOnCompleteListener { task ->
    if (!task.isSuccessful) return@addOnCompleteListener
    // POST {apiBaseUrl}/devices { platform: "fcmv1", appKey, recipientKey, installationId, pushToken: task.result }
}
```

### iOS (backend implemented; client untested)

App side, per Apple's docs:

- Ask for permission with `UNUserNotificationCenter.requestAuthorization` ([Asking permission](https://developer.apple.com/documentation/usernotifications/asking-permission-to-use-notifications)).
- Call `UIApplication.registerForRemoteNotifications()`.
- Receive the device token in `application(_:didRegisterForRemoteNotificationsWithDeviceToken:)` and forward it to your server ([Registering your app with APNs](https://developer.apple.com/documentation/usernotifications/registering-your-app-with-apns)).

```swift
// Based on Apple's documentation; not tested on a device with this backend.
func application(_ application: UIApplication,
                 didRegisterForRemoteNotificationsWithDeviceToken deviceToken: Data) {
    let token = deviceToken.map { String(format: "%02x", $0) }.joined()
    // POST {apiBaseUrl}/devices { platform: "apns", appKey, recipientKey, installationId, pushToken: token }
}
```

### Push payload per platform

The Dispatcher sends one native notification per platform, with the same tag expression; Notification Hubs delivers each format only to installations of that platform ([APNs native send](https://learn.microsoft.com/rest/api/notificationhubs/send-apns-native-notification), [FCM v1 sends](https://learn.microsoft.com/azure/notification-hubs/firebase-migration-rest)). Every payload carries the same values (code: `src/PushInbox.Functions/PushPlatforms.cs`):

| Value | Web (`browser`) | Android (`fcmV1`) | iOS (`apple`) |
|---|---|---|---|
| Title, body | `title`, `body` | `message.notification.title` / `.body` | `aps.alert.title` / `.body` |
| Image | `image` | `message.notification.image` and `message.data.image` | `image` (custom key); `aps.mutable-content: 1` is set when there is an image |
| Action URL | `url` | `message.data.url` | `url` |
| Message ID | `messageId` | `message.data.messageId` | `messageId` |
| Inbox item ID | `inboxItemId` | `message.data.inboxItemId` | `inboxItemId` |
| Text direction | `dir`, `lang` | `message.data.dir` | `dir` |

```json
// fcmV1
{ "message": { "notification": { "title": "…", "body": "…", "image": "https://…" },
               "data": { "messageId": "…", "inboxItemId": "…", "dir": "ltr", "url": "https://…", "image": "https://…" } } }
// apple (headers apns-push-type: alert, apns-priority: 10)
{ "aps": { "alert": { "title": "…", "body": "…" }, "mutable-content": 1 },
  "messageId": "…", "inboxItemId": "…", "dir": "ltr", "url": "https://…", "image": "https://…" }
```

- **Inbox item ID** is the `id` to use with `/inbox/{id}/read` and `/inbox/{id}/click`. It equals `messageId` unless the message has a replace key.
- FCM `data` values are strings only ([FCM v1 message](https://firebase.google.com/docs/reference/fcm/rest/v1/projects.messages)). APNs custom keys sit beside `aps` ([Generating a remote notification](https://developer.apple.com/documentation/usernotifications/generating-a-remote-notification)); `apns-push-type: alert` is required by Apple and is sent as a header ([iOS 13 updates](https://learn.microsoft.com/azure/notification-hubs/push-notification-updates-ios-13)).
- FCM and APNs limit the payload to 4 KB, so native alerts shorten the title to 200 and the body to 1,000 characters. The inbox item keeps the full text.
- iOS shows the image only if the app has a Notification Service Extension that downloads `image` ([Modifying content in newly delivered notifications](https://developer.apple.com/documentation/usernotifications/modifying-content-in-newly-delivered-notifications)).
- **Opening the message:** on tap, open `url` if present (section 3); otherwise open the inbox at `inboxItemId`. Then `POST /inbox/{inboxItemId}/read`.

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
  const url = data.url || self.registration.scope + '#m=' + encodeURIComponent(data.inboxItemId);
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

### Android (client untested)

- The backend puts `url` in the FCM message's **data** payload.
- If the app handles the message in `FirebaseMessagingService.onMessageReceived`, build the notification with a `contentIntent` (PendingIntent) that opens the tracked `url` with `Intent.ACTION_VIEW` ([Set the tap action](https://developer.android.com/develop/ui/views/notifications/build-notification)).
- For *notification* messages received while the app is in the background, Android shows the notification itself. A tap launches the launcher activity with the data payload in the intent extras; read `url` there and open it ([Receive messages](https://firebase.google.com/docs/cloud-messaging/android/receive-messages)).
- To land in the app, make the action URL an Android App Link to your app ([App Links](https://developer.android.com/training/app-links)).
- **Unverified:** whether the browser opens the app when CIJ's tracking page redirects to an App Link from JavaScript. Test this before you rely on it.

### iOS (client untested)

- The backend puts `url` in the APNs payload as a custom key.
- In `userNotificationCenter(_:didReceive:withCompletionHandler:)`, read it from `response.notification.request.content.userInfo` and open it ([UNUserNotificationCenterDelegate](https://developer.apple.com/documentation/usernotifications/unusernotificationcenterdelegate/usernotificationcenter(_:didreceive:withcompletionhandler:))).
- To land in the app, use a Universal Link ([Supporting universal links](https://developer.apple.com/documentation/xcode/supporting-universal-links-in-your-app)).
- **Unverified:** the same redirect caveat as Android. Opening the tracked link in an in-app browser and then routing internally is an alternative.

## What must change for production

In priority order. Details and sources are in [security considerations](security.md).

1. **Authenticated Inbox API.** Replace `appKey` + `recipientKey` query parameters with a signed-in end user.
   - Use your existing identity provider, or Microsoft Entra External ID ([overview](https://learn.microsoft.com/entra/external-id/customers/overview-customers-ciam)). Validate the token in the API, for example with App Service authentication ([overview](https://learn.microsoft.com/azure/app-service/overview-authentication-authorization)).
   - Derive `recipientKey` from a verified claim, never from the request.
   - Device registration must be authenticated the same way, otherwise anyone can subscribe to another person's messages ([SEC-10](security.md#sec-10-inbox-api-end-user-authentication-and-authorization)).
2. **Execute privileges on the custom APIs.** Restrict `alex_PushOutbound` and `alex_PushDeliveryReport` to a dedicated role ([SEC-07](security.md#sec-07-least-privilege-in-dataverse)).
3. **Plug-in to Azure authentication.** Replace the function key with a managed identity / Entra ID token ([SEC-04](security.md#sec-04-authentication-from-dataverse-to-azure)).
4. **Rate limiting and a gateway.** Put Azure API Management in front of the APIs, with per-key rate limits ([rate-limit-by-key](https://learn.microsoft.com/azure/api-management/rate-limit-by-key-policy), [SEC-16](security.md#sec-16-idempotency-replay-abuse-and-rate-limiting)).
5. **Keyed tag hashing.** Move from a plain SHA-256 to a keyed hash (HMAC) for recipient tags, and remove the transitional plain-tag match in the Dispatcher ([SEC-12](security.md#sec-12-personal-data-in-notification-hubs-tags)).
6. **Test native apps on devices** (section 1), including clicks (section 3), and add a custom trigger if you need to branch on app opens specifically.
7. **Production tiers** ([production sizing](production-sizing.md)).
