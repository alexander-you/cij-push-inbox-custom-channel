# Configuration

## Environment variable

| Display name | Schema name | Type | Value |
|---|---|---|---|
| Push channel service address | `alex_ChannelApiBaseUrl` | Text | Base URL of the Azure Channel API including `/api`, for example `https://func-contoso.azurewebsites.net/api` (the azd output `API_BASE_URL`). Set by `deploy/connect-azure.sh`. No default. |

You can set the value in either of two ways ([Environment variables](https://learn.microsoft.com/power-apps/maker/data-platform/environmentvariables)):
- In the maker portal, under **Solutions > Customer Insights Journeys - Push Inbox Channel > Environment variables**.
- At import, with a deployment settings file (`pac solution create-settings`, then `pac solution import --settings-file`).

While the value is empty, every send returns `NotSent` with the reason `ChannelApiNotConfigured`, and CIJ doesn't retry.

## Channel instance (push sender)

In Customer Insights - Journeys settings, open the custom channels area. Add a **Push + Inbox** channel instance, and fill the *Push sender* form. ([Custom channel instance](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-define-custom-channel-instance))

| Field | Required | Meaning |
|---|---|---|
| Name | Yes | Friendly name that marketers see when they choose the channel in a journey. |
| App key | Yes | Identifies the mobile or web app that receives the messages. It selects the Notification Hubs tag `app:{appKey}` and the inbox namespace. Use only letters, numbers and hyphens. ([Notification Hubs tag expressions](https://learn.microsoft.com/azure/notification-hubs/notification-hubs-tags-segment-push-message)) |
| Default inbox expiry (days) | No | Days a message stays in the inbox when the message doesn't set its own value. 1–180. |
| Channel API key | Test only | Function key the plug-in sends to the Channel API. The column is secured, so only users with a field security profile can see it. For production, use Power Platform managed identity and leave this column empty. ([Column-level security](https://learn.microsoft.com/power-platform/admin/field-level-security), [Power Platform managed identity](https://learn.microsoft.com/power-platform/admin/set-up-managed-identity)) |

The sender is the channel instance's contact point, which CIJ passes as `From` in the outbound payload. ([Custom channel custom APIs](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-custom-channel-custom-api))

## Message parts

Marketers fill these in the built-in message editor of the journey tile. ([Message parts](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-custom-channel-message-parts))

| Part | Required | Notes |
|---|---|---|
| Title | Yes | Up to 65 characters. Notification title and inbox subject. |
| Message | Yes | Up to 1000 characters. The notification may shorten it; the inbox shows it in full. |
| Image | No | Choose an image from the asset library. It must be an image asset; anything else is sent without an image. Upload a new asset rather than replacing one: CDN copies of a replaced asset can stay stale for up to 30 days ([Asset library](https://learn.microsoft.com/dynamics365/customer-insights/journeys/upload-images-files)). |
| Tap action link | No | Web page or app deep link. CIJ may shorten links in custom channel messages. ([Create custom channels](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-create-custom-channels)) |
| Delivery mode | Yes | *Alert + Inbox*, *Inbox only* or *Alert only*. |
| Inbox expiry (days) | No | 1–180. Empty means the channel default. |
| Replace key | No | A newer message with the same key replaces the recipient's earlier inbox item. |

## Access

| Who | What they need |
|---|---|
| CIJ service | Read on `alex_pushchannelinstance`, granted by `deploy/post-import.sh` ([installation](installation.md#3-grant-cij-read-access-to-the-channel-configuration)). ([Channel instance account and Extensible role](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-define-channel-instance-account)) |
| Admins who configure channels | Create, read, write and delete on `alex_pushchannelinstance`. Until the solution's own roles ship, use System Administrator or System Customizer. |
| Azure backend | An application user for the Function App's managed identity, with Basic User (to run the `alex_PushDeliveryReport` custom API) and **Push Inbox Log Writer** (to write the message log). Created by `deploy/connect-azure.sh`. ([Application users](https://learn.microsoft.com/power-platform/admin/manage-application-users)) |

## Azure keys and secrets

| Secret | Where it lives |
|---|---|
| Notification Hubs connection string (Listen + Send rule) | Key Vault secret, read by the Function App through a Key Vault reference. ([Key Vault references](https://learn.microsoft.com/azure/app-service/app-service-key-vault-references)) |
| VAPID private key | The Notification Hub's browser credential, and the azd environment on the deploying machine (`.azure/`, git-ignored). ([Notification Hubs browser push](https://learn.microsoft.com/azure/notification-hubs/browser-push)) |
| FCM v1 service account key, APNs `.p8` key | Only if you add native apps: the notification hub's Google (FCM v1) and Apple (APNS) settings, set by you in the portal ([installation §4.4](installation.md#44-native-push-credentials-android-and-ios)). |
| Function key | Function App host keys. For test environments it's also copied to `alex_channelapikey` (column-secured). |
| Storage, Service Bus, Cosmos DB, Dataverse | No secrets: the Function App's managed identity is used. |

Details and residual risks: [security.md](security.md).

## Function App settings you may change

| Setting | Default | Meaning |
|---|---|---|
| `PUSH_PLATFORMS` | `browser,fcmv1,apns` (when unset) | Platforms the Dispatcher sends to, comma-separated. A platform without credentials on the hub is skipped. Unknown values stop the Dispatcher at startup. |
| `MESSAGE_LOG_TTL_DAYS`, `MESSAGE_LOG_TEST_TTL_DAYS` | `180`, `7` | Message log retention ([data model §8](data-model.md#8-message-log-elastic-table-alex_pushmessagelog)). |
