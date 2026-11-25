# Troubleshooting

These are the issues we ran into while building and testing the solution, with the cause and the fix for each. To see what happened to a message, start with **Application Insights → Logs → `traces`** for the Function App, and search for the message ID or the CIJ `RequestId`. Recipient keys aren't logged; only a hashed `RecipientLogId` is ([SEC-15](security.md#sec-15-logging-and-monitoring-without-personal-data)).

## Trace messages for one send

| Trace (abridged) | Component | Meaning |
|---|---|---|
| `Accepted {MessageId} … request {RequestId}` | Channel API | CIJ's send was validated and queued. An empty `request` means a test send. |
| `Duplicate IdempotencyId …; returning MessageId …` | Channel API | A CIJ retry of a message that was already queued. Nothing is sent again. |
| `IdempotencyId … is still Pending; re-enqueuing …` | Channel API | A retry of a message whose first enqueue failed. It is queued again. |
| `Inbox item … stored …, ttl …s` | Dispatcher | The inbox item was written. |
| `Notification Hubs send {Status} … success N failure M` | Dispatcher | The push was handed to Notification Hubs. `success`/`failure` are filled only for test sends (see below). |
| `Push step already done for … ; not pushing again` | Dispatcher | A Service Bus redelivery. The push was skipped to avoid a duplicate. |
| `Delivery report {Status} sent for request …: {Result}` | Dispatcher | The status was reported to CIJ. `Result` is the HTTP result. |
| `Delivery report skipped …: the CIJ request has no RequestId (test send)` | Dispatcher | Expected for test sends. |
| `Delivery report attempt N failed …` | Dispatcher | The report is retried; the push isn't repeated. |
| `Message log row … {Type} written …` | LogWriter | A row was written to `alex_pushmessagelog`. |

## CIJ and journeys

| Symptom | Cause | Fix |
|---|---|---|
| The tile shows every entry under **Processing**, with delivery rate 0% and "no data". | Analytics latency. In our tests, *Delivered* reports reached the tile's analytics tens of minutes after the send, even though the report call returned HTTP 200. | Wait, then check the trace `Delivery report Delivered sent for request …` and its HTTP result. Entries move to **Processed** when CIJ processes the *Delivered* report. |
| A test send shows no delivery status and adds no journey analytics. | CIJ test sends carry an empty `RequestId`, so there's nothing to report against ([Custom channel custom APIs](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-custom-channel-custom-api)). | Use a journey to test reporting. |
| Every send returns `NotSent` / `ChannelApiNotConfigured`. | The environment variable `alex_ChannelApiBaseUrl` has no value. | Run `deploy/connect-azure.sh`, or set the value ([configuration](configuration.md#environment-variable)). |
| The channel instance can't be selected, or CIJ can't read its configuration. | CIJ's service role has no Read on `alex_pushchannelinstance`. | Rerun `deploy/post-import.sh`, also after CIJ upgrades. |
| A delivery report that you call yourself returns HTTP 400. | The notification payload is missing `ChannelDefinitionId` or `From`. In our tests, CIJ rejected reports without them. | Send the full payload, as the Dispatcher does ([data model §3.5](data-model.md#35-custom-apis-custom-channel-custom-apis-microsoft-sample-solution-custom-api-columns)). |
| *If/then* can't branch on a specific link. | The designer offers only *Delivered*, *Link Clicked* and *Bounced* for custom channels. | Use analytics (Target URL) to tell links apart, or a custom trigger ([developer integration §3](developer-integration.md#3-how-to-report-clicks)). |

## Links and clicks

| Symptom | Cause | Fix |
|---|---|---|
| A link in the message body isn't clickable, or isn't counted. | The link has no scheme, for example `www.example.com`. CIJ tracks only `https://` links, and the reference inbox links only `https://` URLs. | Write full `https://` URLs ([Link tracking mechanics](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-link-tracking-mechanics)). |
| A tap on the notification opens an external website instead of the app. | The **Tap action link** points to that site. | Set the Tap action link to your app's own URL, for example `https://<your-app>/`, and put external links in the body ([developer integration §3](developer-integration.md#3-how-to-report-clicks)). |
| After a tap, the app opens but not on the right message. | A `#fragment` in the Tap action link was lost. CIJ's tracked link is an HTML page that redirects with JavaScript, not an HTTP redirect, so the fragment doesn't survive. | Don't rely on your own fragment. Read `msdynmkt_trackingcontext` from the query string **or** the fragment: it equals the inbox item's `requestId`. |
| The Tap action link contains a recipient key or a contact ID. | The same link goes to every recipient. | Keep it recipient-agnostic. The app knows its recipient from device registration ([developer integration §3](developer-integration.md#3-how-to-report-clicks)). |
| Clicks aren't counted. | Any of these: no Tap action link; the contact has no tracking consent; the latency hasn't passed yet. | See [Troubleshoot link tracking](https://learn.microsoft.com/troubleshoot/dynamics-365/customer-insights/journeys/tracking/troubleshoot-link-tracking). |

## Push and inbox

| Symptom | Cause | Fix |
|---|---|---|
| `Notification Hubs fcmv1 send 400` or `apns send 400` with *no target applications*. | The hub has no FCM v1 or APNs credentials. This is expected when you only use the PWA. The Dispatcher skips that platform and logs it as `NotConfigured`. | Add the credentials ([installation §4.4](installation.md#44-native-push-credentials-android-and-ios)), or set `PUSH_PLATFORMS=browser` ([configuration](configuration.md#function-app-settings-you-may-change)). |
| A send is *NotDelivered* with reason `NoPushPlatform`. | No platform in `PUSH_PLATFORMS` has credentials on the hub. | As above. |
| `Notification Hubs send … success 0` on a test send. | No browser is subscribed for that recipient and app key. The recipient formats can also differ, for example `+972-50-…` vs `+97250…`: hyphens are kept when normalizing. | Subscribe with exactly the recipient value CIJ sends ([developer integration, identifiers](developer-integration.md#identifiers)). |
| A journey send is *Delivered*, but no notification appeared. | *Delivered* means Notification Hubs accepted the push, not that the device showed it. On the Free tier, per-device results aren't available for real sends. | Check the browser's notification permission. Use the Standard tier for per-message telemetry ([Notification Hubs pricing](https://azure.microsoft.com/pricing/details/notification-hubs/)). |
| Two notifications arrived. | Usually two different sends, for example a test send plus a journey entry, or two journey entries. | Compare the `MessageId`/`RequestId` values in the traces. A redelivery of the same message logs `Push step already done`, not a second push. |
| The image doesn't show in the notification. | Android shows the image only when the notification is expanded. | Expand it; the inbox always shows the image ([known limitations](known-limitations.md)). |
| No notifications on iPhone. | iOS web push works only for web apps added to the Home Screen, on iOS 16.4 or later. | Add the PWA to the Home Screen ([Web Push on iOS](https://webkit.org/blog/13878/web-push-for-web-apps-on-ios-and-ipados/)). |

## Message log

| Symptom | Cause | Fix |
|---|---|---|
| No log rows at all. | The app user doesn't have **Push Inbox Log Writer**, or `MESSAGE_LOG_QUEUE` / `DATAVERSE_URL` isn't set. | Rerun `deploy/connect-azure.sh`. Look for `Message log event … dropped` or `not queued` in the traces. |
| The log rows appear late. | The log is written asynchronously, and Dataverse throttling is retried by Service Bus. | Wait. Check the `messagelog` queue's dead-letter count ([data model §8.4](data-model.md#84-write-path)). |
| A row has no contact. | The contact lookup couldn't be set: the trace says `contact link failed`. This happens with test sends and with recipients that aren't contacts. | Expected. Use `alex_recipientlogid` to find the row. |
| A user can't see the log. | The user doesn't have **Push Inbox Log Reader**. | Assign the role ([installation](installation.md#43-connect-dataverse-to-azure)). |

## Deployment and packaging

| Symptom | Cause | Fix |
|---|---|---|
| Creating the Cosmos DB account fails with `ServiceUnavailable` ("high demand"). | The region has no capacity. | Set `COSMOS_LOCATION` to another region that meets your data-residency needs ([installation §4.1](installation.md#41-create-the-azd-environment)). |
| `azd deploy` fails during the .NET publish step on macOS. | The path is a symbolic link, such as `/tmp`. | Run from the real path, for example `/private/tmp/...`. |
| `post-import.sh` warns that a Cxp role is locked (`0x8004023b`). | Microsoft locks that role in some environments. | Expected. CIJ uses Marketing Services User Extensible Role. |
| The managed import fails because the solution is already installed. | The environment has the unmanaged version of this solution. | Use a different environment for the managed package ([Solution concepts](https://learn.microsoft.com/power-platform/alm/solution-concepts-alm)). |
| Your exported solution shows non-English `default` labels in `customapi.xml` or in environment variable definitions. | Observed: the export takes the default label from the exporting user's UI language. | Export with the user's UI language set to English, or keep the source files' English defaults ([Personal options (language)](https://learn.microsoft.com/power-apps/user/set-personal-options)). |
