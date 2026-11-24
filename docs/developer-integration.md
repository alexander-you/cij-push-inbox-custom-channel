# Developer integration (customer apps)

For developers of the customer's own web, Android or iOS app. To receive Push + Inbox messages from Customer Insights - Journeys, your app does three things:

1. Gets a push token from the browser or OS push service and **registers the device** with the backend, together with the recipient key and the app key.
2. **Reads the inbox** and marks items as read or deletes them, through the Inbox API.
3. **Reports opens and clicks.** This is planned; see [step 3](#3-report-opens-and-clicks-planned).

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
| `url` | Action URL, optional |
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
| `POST /inbox/{id}/read?appKey=…&recipientKey=…` | `204`. Sets `readOn`. `404` if the item doesn't exist or belongs to another app key. |
| `DELETE /inbox/{id}?appKey=…&recipientKey=…` | `204`. `404` as above. |

**Item fields:**
- `id`, `messageId`
- `title`, `body`
- `imageUrl`, `actionUrl` (optional). `imageUrl` is the public CDN URL of a CIJ asset-library image; it needs no authentication and allows cross-origin loading.
- `dir`
- `createdOn`, `expiresOn`
- `readOn` (null when unread)

**Expiry:** items expire automatically (Cosmos DB TTL, set by the channel instance's default expiry days, 1–180). An expired item simply stops appearing.

**Refresh:** refresh the list when a push arrives. The reference service worker posts `{ type: 'inbox-changed' }` to open windows, and `app.js` reloads on that message. Also refresh on app start, because **Inbox only** messages send no push.

## 3. Report opens and clicks (planned)

**Not implemented.** There is no open or click endpoint yet, and nothing is reported back to CIJ as an interaction. Marking an item as read (`POST /inbox/{id}/read`) is stored only in the inbox.

The planned design:
- Add Inbox API endpoints for "opened" and "action clicked".
- Write them to the planned Dataverse message log ([data-model.md](data-model.md#planned-message-log-elastic-table)).
- Forward them to CIJ as interactions. **Unverified** which CIJ custom-channel mechanism applies; to be confirmed against Microsoft Learn before implementation.

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
7. **Opens and clicks** (section 3), if you need engagement reporting in CIJ.
8. **Production tiers** ([production sizing](production-sizing.md)).
