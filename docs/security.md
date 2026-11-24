# Security considerations

**Audience:** Chief Information Security Officer, security architects, and the Dynamics 365 and Azure administrators who deploy this solution.
**Scope:** the Push + Inbox custom channel for Dynamics 365 Customer Insights – Journeys (CIJ): the Dataverse solution, the Azure backend deployed by `azd` from this repository, and the reference PWA.
**Release status:** pilot. Some controls a production deployment needs are not implemented yet. Each one is marked **Planned** below and listed again in [the summary](#summary-of-open-items).

Each item has a reference ID (SEC-nn) and five parts:

- **Risk**
- **Control (implemented):** what this repository deploys or ships today
- **Residual risk**
- **Customer responsibility**
- **Sources:** public documentation

Marking rules:

- **Planned** marks controls that are not implemented.
- **Observed** marks behavior seen in a test deployment that isn't guaranteed by documentation.

Related documents: [architecture](architecture.md), [data model](data-model.md), [installation](installation.md), [known limitations](known-limitations.md), [developer integration](developer-integration.md).

## Contents

| ID | Topic |
|---|---|
| [SEC-01](#sec-01-data-flows-and-data-classification) | Data flows and data classification |
| [SEC-02](#sec-02-data-residency-and-region) | Data residency and region |
| [SEC-03](#sec-03-encryption-in-transit-and-at-rest) | Encryption in transit and at rest |
| [SEC-04](#sec-04-authentication-from-dataverse-to-azure) | Authentication from Dataverse to Azure |
| [SEC-05](#sec-05-authentication-from-azure-to-dataverse) | Authentication from Azure to Dataverse |
| [SEC-06](#sec-06-secrets-management) | Secrets management |
| [SEC-07](#sec-07-least-privilege-in-dataverse) | Least privilege in Dataverse |
| [SEC-08](#sec-08-least-privilege-in-azure) | Least privilege in Azure |
| [SEC-09](#sec-09-network-exposure) | Network exposure |
| [SEC-10](#sec-10-inbox-api-end-user-authentication-and-authorization) | Inbox API end-user authentication and authorization |
| [SEC-11](#sec-11-web-push-and-vapid-keys) | Web Push and VAPID keys |
| [SEC-12](#sec-12-personal-data-in-notification-hubs-tags) | Personal data in Notification Hubs tags |
| [SEC-13](#sec-13-consent-and-compliance-profile) | Consent and compliance profile |
| [SEC-14](#sec-14-data-retention-and-deletion) | Data retention and deletion (GDPR) |
| [SEC-15](#sec-15-logging-and-monitoring-without-personal-data) | Logging and monitoring without personal data |
| [SEC-16](#sec-16-idempotency-replay-abuse-and-rate-limiting) | Idempotency, replay, abuse and rate limiting |
| [SEC-17](#sec-17-third-party-push-services) | Third-party push services |
| [SEC-18](#sec-18-supply-chain) | Supply chain |
| [SEC-19](#sec-19-planned-message-log-in-dataverse) | Planned message log in Dataverse |
| [SEC-20](#sec-20-shared-responsibility) | Shared responsibility |
| [SEC-21](#sec-21-message-images-from-the-asset-library) | Message images from the asset library |

---

## SEC-01 Data flows and data classification

**Data flow.** The CIJ journey, or a test send, calls the outbound custom API in Dataverse. Its plug-in POSTs the message to the Azure Channel API and gets back `202`. The Channel API queues the message in Service Bus. The Dispatcher function then:

1. writes the inbox item to Cosmos DB;
2. sends a Web Push through Azure Notification Hubs;
3. reports the delivery status back to Dataverse.

The end-user device then:

1. registers its push subscription through the Inbox API;
2. reads the inbox through the Inbox API.

See the diagrams in [architecture.md](architecture.md).

| Data element | Classification | Where it is stored or processed |
|---|---|---|
| **Recipient key.** The CIJ `To` value, usually a phone number, normalized to lowercase with `+` and spaces removed. | **Personal data** (direct identifier) | Dataverse/CIJ (source). Plug-in memory, not traced. Service Bus message (transient). Cosmos DB `inboxItems` (partition key) and `dispatches`. Notification Hubs tag, as a SHA-256 hash ([SEC-12](#sec-12-personal-data-in-notification-hubs-tags)). Application Insights, as a 12-character hash prefix ([SEC-15](#sec-15-logging-and-monitoring-without-personal-data)). The PWA's browser local storage and Inbox API query string ([SEC-10](#sec-10-inbox-api-end-user-authentication-and-authorization)). |
| **Message content:** title, body, image URL, action URL | **Personal data / confidential.** The content is marketer-authored and may be personalized with contact data. | Service Bus (transient). Cosmos DB `inboxItems`. The encrypted Web Push payload ([SEC-17](#sec-17-third-party-push-services)). The end-user device. |
| **Push subscription:** browser endpoint URL, `p256dh`, `auth` | **Personal data** (device identifier) | Notification Hubs installation. The browser. |
| **CIJ identifiers:** `RequestId`, `CustomerJourneyId`, `MessageId` | Internal | Cosmos DB, Application Insights, Dataverse |
| **Channel API key, Notification Hubs connection string, VAPID private key** | **Secret** | See [SEC-06](#sec-06-secrets-management). |

- **Residual risk:** whether the recipient key is a phone number, an e-mail address or an opaque ID depends on which CIJ attribute the customer maps to the channel.
- **Customer responsibility:**
  - Prefer an opaque, app-specific identifier over a phone number when the customer's app can supply one.
  - Classify message content under the customer's own policy.
  - Don't place secrets or special-category data in push content.
- **Sources:** [Custom channels in CIJ](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-extend-outreach-custom-channels), [Custom channel custom APIs](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-custom-channel-custom-api).

## SEC-02 Data residency and region

- **Risk:** personal data is stored outside the region the customer has approved.
- **Control (implemented):**
  - All Azure resources are created in the single region chosen at `azd env new` (`AZURE_LOCATION`).
  - Cosmos DB can be placed in a different region through `COSMOS_LOCATION`. The default is the same region. **Observed:** in one test subscription a West Europe Cosmos DB account could not be created because of regional capacity, so another EU region was used. The deployment records the region you select.
  - Notification Hubs stores registration data in the region of the namespace.
  - Dataverse data stays in the environment's geography.
- **Residual risk:**
  - Web Push delivery goes through the browser vendor's push service, whose location the customer doesn't control ([SEC-17](#sec-17-third-party-push-services)).
  - Static Web Apps serves static files (no personal data) from a global content network.
  - Message images come from the CIJ asset library, which can cache them outside the region ([SEC-21](#sec-21-message-images-from-the-asset-library)).
- **Customer responsibility:**
  - Choose `AZURE_LOCATION` and `COSMOS_LOCATION` in the same geography as the Dataverse environment.
  - Use [Azure Policy](https://learn.microsoft.com/azure/governance/policy/overview) "allowed locations" to enforce the choice.
- **Sources:**
  - [Notification Hubs FAQ: "stores customer registration data in the region selected by the customer"](https://learn.microsoft.com/azure/notification-hubs/notification-hubs-push-notification-faq)
  - [Azure regions](https://learn.microsoft.com/azure/reliability/regions-list)
  - [Power Platform regions](https://learn.microsoft.com/power-platform/admin/regions-overview)

## SEC-03 Encryption in transit and at rest

- **Risk:** data is disclosed on the wire or from storage media.
- **Control (implemented):**
  - **In transit:**
    - Function App: `httpsOnly: true` and minimum TLS 1.2.
    - Storage, Service Bus and Cosmos DB: minimum TLS 1.2 where the resource exposes the setting.
    - Dataverse calls and Notification Hubs calls use HTTPS.
    - Web Push payloads are encrypted end-to-end to the browser (RFC 8291).
  - **At rest:**
    - Cosmos DB, Storage, Service Bus and Key Vault encrypt at rest with Microsoft-managed keys by default. Cosmos DB uses AES-256, and this can't be turned off.
    - Notification Hubs encrypts customer data at rest **except registration tags** (see [SEC-12](#sec-12-personal-data-in-notification-hubs-tags)).
- **Residual risk:** keys are Microsoft-managed. Customer-managed keys aren't configured.
- **Customer responsibility:** enable customer-managed keys where policy requires them. Some services need a higher tier for this, for example Service Bus Premium.
- **Sources:**
  - [Azure encryption at rest](https://learn.microsoft.com/azure/security/fundamentals/encryption-atrest)
  - [Cosmos DB encryption at rest](https://learn.microsoft.com/azure/cosmos-db/database-encryption-at-rest)
  - [Service Bus customer-managed keys](https://learn.microsoft.com/azure/service-bus-messaging/configure-customer-managed-key)
  - [Notification Hubs FAQ](https://learn.microsoft.com/azure/notification-hubs/notification-hubs-push-notification-faq)
  - [RFC 8291 Message Encryption for Web Push](https://datatracker.ietf.org/doc/html/rfc8291)

## SEC-04 Authentication from Dataverse to Azure

- **Risk:** an unauthorized caller submits messages to the Channel API and sends pushes to customers' users.
- **Control (implemented):**
  - `POST /messages` uses Azure Functions `AuthorizationLevel.Function`. Requests without a valid function key get `401`, as verified in the test deployment.
  - The key is stored in the channel instance column `alex_channelapikey`. That column has column-level security enabled (`IsSecured = 1`), and the solution ships no field security profile. So only System Administrators can read it, unless the customer grants a profile.
  - The plug-in reads the key in system context and sends it in the `x-functions-key` header. The key is never written to the trace log.
- **Residual risk:**
  - A function key is a shared secret. Microsoft recommends other mechanisms for production HTTP endpoints.
  - Anyone holding the key can call the API until it is rotated.
  - Column-level security doesn't apply to System Administrators.
- **Planned:** replace the function key with [Power Platform managed identity for plug-ins](https://learn.microsoft.com/power-platform/admin/set-up-managed-identity) and Entra ID token validation on the Function App.
- **Customer responsibility:**
  - Rotate the function key: `az functionapp keys set`, then update the instance.
  - Restrict System Administrator membership.
- **Sources:**
  - [Function access keys: "consider other options to secure HTTP endpoints in production"](https://learn.microsoft.com/azure/azure-functions/function-keys-how-to)
  - [Column-level security: "doesn't apply for users who have the system administrator role"](https://learn.microsoft.com/power-platform/admin/field-level-security)

## SEC-05 Authentication from Azure to Dataverse

- **Risk:** the Dispatcher needs to write delivery status into Dataverse, and stored credentials could leak.
- **Control (implemented):**
  - The Function App has a system-assigned managed identity. `deploy/connect-azure.sh` registers it as a Dataverse **application user** and assigns only the **Basic User** role.
  - The Dispatcher gets tokens with `DefaultAzureCredential`, so no client secret exists.
  - The delivery-report API's plug-in calls CIJ's `msdyn_D365ChannelsNotification` in system context. The application user therefore needs no CIJ privileges.
- **Residual risk:** see [SEC-07](#sec-07-least-privilege-in-dataverse). The custom APIs currently have no execute privilege.
- **Customer responsibility:**
  - Review the application user periodically.
  - Disable it when the backend is removed.
- **Sources:** [Managed identities](https://learn.microsoft.com/entra/identity/managed-identities-azure-resources/overview), [Manage application users](https://learn.microsoft.com/power-platform/admin/manage-application-users).

## SEC-06 Secrets management

| Secret | Where it lives (implemented) | Rotation |
|---|---|---|
| Notification Hubs connection string (rule `backend-listen-send`) | Key Vault secret, read by the Function App through a Key Vault reference. Key Vault has RBAC authorization and public network access disabled. | Regenerate the rule key, then update the secret. |
| Channel API function key | Function App host keys. Dataverse secured column ([SEC-04](#sec-04-authentication-from-dataverse-to-azure)). | `az functionapp keys set`, then update the instance. |
| VAPID private key | Notification Hubs browser credential (set by Bicep). The deployer's local `azd` environment file `.azure/<env>/.env`, which is git-ignored. Passed to Bicep as a `@secure()` parameter. | Generate a new pair and redeploy. Existing browser subscriptions must re-subscribe. |
| Strong-name key `.snk` for the plug-in assembly | Committed in the repository, as Dataverse requires signed assemblies. | Replace with your own key before you build. |

- **Residual risk:**
  - Strong naming is not a security control; it gives identity only.
  - The committed `.snk` is public.
  - The local `.azure` folder holds the VAPID private key in plain text on the deployer's machine.
- **Customer responsibility:**
  - Generate your own `.snk`.
  - Protect or delete the local `azd` environment folder after deployment.
  - Keep production secrets in a pipeline secret store.
- **Sources:**
  - [Key Vault references](https://learn.microsoft.com/azure/app-service/app-service-key-vault-references)
  - [Key Vault RBAC](https://learn.microsoft.com/azure/key-vault/general/rbac-guide)
  - [Strong-named assemblies: "Do not rely on strong names for security"](https://learn.microsoft.com/dotnet/standard/assembly/strong-named)
  - [Register a plug-in](https://learn.microsoft.com/power-apps/developer/data-platform/register-plug-in)

## SEC-07 Least privilege in Dataverse

- **Risk:** over-broad roles, or callable APIs, allow message injection or status spoofing.
- **Control (implemented):**
  - CIJ's application user must read the channel instance. The post-import script adds **Read** on `alex_pushchannelinstance` (Organization depth) to the Microsoft role **Marketing Services User Extensible Role**, using `AddPrivilegesRole`. This is the documented role for custom-channel instance access. No other privilege is added.
  - The Azure managed-identity application user has **Basic User** only ([SEC-05](#sec-05-authentication-from-azure-to-dataverse)).
- **Residual risk:**
  - `alex_PushOutbound` and `alex_PushDeliveryReport` have no `ExecutePrivilegeName`. Any authenticated Dataverse user can call them. They could send a push to any recipient key, or post a false delivery status for a known `RequestId`.
  - The solution ships no security roles of its own.
- **Planned:**
  - Set an execute privilege on both custom APIs.
  - Ship a solution role that grants it only to CIJ's service identity and the Azure application user.
- **Customer responsibility:** until then, limit which users can sign in to the environment and call APIs, and monitor plug-in executions.
- **Sources:**
  - [Channel instance account and Extensible role](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-define-channel-instance-account)
  - [AddPrivilegesRole](https://learn.microsoft.com/power-apps/developer/data-platform/webapi/reference/addprivilegesrole)
  - [Custom API: ExecutePrivilegeName](https://learn.microsoft.com/power-apps/developer/data-platform/custom-api-tables)

## SEC-08 Least privilege in Azure

- **Risk:** shared keys or broad roles allow lateral movement or data access.
- **Control (implemented):**
  - **Identity only.** The Function App's system-assigned identity uses data-plane roles, scoped to each resource:

    | Resource | Roles |
    |---|---|
    | Storage | Blob Data Owner, Queue Data Contributor, Table Data Contributor (the Functions host requirement) |
    | Service Bus | Data Sender, Data Receiver |
    | Key Vault | Secrets User |
    | Cosmos DB | Built-in Data Contributor |

  - **Local auth disabled:**
    - Storage `allowSharedKeyAccess: false`.
    - Service Bus `disableLocalAuth: true`, so no SAS.
    - Cosmos DB `disableLocalAuth: true`, so no account keys.
  - The only shared key in use is the Notification Hubs rule, because the hub REST send uses SAS.
- **Residual risk:**
  - The deployer needs Owner, or Contributor plus User Access Administrator, on the resource group to create the role assignments.
  - The Notification Hubs SAS rule has Listen and Send rights.
- **Customer responsibility:**
  - Deploy with a pipeline identity.
  - Remove standing deployer access afterwards.
- **Sources:**
  - [Identity-based connections for Functions](https://learn.microsoft.com/azure/azure-functions/functions-reference#common-properties-for-identity-based-connections)
  - [Cosmos DB: disable key-based auth](https://learn.microsoft.com/azure/cosmos-db/nosql/security/how-to-disable-key-based-authentication)
  - [Service Bus SAS](https://learn.microsoft.com/azure/service-bus-messaging/service-bus-sas)
  - [Notification Hubs security](https://learn.microsoft.com/azure/notification-hubs/notification-hubs-push-notification-security)

## SEC-09 Network exposure

- **Risk:** data services are reachable from the internet.
- **Control (implemented):**
  - **Policy-driven private networking.** The deployment was built in a tenant where an Azure Policy denies public network access to data services. The Bicep therefore deploys:
    - a virtual network;
    - Flex Consumption **VNet integration** for the Function App;
    - **private endpoints** with private DNS zones for Cosmos DB, Key Vault and Blob storage;
    - a **Network Security Perimeter** for the storage account (`publicNetworkAccess: SecuredByPerimeter`).
  - Cosmos DB and Key Vault have `publicNetworkAccess: Disabled`.
  - CORS on the Function App allows only the PWA's origin.
- **Public by design:**
  - The Function App HTTP endpoints: the Channel API with a key, and the Inbox API without one ([SEC-10](#sec-10-inbox-api-end-user-authentication-and-authorization)). Dataverse plug-ins and end-user devices must reach them.
  - Notification Hubs.
  - Static Web Apps.
  - Service Bus on the **Basic** tier, which doesn't support private endpoints (Premium only). It accepts only Entra ID authentication.
- **Residual risk:**
  - No WAF and no API gateway sits in front of the Function App.
  - Service Bus has a public endpoint.
- **Planned / optional:** API Management in front of the Function App ([SEC-16](#sec-16-idempotency-replay-abuse-and-rate-limiting)).
- **Customer responsibility:**
  - Decide whether private networking is required by your policy; the cost is in the installation guide.
  - Use Service Bus Premium if the queue must be private.
  - Add a gateway or WAF for production.
- **Sources:**
  - [Flex Consumption VNet integration](https://learn.microsoft.com/azure/azure-functions/flex-consumption-plan#virtual-network-integration)
  - [Functions networking options](https://learn.microsoft.com/azure/azure-functions/functions-networking-options)
  - [Private endpoints](https://learn.microsoft.com/azure/private-link/private-endpoint-overview)
  - [Network Security Perimeter](https://learn.microsoft.com/azure/private-link/network-security-perimeter-concepts)
  - [Storage and NSP](https://learn.microsoft.com/azure/storage/common/storage-network-security-perimeter)
  - [Service Bus private endpoints are Premium-only](https://learn.microsoft.com/azure/service-bus-messaging/private-link-service)

## SEC-10 Inbox API end-user authentication and authorization

- **Risk:** someone other than the recipient reads, marks or deletes inbox items, or registers a device to receive another person's pushes.
- **Control (implemented):**
  - The Inbox API is a **test harness** for the reference PWA.
  - Notification Hubs tags are set by the server, never by the client.
  - Every read, mark-read and delete operation is scoped to the caller-supplied `recipientKey` partition and checks `appKey`.
- **Residual risk: CRITICAL for production.**
  - The Inbox API is **anonymous** (`AuthorizationLevel.Anonymous`).
  - It identifies the user only by the `appKey` and `recipientKey` query values. Anyone who knows or guesses a recipient key (for example a phone number) can:
    - read that person's inbox;
    - mark and delete items;
    - register a browser to receive that person's pushes.
  - **Observed:** Application Insights request telemetry recorded the URL without the query string in the test deployment.
- **Planned:**
  - Authenticate end users with the customer's identity provider or Entra External ID. Derive `recipientKey` from the validated token, never from the request.
  - Remove the query parameters.
  - See [developer integration: what must change for production](developer-integration.md#what-must-change-for-production).
- **Customer responsibility:** **do not expose the Inbox API to real users until end-user authentication is in place.**
- **Sources:**
  - [App Service / Functions authentication](https://learn.microsoft.com/azure/app-service/overview-authentication-authorization)
  - [Entra External ID for customers](https://learn.microsoft.com/entra/external-id/customers/overview-customers-ciam)
  - [Notification Hubs installations (tags set by back end)](https://learn.microsoft.com/azure/notification-hubs/notification-hubs-push-notification-registration-management)

## SEC-11 Web Push and VAPID keys

- **Risk:** a stolen VAPID private key lets an attacker send pushes to the customer's subscribers.
- **Control (implemented):**
  - The private key is held in the Notification Hubs browser credential and the local azd environment ([SEC-06](#sec-06-secrets-management)). It is never sent to the PWA.
  - `GET /config` returns only the public key.
  - Pushes are signed with VAPID (RFC 8292) and encrypted with RFC 8291.
- **Residual risk:** a key compromise requires a new key pair, and every subscriber must re-subscribe.
- **Customer responsibility:**
  - Generate a dedicated VAPID pair per environment.
  - Use a monitored contact for the VAPID subject.
- **Sources:**
  - [RFC 8292 VAPID](https://datatracker.ietf.org/doc/html/rfc8292)
  - [RFC 8030 Web Push](https://datatracker.ietf.org/doc/html/rfc8030)
  - [Notification Hubs browser push](https://learn.microsoft.com/azure/notification-hubs/browser-push)

## SEC-12 Personal data in Notification Hubs tags

- **Risk:** Notification Hubs **does not encrypt registration tags at rest**, and Microsoft advises against storing personal data in tags.
- **Control (implemented):**
  - Device installations are tagged `rk:<SHA-256 hex of the recipient key>` and `app:<appKey>`.
  - **Transitional:** devices registered before this change still carry a plain `rk:<recipientKey>` tag until they register again. The Dispatcher matches either form, using the expression `(rk:<hash> || rk:<key>) && app:<appKey>`.
- **Residual risk:**
  - An unsalted SHA-256 of a phone number is **pseudonymous, not anonymous**, because phone numbers can be enumerated.
  - Plain legacy tags remain until those devices re-register.
- **Planned:**
  - Keyed hash (HMAC) with a key in Key Vault.
  - Remove the transitional plain-tag match.
- **Customer responsibility:** have existing subscribers re-register (in the PWA, select **Enable notifications** again).
- **Sources:** [Notification Hubs FAQ: "encrypts all customer data at rest except for registration tags … you should not store personal or confidential data using tags"](https://learn.microsoft.com/azure/notification-hubs/notification-hubs-push-notification-faq).

## SEC-13 Consent and compliance profile

- **Risk:** messages reach people who have not consented.
- **Control (implemented):**
  - The channel relies on CIJ: every message is sent under a CIJ compliance profile and purpose (for example Commercial or Transactional) chosen by the marketer.
  - For custom channels, CIJ evaluates contact-point consent, not the email `DoNot*` fields.
  - The channel definition sets `msdyn_specialconsentrequired = 0`, as in Microsoft's sample.
  - The Azure backend performs no consent check of its own.
- **Residual risk:** *not verified in testing:* that CIJ suppresses a send for a recipient without consent on this channel.
- **Customer responsibility:**
  - Configure compliance profiles and contact-point consent for the push channel.
  - Collect OS or browser notification permission in the app. Browsers require explicit user permission.
- **Sources:**
  - [CIJ compliance settings: "Text messages and custom channel messages don't use the DoNotEmail, DoNotBulkEmail, or DoNotTrack fields"](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-compliance-settings)
  - [Define the channel definition](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-define-channel-definition)

## SEC-14 Data retention and deletion

| Store | Retention (implemented) |
|---|---|
| Cosmos DB `inboxItems` | Per-item TTL = the message's inbox expiry (channel default 30 days, allowed range 1–180). The container default TTL is `-1`, so items without a TTL don't expire. |
| Cosmos DB `dispatches` (idempotency records) | Container TTL 30 days |
| Service Bus `dispatch` queue | Message TTL 1 day. Up to 5 delivery attempts, then dead-letter. Dead-lettered on expiry. |
| Log Analytics / Application Insights | 30 days |
| Notification Hubs installations | Until the device unregisters or the push service rejects the endpoint |
| Dataverse plug-in trace log | Per the environment's trace setting and Dataverse cleanup |
| Cosmos DB periodic backups | By default, a full backup every 4 hours; the latest two are kept |

- **Deletion (GDPR data subject requests):**
  - The end user can delete individual inbox items (`DELETE /inbox/{id}`).
  - There is **no** API to delete everything for one recipient.
  - Because inbox items are partitioned by `recipientKey`, an administrator can delete all of a recipient's items in one operation.
- **Planned:**
  - A "delete by recipient" operation covering Cosmos DB and Notification Hubs installations.
  - The planned Dataverse message log ([SEC-19](#sec-19-planned-message-log-in-dataverse)).
- **Customer responsibility:**
  - Include this solution's stores in the DSR process.
  - Set the trace log setting to *Exception* or *Off* outside troubleshooting.
- **Sources:**
  - [Cosmos DB time to live](https://learn.microsoft.com/azure/cosmos-db/how-to-time-to-live)
  - [Cosmos DB delete by partition key](https://learn.microsoft.com/azure/cosmos-db/nosql/how-to-delete-by-partition-key)
  - [Cosmos DB periodic backup](https://learn.microsoft.com/azure/cosmos-db/periodic-backup-restore-introduction)
  - [Service Bus dead-letter queues](https://learn.microsoft.com/azure/service-bus-messaging/service-bus-dead-letter-queues)
  - [Log Analytics retention](https://learn.microsoft.com/azure/azure-monitor/logs/data-retention-configure)
  - [Azure DSR guidance](https://learn.microsoft.com/compliance/regulatory/gdpr-dsr-azure)

## SEC-15 Logging and monitoring without personal data

- **Risk:** personal data ends up in logs that have broader access and longer retention.
- **Control (implemented):**
  - **Application Insights:**
    - Function logs write the recipient as a 12-character SHA-256 prefix (for example `recipient 0bc8b741cd51`), never the key itself.
    - Installation IDs are hashed the same way.
    - Message content isn't logged.
    - Notification Hubs results are logged as outcome text only, without the push endpoint.
    - IP addresses aren't stored (the Application Insights default).
  - **Dataverse plug-in trace log:** the outbound plug-in traces only metadata: message name, depth, correlation ID, payload length, `RequestId`, the HTTP status, and the response. It doesn't trace the recipient or the content.
- **Residual risk:**
  - Logs written before this control existed (the pilot deployment) contain plain recipient keys until the 30-day retention ends.
  - The hash prefix is pseudonymous ([SEC-12](#sec-12-personal-data-in-notification-hubs-tags)).
- **Planned:** alerts on dead-letter count and on Channel API 4xx/5xx rates.
- **Customer responsibility:**
  - Restrict access to Log Analytics.
  - Purge pilot logs if required.
  - Keep plug-in tracing off in production.
- **Sources:**
  - [Application Insights IP handling: "By default, IP addresses are temporarily collected but not stored"](https://learn.microsoft.com/azure/azure-monitor/app/ip-collection)
  - [Personal data in Azure Monitor Logs](https://learn.microsoft.com/azure/azure-monitor/logs/personal-data-mgmt)
  - [Dataverse plug-in tracing](https://learn.microsoft.com/power-apps/developer/data-platform/logging-tracing)

## SEC-16 Idempotency, replay, abuse and rate limiting

- **Risk:** the same message is delivered twice, or the system is flooded with sends.
- **Control (implemented):**
  - **Idempotency:**
    - The Channel API creates a `dispatches` record keyed on CIJ's `IdempotencyId` (or a generated one when CIJ sends it empty, as test sends do).
    - A duplicate gets `409` from Cosmos DB, and the API returns the original `MessageId` without queueing again.
    - The Service Bus message ID equals the idempotency ID.
  - **Input validation:** JSON body, app key, non-empty content, a known delivery mode, and a recipient are all required.
  - **Scale limit:** the Function App's `maximumInstanceCount` is 40, which caps both cost and scale.
- **Residual risk:**
  - There is **no rate limiting** on any endpoint.
  - The anonymous Inbox API ([SEC-10](#sec-10-inbox-api-end-user-authentication-and-authorization)) can be called at volume.
  - Basic-tier Service Bus has no duplicate detection; the Cosmos DB record provides de-duplication instead.
  - Notification Hubs Free tier quotas apply.
- **Planned:** API Management with `rate-limit-by-key` in front of the Function App.
- **Customer responsibility:**
  - Set CIJ journey frequency caps.
  - Add a gateway for production.
- **Sources:**
  - [Rate limit by key (APIM)](https://learn.microsoft.com/azure/api-management/rate-limit-by-key-policy)
  - [Service Bus overview](https://learn.microsoft.com/azure/service-bus-messaging/service-bus-messaging-overview)
  - [Notification Hubs pricing and quotas](https://azure.microsoft.com/pricing/details/notification-hubs/)

## SEC-17 Third-party push services

- **Risk:** message data is processed by push services outside the customer's tenant: browser vendors' Web Push services, and Google FCM and Apple APNs once mobile support exists.
- **Control (implemented):**
  - Only Web Push is implemented.
  - Payloads are encrypted end-to-end to the browser (RFC 8291), so the push service sees metadata (endpoint, size, timing) but not the content.
  - The full message also stays in the inbox. The push carries only title, body, direction, language and URLs.
- **Residual risk:**
  - The push service's availability, retention and location are outside the customer's control.
  - **Planned** mobile channels (FCM/APNs) will have their own data-handling terms.
- **Customer responsibility:**
  - Review the push vendors' terms.
  - Avoid sensitive content in push notifications; use the inbox and require sign-in to read it.
- **Sources:**
  - [RFC 8030 Web Push](https://datatracker.ietf.org/doc/html/rfc8030)
  - [RFC 8291](https://datatracker.ietf.org/doc/html/rfc8291)
  - [Notification Hubs browser push](https://learn.microsoft.com/azure/notification-hubs/browser-push)
  - [Apple User Notifications](https://developer.apple.com/documentation/usernotifications)

## SEC-18 Supply chain

- **Risk:** a compromised dependency or an altered build artifact.
- **Control (implemented):**
  - Dependencies come from NuGet and npm with pinned package versions in the project files.
  - The plug-in assembly is strong-name signed, as Dataverse requires.
  - The repository's pre-commit hook validates solution structure and localization.
- **Residual risk:**
  - There is no automated dependency scanning.
  - There is no signed build provenance.
  - The plug-in DLL is committed as a binary.
  - The `.snk` is public ([SEC-06](#sec-06-secrets-management)).
  - Repository-hosted CI is not active, so checks run locally.
- **Planned:**
  - Dependabot alerts.
  - Build the plug-in in CI from source.
- **Customer responsibility:**
  - Build from source with your own signing key.
  - Scan dependencies.
  - Deploy managed solutions only.
- **Sources:**
  - [Strong-named assemblies](https://learn.microsoft.com/dotnet/standard/assembly/strong-named)
  - [Dependabot alerts](https://docs.github.com/code-security/dependabot/dependabot-alerts/about-dependabot-alerts)

## SEC-19 Planned message log in Dataverse

**Planned, not implemented.** The design adds a per-message log as a Dataverse **elastic table**, holding status, timestamps and journey identifiers. Its schema is in [data-model.md](data-model.md#planned-message-log-elastic-table).

- **Risk:** the log adds a third store of personal data, the recipient key, plus a lookup to the contact. Access in Dataverse follows its security roles.
- **Planned controls:**
  - Store no message content (title, body, image); identifiers, status and reason only.
  - Column-level security on `alex_recipientkey`. Elastic tables support field-level security.
  - Set a row time-to-live (`ttlinseconds`): 180 days, 7 days for test sends. Dataverse deletes expired rows.
  - Organization-owned rows, written only by the backend application user through a dedicated role. Read access through a separate role.
  - Include the table in delete-by-recipient.
- **Residual risk:** rows exported through Azure Synapse Link before they expire are not deleted from the data lake by TTL. Field-level security doesn't restrict system administrators.
- **Customer responsibility:** decide who can view the log once a display location is chosen.
- **Sources:** [Elastic tables](https://learn.microsoft.com/power-apps/developer/data-platform/elastic-tables), [Create and edit elastic tables](https://learn.microsoft.com/power-apps/maker/data-platform/create-edit-elastic-tables), [Field-level security](https://learn.microsoft.com/power-platform/admin/field-level-security).

## SEC-20 Shared responsibility

| Area | Microsoft | This solution | Customer |
|---|---|---|---|
| Physical, host and platform security of Azure, Dataverse and CIJ | ✔ | | |
| Service encryption at rest and TLS endpoints | ✔ | Enforces TLS 1.2 and disables local auth | Optional customer-managed keys |
| Identity and roles | Entra ID, Dataverse RBAC | Managed identity; minimal grants ([SEC-07](#sec-07-least-privilege-in-dataverse), [SEC-08](#sec-08-least-privilege-in-azure)) | Admin membership, key rotation, access reviews |
| End-user authentication for the Inbox API | — | **Planned** | Supply an identity provider |
| Network | Private Link, NSP | Deploys private endpoints and NSP | Policy, WAF/gateway, Service Bus tier |
| Consent | CIJ consent engine | Uses CIJ compliance profiles | Configure consent and purposes |
| Data retention and DSR | Platform tools | TTLs | DSR process; purge logs |
| App and device | Push services (browser vendors, Google, Apple) | Reference PWA only | The customer's app and its permissions |

- **Source:** [Shared responsibility in the cloud](https://learn.microsoft.com/azure/security/fundamentals/shared-responsibility).

---

## Summary of open items

| ID | Item | Severity for production |
|---|---|---|
| SEC-10 | Anonymous Inbox API: add end-user authentication | **Critical** |
| SEC-07 | Custom APIs callable by any Dataverse user: add an execute privilege and role | High |
| SEC-04 | Function key: move to managed identity / Entra ID | High |
| SEC-16 | No rate limiting: add APIM | Medium |
| SEC-12 | Unkeyed hash in tags; remove legacy plain tags | Medium |
| SEC-14 | Delete by recipient (GDPR) | Medium |
| SEC-18 | Dependency scanning, CI build of the plug-in, own `.snk` | Medium |
| SEC-09 | Public Service Bus endpoint (Basic tier) | Low to Medium, per policy |
| SEC-15 | Alerts on dead-letter and errors | Low |

## SEC-21 Message images from the asset library

- **Risk:**
  - A message image is reachable by anyone who has its URL. An image that contains personal or confidential data would be exposed.
  - Copies of the image can be cached outside the environment's region.
- **Control (implemented):**
  - Marketers pick images from the CIJ asset library. The image part stores only the asset ID. The outbound plug-in reads the asset's public URL (`msdyncrm_blobcdnuri`) at send time and accepts it only when it is `https` and the asset's content type is `image/*`. Otherwise the message is sent without an image ([data-model.md](data-model.md#image-resolution)).
  - The Azure backend receives and stores only that URL, never the image bytes. It gets no Dataverse read access for this.
  - The plug-in trace records only the asset ID when an image can't be resolved. Asset IDs are not personal data.
- **Residual risk:**
  - Asset-library files are public by design. Microsoft: "Assets must be available publicly on the internet so your content can access and display them" ([Asset library](https://learn.microsoft.com/dynamics365/customer-insights/journeys/upload-images-files)). **Verified** in a test environment: an anonymous request to `msdyncrm_blobcdnuri` returned HTTP 200 with `cache-control: public` and `access-control-allow-origin: *`.
  - Microsoft: assets "can be temporarily cached outside the region", and each CDN "caches its own version for up to 30 days" ([Asset library](https://learn.microsoft.com/dynamics365/customer-insights/journeys/upload-images-files), [Impact of disabling Azure CDN](https://learn.microsoft.com/dynamics365/customer-insights/journeys/cdn-disabling)). A replaced or deleted asset can stay visible for that time.
  - If an administrator disables Azure CDN for CIJ, the asset library becomes unavailable and previously sent images show as broken links ([Impact of disabling Azure CDN](https://learn.microsoft.com/dynamics365/customer-insights/journeys/cdn-disabling)). Inbox items keep the URL for up to 180 days.
- **Customer responsibility:**
  - Put only marketing-grade, non-personal images in the asset library.
  - Accept the out-of-region CDN caching, or host images in a CMS of your choice and leave the image part empty.
  - To change an image, upload a new asset rather than replacing it ([Asset library](https://learn.microsoft.com/dynamics365/customer-insights/journeys/upload-images-files)).
- **Sources:** [Message parts](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-custom-channel-message-parts), [Asset library](https://learn.microsoft.com/dynamics365/customer-insights/journeys/upload-images-files), [Impact of disabling Azure CDN](https://learn.microsoft.com/dynamics365/customer-insights/journeys/cdn-disabling).
