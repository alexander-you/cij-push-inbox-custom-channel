# Installation

## Prerequisites

- **Customer Insights - Journeys** installed in the target environment. The `msdyn_channeldefinition` and `msdyn_channelinstance` tables must exist. ([Custom channels in CIJ](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-extend-outreach-custom-channels))
- **System Administrator** role in the target environment.
- **Tools:**
  - [Power Platform CLI](https://learn.microsoft.com/power-platform/developer/cli/introduction) (`pac`)
  - [Azure CLI](https://learn.microsoft.com/cli/azure/install-azure-cli) (`az login`; used for the post-import step)
  - Python 3
- **Hebrew (optional):** enable it in the environment to use the Hebrew labels. ([Enable languages](https://learn.microsoft.com/power-platform/admin/enable-languages))

## 1. Get the solution package

Choose one of these packages:

| Package | Use it for |
|---|---|
| `release/<version>/AlexPushInboxChannel_<version>_managed.zip` | Test, UAT and production environments. |
| `release/<version>/AlexPushInboxChannel_<version>.zip` | A development environment where you'll customize the solution. |
| Built from source (below) | Contributing to this repository. |

[release/README.md](../release/README.md) lists the checksums and changes for each version ([Solution concepts](https://learn.microsoft.com/power-platform/alm/solution-concepts-alm)).

You can't import the managed package into an environment that already has this solution as unmanaged ([Solution concepts](https://learn.microsoft.com/power-platform/alm/solution-concepts-alm)).

**To build from source:** the unpacked solution in `solution/AlexPushInboxChannel/` is the source of truth ([SolutionPackager](https://learn.microsoft.com/power-platform/alm/solution-packager-tool)).

```bash
python3 tests/solution/check_solution.py
pac solution pack --zipfile out/AlexPushInboxChannel.zip --folder solution/AlexPushInboxChannel --packagetype Unmanaged
```

To ship your own customized version, do this in your development environment:
1. Import the unmanaged package.
2. Make your changes.
3. Export the solution as managed with `pac solution export --managed`.

## 2. Import

```bash
pac solution import --environment https://<org>.crm.dynamics.com --path release/1.1.0.0/AlexPushInboxChannel_1_1_0_0_managed.zip --async
```

- `pac` uses the active auth profile unless you pass `--environment`. Always pass it, so that you don't import into the wrong environment ([pac solution import](https://learn.microsoft.com/power-platform/developer/cli/reference/solution#pac-solution-import)).
- The environment variable `alex_ChannelApiBaseUrl` is imported with no value. Step 4.3 sets it. You can also set it at import time with a deployment settings file ([configuration](configuration.md#environment-variable)).

## 3. Grant CIJ read access to the channel configuration

```bash
deploy/post-import.sh https://<org>.crm.dynamics.com
```

The script grants Read (Organization) on `alex_pushchannelinstance` to **Marketing Services User Extensible Role**, as Microsoft documents. ([Channel instance account and Extensible role](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-define-channel-instance-account)) ([AddPrivilegesRole](https://learn.microsoft.com/power-apps/developer/data-platform/webapi/reference/addprivilegesrole))

- **Locked Cxp role:** it also tries **Cxp Channel Definitions Services User** ([Custom channel instance](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-define-custom-channel-instance)). Where that role is locked (`0x8004023b`), it prints a warning and continues.
- **Idempotent:** the script is safe to run more than once. Rerun it after Customer Insights - Journeys upgrades.

## 4. Deploy the Azure backend

The backend is an [Azure Developer CLI](https://learn.microsoft.com/azure/developer/azure-developer-cli/overview) template (`azure.yaml`, `infra/`). One `azd up` creates the resources below, builds the Functions, and publishes the reference PWA. ([azd for Azure Functions](https://learn.microsoft.com/azure/developer/azure-developer-cli/function-app-deployments))

| Resource | SKU |
|---|---|
| Function App (Channel API, Dispatcher, Inbox API), .NET 10 isolated | Flex Consumption (FC1) |
| Service Bus namespace and `dispatch` queue | Basic |
| Cosmos DB for NoSQL (`inboxItems`, `dispatches`) | Serverless |
| Notification Hubs namespace and hub, with Web Push (VAPID) credentials | Free |
| Key Vault (Notification Hubs connection string) | Standard |
| Storage account (Functions host and deployment package) | Standard LRS |
| Application Insights and Log Analytics | Pay-as-you-go |
| Static Web Apps (reference PWA) | Free |
| Virtual network, private endpoints (Cosmos DB, Key Vault, blob), private DNS zones, Network Security Perimeter (storage) | — |
| Budget alert on the resource group | — |

Additional prerequisites: [Azure Developer CLI](https://learn.microsoft.com/azure/developer/azure-developer-cli/install-azd), [.NET 10 SDK](https://dotnet.microsoft.com/download), Node.js (to generate VAPID keys), and Owner or User Access Administrator on the subscription (the template creates role assignments).

### 4.1 Create the azd environment

```bash
azd auth login                      # or: azd config set auth.useAzCliAuth true
azd env new <env-name> --location westeurope
azd env set AZURE_RESOURCE_GROUP_NAME rg-<env-name>
azd env set DATAVERSE_URL https://<org>.crm.dynamics.com
azd env set BUDGET_CONTACT_EMAILS "ops@contoso.com"        # comma-separated; empty = no budget alert
azd env set COSMOS_LOCATION ""                              # optional, see below

# Web Push (VAPID) key pair, stored only in the azd environment and the Notification Hub
npx web-push generate-vapid-keys --json
azd env set VAPID_PUBLIC_KEY  <publicKey>
azd env set VAPID_PRIVATE_KEY <privateKey>
azd env set VAPID_SUBJECT     mailto:push-admin@contoso.com
```

- **VAPID keys:** Notification Hubs needs the application server key pair and a subject to send Web Push. ([Notification Hubs browser push](https://learn.microsoft.com/azure/notification-hubs/browser-push), [web-push CLI](https://github.com/web-push-libs/web-push#command-line)) Keep the private key out of source control; `.azure/` is git-ignored.
- **`COSMOS_LOCATION`:** leave it empty to use the main region. Set another region if Cosmos DB account creation fails with `ServiceUnavailable` ("high demand") in the main region. We hit this in West Europe and North Europe and deployed Cosmos DB to France Central. Pick a region that meets your data-residency requirements ([security](security.md)).

### 4.2 Provision and deploy

```bash
azd up
```

The `postprovision` hook writes `src/web/config.js` (the PWA's API address) before the PWA is published. Outputs: `API_BASE_URL`, `WEB_URL`, `FUNCTION_APP_NAME`, `FUNCTION_PRINCIPAL_ID`.

- **Identity-based connections only.** The Function App uses its system-assigned managed identity with Azure RBAC for Storage, Service Bus, Cosmos DB (data-plane role) and Key Vault. No storage, Service Bus or Cosmos DB keys are stored. ([Identity-based connections in Functions](https://learn.microsoft.com/azure/azure-functions/functions-reference#common-properties-for-identity-based-connections))
- **Private data services.** Cosmos DB and Key Vault are reached through private endpoints from the Function App's virtual network integration. ([Flex Consumption networking](https://learn.microsoft.com/azure/azure-functions/flex-consumption-plan#virtual-network-integration), [Private endpoints](https://learn.microsoft.com/azure/private-link/private-endpoint-overview))
- **Storage and policy-restricted tenants.** Many enterprise tenants enforce Azure Policy that sets `publicNetworkAccess` to `Disabled` on storage. Flex Consumption still needs its deployment container reachable for `azd deploy`, so the template places the storage account in a Network Security Perimeter with `publicNetworkAccess = SecuredByPerimeter` and an inbound rule for the deploying subscription. ([Network Security Perimeter](https://learn.microsoft.com/azure/private-link/network-security-perimeter-concepts), [Storage and NSP](https://learn.microsoft.com/azure/storage/common/storage-network-security-perimeter), [`publicNetworkAccess` values](https://learn.microsoft.com/azure/templates/microsoft.storage/storageaccounts))
- **On macOS**, run `azd` from a path without a symbolic link (for example `/private/tmp/...` instead of `/tmp/...`); the .NET publish step failed from a symlinked path in our tests.

### 4.3 Connect Dataverse to Azure

```bash
deploy/connect-azure.sh https://<org>.crm.dynamics.com [<channel instance id>]
```

The script is idempotent. It:

1. Sets the environment variable value `alex_ChannelApiBaseUrl` to `API_BASE_URL`. ([Environment variables](https://learn.microsoft.com/power-apps/maker/data-platform/environmentvariables))
2. Creates a Dataverse **application user** for the Function App's managed identity and gives it **Basic User**, so the dispatcher can call the `alex_PushDeliveryReport` custom API. ([Application users](https://learn.microsoft.com/power-platform/admin/manage-application-users))
3. Gives the same application user the **Push Inbox Log Writer** role, so the backend can write the message log ([data model §8](data-model.md#8-message-log-elastic-table-alex_pushmessagelog)).
4. Optional, **test environments only**: writes the default function key into `alex_channelapikey` on the given channel instance. Create the channel instance first ([configuration](configuration.md#channel-instance-push-sender)). For production, see [security](security.md).

**Marketers and admins who read the message log** need the **Push Inbox Log Reader** role. Assign it in the Power Platform admin center ([Assign a security role to a user](https://learn.microsoft.com/power-platform/admin/assign-security-roles)).

### 4.4 Native push credentials (Android and iOS)

Only needed if your own Android or iOS app receives the push. The template configures Web Push only. Add the native credentials yourself, in your own notification hub, from your own Firebase project and Apple developer account:

| Platform | What you need | Where to set it |
|---|---|---|
| Android (FCM v1) | A Firebase **service account** private key (JSON): project ID, private key and client email | Azure portal > the notification hub > **Settings > Google (FCM v1)**, or the management API ([FCM v1 in Notification Hubs](https://learn.microsoft.com/azure/notification-hubs/notification-hubs-gcm-to-fcm), [REST and management API](https://learn.microsoft.com/azure/notification-hubs/firebase-migration-rest)) |
| iOS (APNs) | **Token-based** authentication: key ID, the `.p8` key, team ID and bundle ID. Choose *Sandbox* for development builds and *Production* for store builds | Azure portal > the notification hub > **Settings > Apple (APNS)** ([Configure APNs in Notification Hubs](https://learn.microsoft.com/azure/notification-hubs/configure-apple-push-notification-service)) |

- **Use token authentication for APNs.** The backend sends the `apns-push-type` header, which Notification Hubs supports only with token-based APNs credentials ([iOS 13 updates](https://learn.microsoft.com/azure/notification-hubs/push-notification-updates-ios-13)).
- **No redeploy is needed.** The Dispatcher sends to every platform in `PUSH_PLATFORMS` (default `browser,fcmv1,apns`). While a platform has no credentials, Notification Hubs answers `400 … no target applications` and the Dispatcher skips that platform (verified). Set `PUSH_PLATFORMS` on the Function App to drop the extra calls if you'll never use a platform.
- **Don't** put these credentials in this repository or in the azd environment.

## 5. Configure CIJ and run the first test

1. In CIJ settings, add a **Push + Inbox** channel instance (app key, for example `contoso-demo-app`), then run step 4.3 with its id.
2. Open `WEB_URL?appKey=<app key>&recipientKey=contact_<contact id>` in Edge or Chrome, using the ID of the contact you'll put in the test journey, for example `…/?appKey=contoso-demo-app&recipientKey=contact_00000000-0000-0000-0000-000000000001`. Select **Subscribe** and allow notifications. Journey sends address `contact_<contact id>` ([developer integration, identifiers](developer-integration.md#identifiers)). Taking the key from the URL is a test-harness shortcut only.
3. In a CIJ message for the Push + Inbox channel, use **Test send**. Test sends have no contact ID, so the recipient key is whatever you type: enter the same `contact_<contact id>` value, choose a preview contact if the message has personalization, and send.
4. Expected: a browser notification, and the item in the PWA inbox. Test sends carry an empty `RequestId`, so no delivery report is sent for them.

For a real send, build a short journey: a trigger or segment, then a **Push + Inbox** message tile. The delivery status, and clicks on the tracked links, show in the tile's analytics after a delay of **tens of minutes**.

Troubleshooting ([full guide](troubleshooting.md)): Application Insights shows `Accepted …`, `Inbox item … stored`, and `Notification Hubs send … success N` for every message. `success 0` means no browser is subscribed for that recipient and app key.

## 6. Verify


1. **Solution check:** `python3 tests/solution/check_solution.py` prints `OK`.
2. **Relationship:** `GET EntityDefinitions(LogicalName='alex_pushchannelinstance')/OneToManyRelationships?$filter=ReferencingAttribute eq 'msdyn_extendedentityid'` returns one relationship with `CascadeDelete` = `RemoveLink`. ([Custom channel instance](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-define-custom-channel-instance))
3. **Channel definition:** `GET msdyn_channeldefinitions(b0d2cb5f-fc8a-4f71-88fd-3f494aee6df2)` returns `msdyn_outboundendpointurltemplate` = `/alex_PushOutbound`, and its `msdyn_channelmessageparts` returns 7 rows.
4. **CIJ settings:** **Push + Inbox** appears among the custom channels, and adding a channel instance opens the *Push sender* form. ([Custom channel instance](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-define-custom-channel-instance))
5. **Outbound API:** `POST alex_PushOutbound` with `{"payload":"{…}"}` returns `Sent` and a `MessageId` once step 4.3 is done (`NotSent` / `ChannelApiNotConfigured` while the environment variable is empty).
6. **Inbox API:** `GET {API_BASE_URL}/inbox?appKey=<app key>&recipientKey=contact_<contact id>` returns the item.
7. **Hebrew labels:** `python3 tools/l10n/translations.py verify https://<org>.crm.dynamics.com` prints `problems: 0`. A user with Hebrew as UI language sees Hebrew, right-to-left forms and views. ([Personal options (language)](https://learn.microsoft.com/power-apps/user/set-personal-options))

## Upgrade

1. **Import the new package of the same type.**
   - **Managed:** keep the default *Upgrade* option. It removes components that are no longer in the new version ([Update solutions](https://learn.microsoft.com/power-platform/alm/update-solutions-alm)).
   - **Unmanaged:** the import adds and updates components but never deletes any.
2. **Rerun `deploy/post-import.sh` and `deploy/connect-azure.sh`.** Both are idempotent.
3. **Redeploy the backend.** Run `azd deploy` (code only) or `azd up` (infrastructure too).
4. **Read the version notes** in [release/README.md](../release/README.md) and [known limitations](known-limitations.md). Some changes need a manual step. For example, upgrading from the plain-text image part requires one.

**Order.** Deploy the backend before the solution when the new version adds parts or APIs that the backend must handle. Otherwise the order doesn't matter: the plug-in only calls the Channel API.

## Uninstall

Delete the solution.

- **Managed package:** deleting it removes its components, including the message log table and all its rows.
- **Unmanaged package:** deleting the solution removes only the solution container. The tables, APIs and other components stay, and you must delete them yourself ([Solution concepts](https://learn.microsoft.com/power-platform/alm/solution-concepts-alm)).

The relationship uses `RemoveLink`, so existing CIJ channel instances lose their link to the config row but aren't deleted. Remove those channel instances in CIJ first. ([Custom channel instance](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-define-custom-channel-instance))

To remove the Azure backend, run `azd down --purge` (purges the soft-deleted Key Vault). Then delete the managed identity's application user in Dataverse. ([azd down](https://learn.microsoft.com/azure/developer/azure-developer-cli/reference#azd-down))
