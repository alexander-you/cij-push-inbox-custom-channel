# Production sizing

The template in `infra/` deploys a **dev/test** configuration. This page lists, per component, what the template deploys, the tier recommended for production, when you need a higher tier, and what each choice means. It intentionally contains no prices; use the [Azure pricing calculator](https://azure.microsoft.com/pricing/calculator/) with your own volumes.

Statements are taken from the linked Microsoft Learn pages. Anything not confirmed there is labelled **Unverified**.

To change a tier, edit the `sku` of the resource in `infra/resources.bicep` and run `azd provision`.

## Summary

| Component | Deployed (dev/test) | Recommended for production | Move higher when |
|---|---|---|---|
| Notification Hubs | Free | **Basic** (small) or **Standard** | You need more than 200,000 devices, rich telemetry, or availability zones |
| Azure Functions | Flex Consumption, no always-ready instances | **Flex Consumption** with always-ready instances | Rarely; Premium is not required for VNet integration |
| Service Bus | Basic | **Basic** | Your security policy requires Service Bus to be private (Premium) |
| Cosmos DB | Serverless | **Serverless** for intermittent traffic | Sustained traffic, multi-region, or a latency SLA on writes (provisioned / autoscale) |
| Static Web Apps (reference PWA) | Free | **Standard**, if you host the PWA at all | You need an SLA or private endpoints |
| Key Vault | Standard | **Standard** | You need HSM-protected keys (Premium) |
| Storage (Functions deployment) | Standard LRS | Standard, with the redundancy your recovery plan requires | Zone or regional outage tolerance |
| Log Analytics / Application Insights | 30-day retention | Retention per your audit policy | Longer retention or high telemetry volume (sampling) |
| Private networking (VNet, private endpoints, private DNS) | Always deployed | **Conditional** on your security policy | — |

## Notification Hubs

The [Notification Hubs FAQ](https://learn.microsoft.com/azure/notification-hubs/notification-hubs-push-notification-faq) defines the tiers:

- **Free:** "a good starting point for exploring push capabilities. It's not recommended for production apps." 500 devices and 1 million pushes per month, **no SLA**.
- **Basic:** "recommended for smaller production apps." 200,000 devices and 10 million pushes per month as a baseline.
- **Standard:** "recommended for medium to large production apps." 10 million devices and 10 million pushes per month as a baseline. It adds Standard-only features, including rich telemetry (per-message telemetry and PNS feedback) and multi-tenancy.
- **SLA:** Basic and Standard are covered by a 99.9% SLA (same FAQ). Delivery from the platform push services (FCM, APNs, Web Push) to the device is outside that SLA.

The [Notification Hubs reliability guide](https://learn.microsoft.com/azure/reliability/reliability-notification-hubs) recommends, for production:

- Basic or Standard, so the namespace is eligible for the SLA;
- zone redundancy;
- metadata disaster recovery to another region.

Two options are add-ons:

- **Availability zones:** an add-on.
- **Private Link:** the [Private Link page](https://learn.microsoft.com/azure/notification-hubs/private-link) states that it is available "for an additional fee" and is "currently in preview", enabled through Microsoft support.

**Recommendation:** Basic for a first production rollout. Use Standard if you expect more than 200,000 registered devices, or need per-message telemetry. This solution's delivery report relies only on the send outcome, not on rich telemetry.

## Azure Functions (Channel API, Dispatcher, Inbox API)

The template uses the Flex Consumption plan (`FC1`, 2,048 MB instances, maximum 40 on-demand instances). According to [Flex Consumption plan hosting](https://learn.microsoft.com/azure/azure-functions/flex-consumption-plan):

- It supports **virtual network integration**. The Consumption plan doesn't. Premium is therefore not needed for private networking.
- **Always-ready instances** are optional dedicated instances that reduce cold starts. They are billed separately from on-demand execution.
- The maximum instance count limits only on-demand instances; always-ready instances are not counted against it.
- With zone redundancy, you must configure at least two always-ready instances.
- It scales out to 1,000 instances and runs on Linux only.

**Recommendation:** stay on Flex Consumption.

- Add one or two always-ready instances for the HTTP functions (Channel API, Inbox API) if cold-start latency on the first send after idle time matters. See [Set always-ready instance counts](https://learn.microsoft.com/azure/azure-functions/flex-consumption-how-to).
- Raise `maximumInstanceCount` only if the Dispatcher can't keep up with the queue.

## Service Bus

According to [Service Bus Premium messaging](https://learn.microsoft.com/azure/service-bus-messaging/service-bus-premium-messaging):

- **Basic** "supports queues only."
- **Standard** "adds topics and subscriptions, sessions, transactions, and duplicate detection, on shared capacity."
- **Premium** provides dedicated messaging units, **virtual network integration and Private Link**, geo-replication and large messages.

[Duplicate detection](https://learn.microsoft.com/azure/service-bus-messaging/duplicate-detection) is not supported on Basic.

**Verified in the code:** this solution doesn't use any Standard-only feature.

- `ChannelApi.cs` sends to one queue.
- `Dispatcher.cs` reads it with a `ServiceBusTrigger`.
- No topics, sessions, transactions or scheduled messages are used.
- Idempotency is enforced in Cosmos DB (the `dispatches` container keyed on `idempotencyId`), not by Service Bus duplicate detection.

**Recommendation:** keep Basic. Move to Premium only if your security policy requires Service Bus to have no public endpoint. The deployed namespace uses Entra ID (local SAS authentication is disabled) but is reachable on a public endpoint; see [security considerations, SEC-09](security.md#sec-09-network-exposure).

## Cosmos DB (inbox and idempotency store)

[Throughput: provisioned vs. serverless](https://learn.microsoft.com/azure/cosmos-db/throughput-serverless) compares the two modes:

| | Serverless | Provisioned throughput (manual or autoscale) |
|---|---|---|
| **Best suited for** | Intermittent or unpredictable traffic, low average-to-peak ratio | Sustained traffic requiring predictable performance |
| **Regions** | A single Azure region only | Any number of regions |
| **Performance commitment** | <10 ms point reads and <30 ms writes, covered by an SLO | <10 ms point reads and writes, covered by an SLA |

More limits:

- **Throughput:** a serverless container provides up to 5,000 RU/s per physical partition ([serverless performance](https://learn.microsoft.com/azure/cosmos-db/serverless-performance)).
- **Region:** you can't add regions to a serverless account later ([serverless](https://learn.microsoft.com/azure/cosmos-db/serverless)).
- **Availability zones:** serverless supports them in designated regions, with SLAs aligned with single-region writes (same page).

**Recommendation:**

- **Serverless** fits campaign-style sends, which are bursty.
- Choose **provisioned autoscale** if:
  - you send continuously at high volume;
  - you need multi-region;
  - you need an SLA rather than an SLO on write latency.
- Moving an existing account between modes is not covered here (**Unverified**). Plan to create a new account and migrate data. Inbox items are short-lived (TTL), which makes this easier.

## Static Web Apps (reference PWA only)

The PWA is a test harness and reference implementation. In production, your own app replaces it; see [developer integration](developer-integration.md).

If you do host a web inbox on Static Web Apps, [Static Web Apps plans](https://learn.microsoft.com/azure/static-web-apps/plans) applies:

- The **Free** plan is "for personal projects" and has no SLA and no private endpoints.
- The **Standard** plan is "for production apps," with an SLA and private endpoints.

## Key Vault

[Key Vault overview](https://learn.microsoft.com/azure/key-vault/general/overview):

- **Standard** uses software-protected keys.
- **Premium** adds HSM-protected keys.

This solution stores only one secret (the Notification Hubs connection string) and no keys, so **Standard is sufficient**.

## Storage (Functions deployment storage)

The account holds the Functions deployment package and runtime state, not message data.

- The template uses LRS.
- Choose ZRS or geo-redundant storage if your recovery plan requires zone or regional resilience ([Storage redundancy](https://learn.microsoft.com/azure/storage/common/storage-redundancy)).

## Log Analytics and Application Insights

- **Retention:** the template sets 30-day retention. Adjust it to your audit policy ([Configure data retention](https://learn.microsoft.com/azure/azure-monitor/logs/data-retention-configure)).
- **Volume:** at high send volumes, consider [sampling](https://learn.microsoft.com/azure/azure-monitor/app/sampling-classic-api) to limit telemetry volume.
- **Personal data:** recipient keys are logged only as hashes; see [security considerations, SEC-15](security.md#sec-15-logging-and-monitoring-without-personal-data).

## Private networking (conditional)

The template always deploys:

- a virtual network;
- private endpoints for Storage, Cosmos DB and Key Vault, with private DNS zones;
- a network security perimeter.

It does this because the environment it was built in enforces private data services through Azure Policy. Whether you need this depends on **your** security policy:

- **No such policy:** private networking is optional hardening. Making it a template parameter is planned.
- **Policy requires private PaaS:** keep it.
  - Also plan for Service Bus Premium (Private Link isn't available on Basic or Standard).
  - Consider Notification Hubs Private Link (preview, add-on), if your policy covers it.

References:

- [What is a private endpoint?](https://learn.microsoft.com/azure/private-link/private-endpoint-overview)
- [Network security perimeter concepts](https://learn.microsoft.com/azure/private-link/network-security-perimeter-concepts), now generally available
- [Azure Functions networking options](https://learn.microsoft.com/azure/azure-functions/functions-networking-options)

## Example profiles

| Profile | Notification Hubs | Functions | Service Bus | Cosmos DB | Networking |
|---|---|---|---|---|---|
| **Small production** (up to 200,000 devices, campaign sends) | Basic | Flex Consumption, 1 always-ready instance for HTTP | Basic | Serverless | Public endpoints with Entra ID, or private if policy requires |
| **Medium / large production** (more than 200,000 devices, continuous sends) | Standard, availability zones | Flex Consumption, 2+ always-ready instances, zone redundant | Basic | Provisioned autoscale, zone redundant | Per policy |
| **Strict private** (policy forbids public data-plane endpoints) | Basic or Standard, plus Private Link (preview) | Flex Consumption with VNet integration | **Premium** | Serverless or autoscale, private endpoint | VNet, private endpoints, private DNS, network security perimeter |

Use the [Azure pricing calculator](https://azure.microsoft.com/pricing/calculator/) to estimate each profile with your own device count and send volume. Microsoft's SLAs are listed at [Service Level Agreements for Online Services](https://www.microsoft.com/licensing/docs/view/Service-Level-Agreements-SLA-for-Online-Services).
