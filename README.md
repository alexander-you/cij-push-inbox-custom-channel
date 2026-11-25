# Push + Inbox custom channel for Dynamics 365 Customer Insights - Journeys

Customer Insights - Journeys (CIJ) can send push notifications, but its native push channel has no persistent in-app inbox. This solution adds a CIJ **custom channel**, *Push + Inbox*. One journey tile sends a push alert, an inbox message that stays in the app's message center until it expires, or both. The backend is Azure: Functions, Service Bus, Cosmos DB and Notification Hubs.

- **Delivery modes:** *Alert + Inbox*, *Inbox only* or *Alert only*, chosen per message.
- **Reporting:** delivery status (`Delivered` / `NotDelivered`) is reported back to CIJ. *Delivered* means the inbox item was stored or Notification Hubs accepted the push, not that a device displayed it. **Clicks** are counted by CIJ link tracking: a tap on the notification opens the tracked action link, so it appears under *Unique clicks / Total clicks* and can drive an *If/then* "Link Clicked" branch ([how to report clicks](docs/developer-integration.md#3-how-to-report-clicks)). Inbox *opened* events aren't reported to CIJ.
- **Platforms:** web push (PWA), verified. Android (FCM v1) and iOS (APNs) are implemented in the backend through Azure Notification Hubs but **not yet tested on devices**; you add the FCM v1 and APNs credentials to your hub.
- **Languages:** full English and Hebrew, including right-to-left.
- **Installation:** a single Dataverse solution plus a post-import script, and an `azd` template for Azure.

```mermaid
flowchart LR
  J[CIJ journey] --> P[Outbound custom API] --> C[Channel API] --> Q[(Service Bus)] --> D[Dispatcher]
  D --> I[(Cosmos DB inbox)]
  D --> N[Notification Hubs] --> A[App / PWA]
  D -->|status| J
  A -->|tap: CIJ tracked link| J
```

## Status

| Part | Status |
|---|---|
| Dataverse solution `AlexPushInboxChannel` 1.1.0.0: channel definition, 7 message parts, config table and form, custom APIs, plug-ins, environment variable, message log, English and Hebrew labels | Available as managed and unmanaged packages in [`release/1.1.0.0`](release/README.md) |
| Azure backend (`azd up`): Channel API with idempotency, Service Bus, Dispatcher, Cosmos DB inbox, Notification Hubs Web Push, delivery reports to CIJ | Available (preview) |
| Native push in the backend: FCM v1 and APNs device registration and sends | Available (preview), **not tested on devices**; no reference native app. See [developer integration](docs/developer-integration.md#native-apps-registration-contract-backend-implemented-clients-untested). |
| Reference PWA inbox (Static Web Apps) and Inbox API | Available as a **test harness**: the Inbox API is anonymous. See [known limitations](docs/known-limitations.md). |
| Dataverse message log `alex_pushmessagelog` (elastic table, async writer, Log Writer / Log Reader roles) | Available (preview). See [data model](docs/data-model.md#8-message-log-elastic-table-alex_pushmessagelog). |
| End-user authentication, interaction triggers, a reference native app | Planned (see roadmap) |

## Documentation

**Start here:** the [implementer guide](docs/implementer-guide.md) walks through the whole path, from prerequisites to a verified journey send.

- [Implementer guide](docs/implementer-guide.md)
- [Installation](docs/installation.md) (including upgrade and uninstall)
- [Configuration](docs/configuration.md)
- [Architecture](docs/architecture.md)
- [Data model reference](docs/data-model.md)
- [Localization (English / Hebrew, RTL)](docs/localization.md)
- [Design choices](docs/design-choices.md)
- [Known limitations](docs/known-limitations.md)
- [Troubleshooting](docs/troubleshooting.md)
- [Security considerations](docs/security.md)
- [Production sizing](docs/production-sizing.md)
- [Developer integration (customer apps)](docs/developer-integration.md)
- [Contributing](CONTRIBUTING.md)

## Repository layout

```text
release/<version>/               Managed and unmanaged solution packages (see release/README.md)
solution/AlexPushInboxChannel/   Dataverse solution source (pac solution unpack format)
solution/src/                    Plug-in source (.NET Framework 4.8)
deploy/post-import.sh            Grants CIJ read access to the channel configuration
deploy/connect-azure.sh          Connects Dataverse to the Azure backend after azd up
azure.yaml, infra/               azd template (Bicep)
src/PushInbox.Functions/         Azure Functions: Channel API, Dispatcher, Inbox API (.NET 10 isolated)
src/web/                         Reference PWA inbox (Web Push)
tools/l10n/                      Translation export, fill, import and verify
tests/solution/                  Solution structure and label check
tests/PushInbox.Functions.Tests/ Backend unit tests (dotnet test)
.githooks/                       Pre-commit hook (enable with tools/setup-hooks.sh)
```

## Roadmap

1. **Done (preview):** Azure core and Web Push. Channel API with idempotency, Service Bus, Dispatcher, Cosmos DB inbox, Notification Hubs, delivery reports to CIJ, reference PWA, `azd up`.
2. **Done – Dataverse message log:** one row per message event in an elastic table (`alex_pushmessagelog`), including inbox Read and app-side Clicked, for audit and reporting. See [data-model.md](docs/data-model.md#8-message-log-elastic-table-alex_pushmessagelog), including the options for building summaries and reports on top of it.
3. **Security:** authenticated Inbox API (end-user identity instead of `recipientKey` in the query string), Power Platform managed identity for the plug-in instead of the function key, solution security roles.
4. **Interactions:** clicks are done through CIJ link tracking. Still open: an *opened* custom trigger if journeys must branch on app opens; `replaceKey`.
5. **Images:** pick the image from the CIJ asset library instead of typing a URL.
6. **Mobile apps:** backend done (FCM v1 and APNs registration and sends, [developer integration guide](docs/developer-integration.md)). Still open: device testing and a reference native app.
7. **Release:** managed and unmanaged packages are done ([1.1.0.0](release/README.md)). Still open: a sample journey, dead-letter alerting, and delete by recipient.

## License

[MIT](LICENSE).
