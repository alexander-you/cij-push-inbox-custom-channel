# Configuration

## Environment variable

| Display name | Schema name | Type | Value |
|---|---|---|---|
| Push channel service address | `alex_ChannelApiBaseUrl` | Text | Base URL of the Azure Channel API, for example `https://contoso-push.azurewebsites.net`. No default. |

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
| Image link | No | HTTPS URL. |
| Tap action link | No | Web page or app deep link. CIJ may shorten links in custom channel messages. ([Create custom channels](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-create-custom-channels)) |
| Delivery mode | Yes | *Alert + Inbox*, *Inbox only* or *Alert only*. |
| Inbox expiry (days) | No | 1–180. Empty means the channel default. |
| Replace key | No | A newer message with the same key replaces the recipient's earlier inbox item. |

## Access

| Who | What they need |
|---|---|
| CIJ service | Read on `alex_pushchannelinstance`, granted by `deploy/post-import.sh` ([installation](installation.md#3-grant-cij-read-access-to-the-channel-configuration)). ([Channel instance account and Extensible role](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-define-channel-instance-account)) |
| Admins who configure channels | Create, read, write and delete on `alex_pushchannelinstance`. Until the solution's own roles ship, use System Administrator or System Customizer. |
| Azure backend | A server-to-server application user that can run the `alex_PushDeliveryReport` custom API. ([Application users](https://learn.microsoft.com/power-platform/admin/manage-application-users), [Server-to-server authentication](https://learn.microsoft.com/power-apps/developer/data-platform/use-single-tenant-server-server-authentication)) |

## Azure keys and secrets

The planned Azure backend keeps the Notification Hubs connection string, the VAPID key pair for browser push, and its Dataverse credentials in Key Vault, accessed through Key Vault references. ([Key Vault references](https://learn.microsoft.com/azure/app-service/app-service-key-vault-references), [Notification Hubs browser push](https://learn.microsoft.com/azure/notification-hubs/browser-push)) Don't store these in Dataverse.
