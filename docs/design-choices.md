# Design choices

The key choices behind the solution, with the public documentation that supports each one.

## Channel and Dataverse

**One custom channel, three delivery modes.** The channel is one CIJ **Custom** channel, *Push + Inbox*, with a single journey tile. The `deliveryMode` message part selects *Alert + Inbox*, *Inbox only* or *Alert only*. Messages go out through an outbound custom API, and status comes back through a delivery-report API. ([Channel definition](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-define-channel-definition), [Message parts](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-custom-channel-message-parts), [Custom channel custom APIs](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-custom-channel-custom-api))

**Standard config table with a hand-authored polymorphic relationship.** `alex_pushchannelinstance` is a Standard, user-owned table. CIJ links it through `msdyn_channelinstance.msdyn_extendedentityid` with `CascadeDelete=RemoveLink`. Elastic tables support only `Cascade.None`, so they can't hold this relationship. The relationship XML is written by hand because CIJ documents that the XrmToolBox Polymorphic Lookup Creator output doesn't work. ([Custom channel instance](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-define-custom-channel-instance), [Microsoft sample solution](https://download.microsoft.com/download/44685e3a-8b09-4caa-be78-12bc6867c8d1/SampleCustomChannel.zip), [Create elastic tables with code](https://learn.microsoft.com/power-apps/developer/data-platform/create-elastic-tables))

**CIJ read access through the Marketing Services User Extensible role.** CIJ reads the config table during submission. `deploy/post-import.sh` grants Read at Organization depth to *Marketing Services User Extensible Role*. That role is customizable, and the CIJ service user holds it. The script also tries *Cxp Channel Definitions Services User*, but that role is non-customizable in many environments. The solution doesn't change managed properties on Microsoft roles. ([Channel instance account and Extensible role](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-define-channel-instance-account), [Custom channel instance](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-define-custom-channel-instance), [AddPrivilegesRole](https://learn.microsoft.com/power-apps/developer/data-platform/webapi/reference/addprivilegesrole), [Managed properties](https://learn.microsoft.com/power-platform/alm/managed-properties-alm))

**Publisher, prefix and GUIDs.**
- The publisher prefix is `alex`, and every component lives in one solution, `AlexPushInboxChannel`.
- The solution is unmanaged in source and managed for release.
- All GUIDs are new and lowercase; none are reused from Microsoft's sample.

([Solution publisher](https://learn.microsoft.com/power-apps/maker/data-platform/create-solution#solution-publisher), [Solution concepts](https://learn.microsoft.com/power-platform/alm/solution-concepts-alm), [Channel definition](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-define-channel-definition), [Message parts](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-custom-channel-message-parts))

**Seven message parts, built-in editor.**
- Parts: `title`, `body`, `imageId`, `actionUrl`, `deliveryMode`, `inboxExpiryDays` and `replaceKey`.
- `imageId` is the `image` type, so marketers pick from the CIJ asset library instead of typing a URL. The part returns a file ID, and the outbound plug-in turns it into the asset's public URL ([Message parts](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-custom-channel-message-parts), [Asset library](https://learn.microsoft.com/dynamics365/customer-insights/journeys/upload-images-files)). Earlier versions used a plain-text URL part; that was replaced because typed URLs bypass the library.
- There's no channel account table (`msdyn_supportsaccount = 0`).

([Message parts](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-custom-channel-message-parts), [Message editor](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-custom-channel-message-editor), [Custom channels in CIJ](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-extend-outreach-custom-channels))

**Plain-language descriptions everywhere.** Every table, column, environment variable, channel definition and message part has a user-facing description in the solution metadata. ([Column descriptions](https://learn.microsoft.com/power-apps/maker/data-platform/create-edit-field-portal), [Custom channel localization](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-custom-channel-localization-admin))

## Azure backend

**Serverless, queue-buffered backend.** The backend uses Azure Functions (Channel API, Dispatcher, Inbox API), a Service Bus queue for retries and dead-lettering, Cosmos DB, Notification Hubs, Key Vault and Application Insights. It is deployed with `azd` and Bicep. ([Notification Hubs bindings for Functions](https://learn.microsoft.com/azure/azure-functions/functions-bindings-notification-hubs), [Service Bus overview](https://learn.microsoft.com/azure/service-bus-messaging/service-bus-messaging-overview), [Key Vault references](https://learn.microsoft.com/azure/app-service/app-service-key-vault-references), [azd templates](https://learn.microsoft.com/azure/developer/azure-developer-cli/azd-templates), [azd for Azure Functions](https://learn.microsoft.com/azure/developer/azure-developer-cli/function-app-deployments))

**Inbox in Cosmos DB, not Dataverse.**
- Inbox items are high-volume, expiring, per-recipient data. They live in Cosmos DB, partitioned by `recipientKey`, with a per-item TTL.
- CIJ stores delivery status and trigger events. v1 has no Dataverse message log.
- **Planned (required): a Dataverse message log as an elastic table** (`alex_pushmessagelog`): one row per message event, written by the backend, deleted by TTL. Where it appears in the UI is not decided. Schema: [data-model.md](data-model.md#planned-message-log-elastic-table).

([Cosmos DB time to live](https://learn.microsoft.com/azure/cosmos-db/how-to-time-to-live), [Cosmos DB partitioning](https://learn.microsoft.com/azure/cosmos-db/partitioning-overview), [Elastic tables](https://learn.microsoft.com/power-apps/maker/data-platform/create-edit-elastic-tables), [Elastic tables for developers](https://learn.microsoft.com/power-apps/developer/data-platform/elastic-tables), [Custom channel custom APIs](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-custom-channel-custom-api))

**Fast, idempotent outbound path.**
- The plug-in posts to the Channel API with a 15-second timeout.
- The API de-duplicates on `IdempotencyId`, queues the message and returns 202.
- `Sent` means Azure accepted the message. `NotSent` is used only for non-retriable errors.

([Custom channel custom APIs](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-custom-channel-custom-api), [Plug-in external call timeout](https://learn.microsoft.com/power-apps/developer/data-platform/best-practices/business-logic/set-timeout-for-external-calls-from-plug-ins))

**Inbox expiry capped at 180 days.** Expiry is a per-item Cosmos DB `ttl` (container `defaultTtl = -1`), with a maximum of 180 days. ([Cosmos DB time to live](https://learn.microsoft.com/azure/cosmos-db/how-to-time-to-live))

**Recipient identity and server-set tags.**
- `recipientKey = lower({UserEntityType}_{UserId})` comes from the journey context.
- Devices are linked to a contact through a verified claim and a Dataverse alternate key.
- Only the Inbox API sets Notification Hubs tags (`rk:…`, `app:…`).

([Custom channel custom APIs](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-custom-channel-custom-api), [Alternate keys](https://learn.microsoft.com/power-apps/developer/data-platform/define-alternate-keys-entity), [Notification Hubs installations](https://learn.microsoft.com/azure/notification-hubs/notification-hubs-push-notification-registration-management), [Notification Hubs tag expressions](https://learn.microsoft.com/azure/notification-hubs/notification-hubs-tags-segment-push-message))

**Authentication and secrets.**
- Plug-in → Azure: a column-secured function key for test environments, and Power Platform managed identity for production.
- Azure → Dataverse: a server-to-server application user.
- Secrets: Key Vault references.
- No secret-type environment variables are read from plug-ins.

([Power Platform managed identity](https://learn.microsoft.com/power-platform/admin/set-up-managed-identity), [Column-level security](https://learn.microsoft.com/power-platform/admin/field-level-security), [Application users](https://learn.microsoft.com/power-platform/admin/manage-application-users), [Server-to-server authentication](https://learn.microsoft.com/power-apps/developer/data-platform/use-single-tenant-server-server-authentication), [Key Vault references](https://learn.microsoft.com/azure/app-service/app-service-key-vault-references), [Key Vault secret environment variables](https://learn.microsoft.com/power-apps/maker/data-platform/environmentvariables-azure-key-vault-secrets), [Environment variables](https://learn.microsoft.com/power-apps/maker/data-platform/environmentvariables))

**Delivery status and interactions.** `Delivered` means the inbox item was stored, or Notification Hubs accepted an alert-only push. Inbox *opened*, *clicked* and *dismissed* events are raised as CIJ custom triggers, so journeys can branch on them. ([Custom channel custom APIs](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-custom-channel-custom-api), [Custom triggers](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-custom-triggers), [Raise triggers](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-custom-actions))

**Notification Hubs tier.** Use the Free tier for development and Standard for production (telemetry, PNS feedback, scale). ([Notification Hubs telemetry](https://learn.microsoft.com/rest/api/notificationhubs/get-notification-message-telemetry), [PNS feedback](https://learn.microsoft.com/rest/api/notificationhubs/get-pns-feedback), [Notification Hubs pricing](https://azure.microsoft.com/pricing/details/notification-hubs/))

## Testing

**Test in a desktop browser, with no mobile dev environment.**
- A reference PWA registers a browser (Web Push) installation in Notification Hubs and receives real notifications on a desktop.
- Playwright and DevTools push simulation cover the inbox UI.
- Recorded CIJ payloads drive the contract tests.
- A real phone is optional: Android Chrome, or an iOS 16.4+ Home Screen web app.

([Notification Hubs browser push](https://learn.microsoft.com/azure/notification-hubs/browser-push), [Notification Hubs Debug Send](https://learn.microsoft.com/rest/api/notificationhubs/notification-hubs/debug-send), [DevTools for PWAs](https://developer.chrome.com/docs/devtools/progressive-web-apps), [Playwright permissions](https://playwright.dev/docs/api/class-browsercontext#browser-context-grant-permissions), [Web Push on iOS](https://webkit.org/blog/13878/web-push-for-web-apps-on-ios-and-ipados/))

## Languages

**English and Hebrew, including right-to-left.**
- English (1033) is the base language. Hebrew (1037) is delivered through the Dataverse translation file and channel definition locale records.
- Message content is UTF-8 JSON end to end.
- Notifications and the inbox render with `dir="auto"` and `lang`.

See [localization.md](localization.md). ([Translate table and column text](https://learn.microsoft.com/power-apps/maker/data-platform/export-customized-entity-field-text-translation), [Custom channel localization](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-custom-channel-localization-admin), [RFC 8259 UTF-8](https://www.rfc-editor.org/rfc/rfc8259#section-8.1), [Notification.dir](https://developer.mozilla.org/docs/Web/API/Notification/dir), [HTML dir attribute](https://developer.mozilla.org/docs/Web/HTML/Global_attributes/dir), [W3C: dir in HTML](https://www.w3.org/International/questions/qa-html-dir))
