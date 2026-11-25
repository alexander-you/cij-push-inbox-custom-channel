# Data model specification

Dataverse and Azure data model for the Push + Inbox custom channel: tables, columns, user-facing descriptions, relationships, custom APIs, roles, solution layout, Azure schemas and identity mapping. Rationale: [design-choices.md](design-choices.md). Publisher prefix: `alex`. Installation: [installation.md](installation.md). Labels exist in English (1033) and Hebrew (1037).

## 1. How the pieces connect

A custom channel is a set of base CIJ tables plus your own components. Some links are real lookups. Others are **by-name references**: plain strings or GUIDs stored on the channel definition, which aren't enforced by Dataverse ([Channel definition](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-define-channel-definition)).

| From | To | Link type | Set where | Source |
|---|---|---|---|---|
| `msdyn_channelmessagepart.msdyn_channeldefinitionid` | `msdyn_channeldefinition` | Lookup (N:1) | `customizations.xml` | ([Message parts](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-custom-channel-message-parts)) |
| `msdyn_channeldefinitionlocale.msdyn_channeldefinitionid` | `msdyn_channeldefinition` | Lookup (N:1), optional | `customizations.xml` | ([Custom channel localization](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-custom-channel-localization-admin)) |
| `msdyn_channelinstance.msdyn_channeldefinitionid` | `msdyn_channeldefinition` | Lookup (N:1) | Created by CIJ when an admin adds the channel | ([Microsoft sample solution](https://download.microsoft.com/download/44685e3a-8b09-4caa-be78-12bc6867c8d1/SampleCustomChannel.zip)) (sample plug-in query) |
| `msdyn_channelinstance.msdyn_extendedentityid` | **our** `alex_pushchannelinstance` | **Polymorphic lookup.** A hand-authored 1:N relationship, `CascadeDelete=RemoveLink` | `Other/Relationships/alex_pushchannelinstance.xml` | ([Custom channel instance](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-define-custom-channel-instance), [Multi-table lookups](https://learn.microsoft.com/power-apps/developer/data-platform/webapi/multitable-lookup)) |
| `msdyn_channeldefinition.msdyn_channeldefinitionexternalentity` | `alex_pushchannelinstance` | By name (string) | `customizations.xml` | ([Channel definition](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-define-channel-definition), [Custom channel instance](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-define-custom-channel-instance)) |
| `msdyn_channeldefinition.msdyn_channeldefinitionexternalformid` | Main form of `alex_pushchannelinstance` | By ID (GUID) | `customizations.xml` | ([Channel definition](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-define-channel-definition), [Custom channel instance](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-define-custom-channel-instance)) |
| `msdyn_channeldefinition.msdyn_outboundendpointurltemplate` | Custom API `alex_PushOutbound` | By name, as `/{uniquename}` | `customizations.xml` | ([Channel definition](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-define-channel-definition), [Custom channel custom APIs](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-custom-channel-custom-api)) |
| `customapi.plugintypeid` | Plug-in type in our assembly | Lookup | `customapis/<name>/customapi.xml` | ([Microsoft sample solution](https://download.microsoft.com/download/44685e3a-8b09-4caa-be78-12bc6867c8d1/SampleCustomChannel.zip), [Custom API columns](https://learn.microsoft.com/power-apps/developer/data-platform/customapi-table-columns)) |
| Role **Marketing Services User Extensible Role** (required), plus **Cxp Channel Definitions Services User** where customizable | `alex_pushchannelinstance` | **Read** privilege | Post-import step (see section 4) | ([Channel instance account and Extensible role](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-define-channel-instance-account), [Custom channel instance](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-define-custom-channel-instance), [AddPrivilegesRole](https://learn.microsoft.com/power-apps/developer/data-platform/webapi/reference/addprivilegesrole)) |
| Message `Message[<msdyn_name>]` in the outbound payload | `msdyn_channelmessagepart.msdyn_name` | By name (dictionary key) | Runtime | ([Message parts](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-custom-channel-message-parts), [Custom channel custom APIs](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-custom-channel-custom-api)) |

**Runtime lookup, as in the sample's plug-in ([Microsoft sample solution](https://download.microsoft.com/download/44685e3a-8b09-4caa-be78-12bc6867c8d1/SampleCustomChannel.zip)):**

1. Query `msdyn_channelinstance` where `msdyn_channeldefinitionid` = `payload.ChannelDefinitionId` and `msdyn_contactpoint` = `payload.From`. The sample notes there's only one instance for each definition and contact point.
2. Read `msdyn_extendedentityid`. This is an `EntityReference` to our config row.
3. Retrieve our config row.

So the **sender ("From") is the channel instance's contact point**. Our extended row holds per-sender configuration.

## 2. Dataverse ER diagram

Solid lines are real lookups or relationships. Dotted lines are by-name references or privileges.

```mermaid
erDiagram
  msdyn_channeldefinition ||--|{ msdyn_channelmessagepart : "msdyn_channeldefinitionid"
  msdyn_channeldefinition ||--o{ msdyn_channeldefinitionlocale : "msdyn_channeldefinitionid"
  msdyn_channeldefinition ||--o{ msdyn_channelinstance : "msdyn_channeldefinitionid"
  alex_pushchannelinstance ||--o{ msdyn_channelinstance : "msdyn_extendedentityid (polymorphic, RemoveLink)"
  msdyn_channeldefinition }o..|| alex_pushchannelinstance : "externalentity + externalformid (by name)"
  msdyn_channeldefinition }o..|| customapi_PushOutbound : "outboundendpointurltemplate (by name)"
  customapi_PushOutbound ||--|| plugintype_Outbound : "plugintypeid"
  customapi_PushDeliveryReport ||--|| plugintype_DeliveryReport : "plugintypeid"
  plugintype_DeliveryReport }o..|| msdyn_D365ChannelsNotification : "calls (base API)"
  role_MktServicesUserExtensible }o..o{ alex_pushchannelinstance : "Read (Organization)"
  role_PushInboxIntegration }o..o{ customapi_PushDeliveryReport : "app user executes"

  msdyn_channeldefinition {
    guid msdyn_channeldefinitionid PK
    string msdyn_channeltype "Custom"
    string msdyn_outboundendpointurltemplate "/alex_PushOutbound"
    bit msdyn_hasdeliveryreceipt "1"
    bit msdyn_hasinbound "0"
    bit msdyn_supportsaccount "0"
  }
  alex_pushchannelinstance {
    guid alex_pushchannelinstanceid PK
    string alex_name
    string alex_appkey
    int alex_defaultexpirydays
  }
  msdyn_channelinstance {
    guid msdyn_channelinstanceid PK
    string msdyn_contactpoint "= payload.From"
    lookup msdyn_extendedentityid
  }
```

## 3. Dataverse components (exact definitions)

### 3.1 Channel definition (`customizations.xml` → `msdyn_channeldefinitions`) ([Channel definition](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-define-channel-definition))

| Attribute | Value | Note |
|---|---|---|
| `msdyn_channeldefinitionid` (XML attribute) | **New lowercase GUID** | Never reuse the sample's GUIDs. |
| `msdyn_channeltype` | `Custom` | |
| `msdyn_displayname` / `msdyn_description` | `Push + Inbox` / … | Can be localized through locales ([Custom channel localization](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-custom-channel-localization-admin)). |
| `msdyn_outboundendpointurltemplate` | `/alex_PushOutbound` | Leading slash plus the custom API unique name ([Channel definition](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-define-channel-definition), [Microsoft sample solution](https://download.microsoft.com/download/44685e3a-8b09-4caa-be78-12bc6867c8d1/SampleCustomChannel.zip)). |
| `msdyn_hasdeliveryreceipt` | `1` | We report `Delivered` and `NotDelivered` ([Custom channel custom APIs](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-custom-channel-custom-api)). |
| `msdyn_hasinbound` | `0` | Inbound is documented for SMS only ([Custom channel custom APIs](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-custom-channel-custom-api)). |
| `msdyn_supportsaccount` | `0` | Docs: "For SMS, it must be true; otherwise, false" ([Channel definition](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-define-channel-definition)). Account entities are "Not supported" for Custom ([Custom channels in CIJ](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-extend-outreach-custom-channels)). The sample sets `1` and points to an account entity it doesn't ship, so don't copy that ([Microsoft sample solution](https://download.microsoft.com/download/44685e3a-8b09-4caa-be78-12bc6867c8d1/SampleCustomChannel.zip)). |
| `msdyn_channeldefinitionexternalentity` | `alex_pushchannelinstance` | |
| `msdyn_channeldefinitionexternalformid` | Main form GUID of the entity above (lowercase) | Rendered in the CIJ channel setup wizard ([Custom channel instance](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-define-custom-channel-instance)). |
| `msdyn_messageformid` | *(omit)* | Use the built-in generic editor, as the docs recommend ([Message editor](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-custom-channel-message-editor)). |
| `msdyn_specialconsentrequired`, `msdyn_supportsattachment`, `msdyn_supportsbinary` | `0` | As in the sample and docs examples ([Channel definition](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-define-channel-definition), [Microsoft sample solution](https://download.microsoft.com/download/44685e3a-8b09-4caa-be78-12bc6867c8d1/SampleCustomChannel.zip)). |
| `msdyn_outboundbatchendpointurltemplate` | *(omit)* | Undocumented, so not used. Single-message API only ([Custom channel custom APIs](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-custom-channel-custom-api)). |

### 3.2 Message parts (`customizations.xml` → `msdyn_channelmessageparts`) ([Message parts](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-custom-channel-message-parts))

Each part has its own lowercase GUID, `msdyn_channeldefinitionid` set to the definition above, and `msdyn_name` used as the key of `payload.Message` ([Message parts](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-custom-channel-message-parts), [Custom channel custom APIs](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-custom-channel-custom-api)). `msdyn_order`: a larger number puts the part higher in the editor ([Message parts](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-custom-channel-message-parts)).

| `msdyn_name` | Type (`msdyn_type`) | Required | Max length | Order | Notes |
|---|---|---|---|---|---|
| `title` | plaintext `192350000` | Yes | 65 | 70 | Push title and inbox subject. |
| `body` | plaintext `192350000` | Yes | 1000 | 60 | Clients truncate the alert body. The inbox shows it in full. |
| `imageId` | image `192350004` | No | – | 50 | The marketer picks an image from the CIJ asset library. The part holds the `msdyncrm_file` ID ([Message parts](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-custom-channel-message-parts)). The outbound plug-in resolves it to the asset's public URL; see [Image resolution](#image-resolution). |
| `actionUrl` | plaintext `192350000` | No | 1000 | 40 | Deep link or URL. CIJ replaces `https://` URLs with tracked short links (verified), so a tap counts as a click; they expire after 6 months ([Create custom channels](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-create-custom-channels)).  |
| `deliveryMode` | options `192350008` | Yes | – | 30 | `msdyn_options`: `[{"label":"Alert + Inbox","value":1},{"label":"Inbox only","value":2},{"label":"Alert only","value":3}]` ([Message parts](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-custom-channel-message-parts)). |
| `inboxExpiryDays` | number `192350006` | No | – | 20 | Capped at 180 by the Channel API. The default comes from channel instance config. |
| `replaceKey` | plaintext `192350000` | No | 100 | 10 | The same key replaces the recipient's earlier inbox item. |

#### Image resolution

The `imageId` part uses the CIJ asset library, so marketers don't type URLs.

- **Stored value.** The message (`msdynmkt_customchannelmessage.msdynmkt_messageparts`, JSON keyed by `msdyn_name`) stores the bare `msdyncrm_file` ID of the chosen asset, or `null` when none is chosen. Learn: "The message part is an image ID of the msdyncrm_file entity" ([Message parts](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-custom-channel-message-parts)).
- **Public URL.** `msdyncrm_file.msdyncrm_blobcdnuri`, for example `https://<geo CDN host>/<organization ID>/digitalassets/images/<file ID>?ts=<version>`. It is anonymous by design: "Assets must be available publicly on the internet" ([Asset library](https://learn.microsoft.com/dynamics365/customer-insights/journeys/upload-images-files)). It differs per environment, so it is always read from the column, never built. The other URL columns (`msdyncrm_bloburi`, `msdyncrm_mainimage_url`) are Dataverse-relative and require sign-in.
- **Where it is resolved.** The `alex_PushOutbound` plug-in, at send time:
  1. Empty value: no image.
  2. An absolute `https` URL: passed through unchanged.
  3. A GUID: `Retrieve("msdyncrm_file", id, msdyncrm_blobcdnuri, msdyncrm_contenttype)`. If the asset has an `https` URL and an `image/*` content type, that URL is used.
  4. Anything else, or a missing asset: the message is sent **without** an image, and the plug-in writes a trace with the asset ID. The send doesn't fail, because the image is optional.
- **Contract with Azure.** The plug-in still forwards the CIJ payload unchanged, and sends the resolved URL in the `x-push-image-url` header. The Channel API uses that header, or `Message.imageId` when it is already an `https` URL, and stores it as `imageUrl` on the inbox item.
- **Privileges.** The plug-in runs as the SYSTEM user (`CreateOrganizationService(null)`), so no role change is needed. The Azure managed identity needs no Dataverse read access.
- **Upgrade from the plain-text `imageUrl` part.** Importing the new solution over an earlier version renames the part and changes its type, but keeps `msdyn_maxlength` at 1000. Clear it on the `msdyn_channelmessagepart` row if you want the definition to match a fresh install. Messages saved with the old plain-text part keep an `imageUrl` key that is no longer read; select the image again.

### 3.3 Extended channel instance entity `alex_pushchannelinstance` ([Custom channel instance](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-define-custom-channel-instance))

- **Naming:** the docs require a "lowerCamelCase" logical name ([Custom channel instance](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-define-custom-channel-instance)). Dataverse logical names are lowercase, so we follow the sample exactly: the prefix plus a single lowercase word, with schema name equal to logical name (`cr65f_samplechannelinstance` ([Microsoft sample solution](https://download.microsoft.com/download/44685e3a-8b09-4caa-be78-12bc6867c8d1/SampleCustomChannel.zip))).
- **Ownership:** User-owned, as in the sample ([Microsoft sample solution](https://download.microsoft.com/download/44685e3a-8b09-4caa-be78-12bc6867c8d1/SampleCustomChannel.zip)). CIJ's service roles get Read at **Organization** depth.
- **Columns:** credentials are deliberately absent.

| Column | Type | Required | Purpose |
|---|---|---|---|
| `alex_name` | Text (100), primary name | Yes | Same pattern as the sample. CIJ controls also show the name ([Custom channel instance](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-define-custom-channel-instance)). |
| `alex_appkey` | Text (100) | Yes | Logical app or tenant key in Azure. Selects the Notification Hub and inbox namespace. Tag-safe characters only ([Notification Hubs tag expressions](https://learn.microsoft.com/azure/notification-hubs/notification-hubs-tags-segment-push-message)). |
| `alex_defaultexpirydays` | Whole number (1–180) | No | Default inbox expiry when the message part is empty. Defaults to 30 on create: the form OnLoad script `alex_/scripts/pushchannelinstance.js` sets it, and the PreOperation Create plug-in `DefaultExpiryPlugin` sets it for API-created rows. |
| `alex_channelapikey` | Text (200), **column security** on | Test only | Function key for test environments. Production uses Power Platform managed identity instead ([Power Platform managed identity](https://learn.microsoft.com/power-platform/admin/set-up-managed-identity), [Column-level security](https://learn.microsoft.com/power-platform/admin/field-level-security)). |

- **Form:** one **main** form with `alex_appkey` and `alex_defaultexpirydays`. Its lowercase GUID goes into `msdyn_channeldefinitionexternalformid`. Name and description fields aren't needed on the form ([Custom channel instance](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-define-custom-channel-instance)).
- **Channel API base URL:** stored in environment variable `alex_ChannelApiBaseUrl` (type String), with its value set per environment at import ([Environment variables](https://learn.microsoft.com/power-apps/maker/data-platform/environmentvariables)).
  - Don't use a *secret*-type environment variable from a plug-in. The documented retrieval is the `RetrieveEnvironmentVariableSecretValue` action in flows ([Key Vault secret environment variables](https://learn.microsoft.com/power-apps/maker/data-platform/environmentvariables-azure-key-vault-secrets)).

### 3.4 The polymorphic relationship (hand-authored) ([Custom channel instance](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-define-custom-channel-instance), [Microsoft sample solution](https://download.microsoft.com/download/44685e3a-8b09-4caa-be78-12bc6867c8d1/SampleCustomChannel.zip))

`Other/Relationships/alex_pushchannelinstance.xml`:

```xml
<?xml version="1.0" encoding="utf-8"?>
<EntityRelationships xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
  <EntityRelationship Name="msdyn_ChannelInstance_extendedentityid_alex_pushchannelinstance">
    <EntityRelationshipType>OneToMany</EntityRelationshipType>
    <IsCustomizable>0</IsCustomizable>
    <IntroducedVersion>1.0.0.0</IntroducedVersion>
    <IsHierarchical>0</IsHierarchical>
    <ReferencingEntityName>msdyn_ChannelInstance</ReferencingEntityName>
    <ReferencedEntityName>alex_pushchannelinstance</ReferencedEntityName>
    <CascadeAssign>NoCascade</CascadeAssign>
    <CascadeDelete>RemoveLink</CascadeDelete>
    <CascadeReparent>NoCascade</CascadeReparent>
    <CascadeShare>NoCascade</CascadeShare>
    <CascadeUnshare>NoCascade</CascadeUnshare>
    <CascadeRollupView>NoCascade</CascadeRollupView>
    <IsValidForAdvancedFind>1</IsValidForAdvancedFind>
    <ReferencingAttributeName>msdyn_extendedentityId</ReferencingAttributeName>
    <RelationshipDescription>
      <Descriptions>
        <Description description="" languagecode="1033" />
      </Descriptions>
    </RelationshipDescription>
    <EntityRelationshipRoles>
      <EntityRelationshipRole>
        <NavPaneDisplayOption>UseCollectionName</NavPaneDisplayOption>
        <NavPaneArea>Details</NavPaneArea>
        <NavPaneOrder>10000</NavPaneOrder>
        <NavigationPropertyName>msdyn_extendedentityid_alex_pushchannelinstance</NavigationPropertyName>
        <RelationshipRoleType>1</RelationshipRoleType>
      </EntityRelationshipRole>
      <EntityRelationshipRole>
        <NavigationPropertyName>msdyn_ChannelInstance_extendedentityid_alex_pushchannelinstance</NavigationPropertyName>
        <RelationshipRoleType>0</RelationshipRoleType>
      </EntityRelationshipRole>
    </EntityRelationshipRoles>
  </EntityRelationship>
</EntityRelationships>
```

Also required:

- In `Other/Relationships.xml`, add `<EntityRelationship Name="msdyn_ChannelInstance_extendedentityid_alex_pushchannelinstance" />` ([Custom channel instance](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-define-custom-channel-instance)).
- Add `Entities/msdyn_ChannelInstance/Entity.xml` as a **shell**. It declares only the existing `msdyn_extendedentityId` lookup attribute (`LookupStyle=polymorphic`, `unmodified="1"`).
- In `Solution.xml`, add `msdyn_channelinstance` as a root component with `behavior="1"`. This is how the sample ships it ([Microsoft sample solution](https://download.microsoft.com/download/44685e3a-8b09-4caa-be78-12bc6867c8d1/SampleCustomChannel.zip)).
- Notes on names:
  - The case matters: `msdyn_ChannelInstance` and `msdyn_extendedentityId` are **schema names**, while navigation property names use lowercase `extendedentityid`.
  - These are copied verbatim from the docs and sample. Don't "normalize" them ([Custom channel instance](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-define-custom-channel-instance), [Microsoft sample solution](https://download.microsoft.com/download/44685e3a-8b09-4caa-be78-12bc6867c8d1/SampleCustomChannel.zip)).

### 3.5 Custom APIs ([Custom channel custom APIs](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-custom-channel-custom-api), [Microsoft sample solution](https://download.microsoft.com/download/44685e3a-8b09-4caa-be78-12bc6867c8d1/SampleCustomChannel.zip), [Custom API columns](https://learn.microsoft.com/power-apps/developer/data-platform/customapi-table-columns))

Both are unbound actions:

- `bindingtype=0` (Global), `isfunction=0`, `allowedcustomprocessingsteptype=0` (None), `isprivate=0`, `iscustomizable=0`.
- The implementing plug-in is linked through `plugintypeid` on the custom API itself. **No SDK message processing step is registered**, which is how the sample does it.

| Unique name | Request parameter | Response property | Plug-in behavior |
|---|---|---|---|
| `alex_PushOutbound` | `payload` (String, type 10, required) | `response` (String, type 10) | Deserializes the outbound contract ([Custom channel custom APIs](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-custom-channel-custom-api)) and resolves the channel instance config (section 1). It POSTs to `{alex_ChannelApiBaseUrl}/messages` with a timeout under 20 s total ([Custom channel custom APIs](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-custom-channel-custom-api), [Plug-in external call timeout](https://learn.microsoft.com/power-apps/developer/data-platform/best-practices/business-logic/set-timeout-for-external-calls-from-plug-ins)). It returns `Sent` with a `MessageId` on 202, or `NotSent` with `Details` on a non-retriable 4xx. |
| `alex_PushDeliveryReport` | `payload` (String, type 10, required) | `response` (String, type 10) | Called by the Azure Dispatcher, running as an application user ([Application users](https://learn.microsoft.com/power-platform/admin/manage-application-users)). It builds the notification contract and executes `msdyn_D365ChannelsNotification` with `notificationPayLoad` ([Custom channel custom APIs](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-custom-channel-custom-api), [Microsoft sample solution](https://download.microsoft.com/download/44685e3a-8b09-4caa-be78-12bc6867c8d1/SampleCustomChannel.zip)). |

**Outbound payload fields we use ([Custom channel custom APIs](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-custom-channel-custom-api)):**

| Field | Use |
|---|---|
| `ChannelDefinitionId`, `From` | Instance lookup |
| `RequestId` | Correlation |
| `IdempotencyId` | De-duplication across CIJ retries |
| `To` | Fallback recipient |
| `Message{}` | The message parts |
| `MarketingAppContext.UserId`, `UserEntityType` | The recipient's CIJ record (null for test sends) |
| `MarketingAppContext.CustomerJourneyId` | Analytics and debugging |
| `MarketingAppContext.IsTestSend` | Test handling |

### 3.6 Security roles

| Role | Shipped how | Privileges |
|---|---|---|
| **Marketing Services User Extensible Role** (Microsoft-owned, customizable) | **Not** put in our solution. A post-import script calls `AddPrivilegesRole` with `prvReadalex_pushchannelinstance` at Global (Organization) depth ([AddPrivilegesRole](https://learn.microsoft.com/power-apps/developer/data-platform/webapi/reference/addprivilegesrole)). The CIJ application user holds this role. | Read on `alex_pushchannelinstance` (**required** ([Channel instance account and Extensible role](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-define-channel-instance-account))) |
| **Cxp Channel Definitions Services User** (Microsoft-owned) | Same script, best effort. Skipped with a warning where the role is locked (`0x8004023b`) ([Custom channel instance](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-define-custom-channel-instance), [Managed properties](https://learn.microsoft.com/power-platform/alm/managed-properties-alm)). | Read on `alex_pushchannelinstance` |
| `Push Inbox Channel Integration` (ours, planned) | In the solution | Assigned to the Azure application user ([Application users](https://learn.microsoft.com/power-platform/admin/manage-application-users)). Read on `alex_pushchannelinstance`, plus the minimum base privileges an app user needs to execute the delivery-report API. |
| `Push Inbox Channel Admin` (ours, planned) | In the solution | Create, read, write, and delete on `alex_pushchannelinstance` for CIJ admins configuring the channel. |
| **Push Inbox Log Writer** (ours) | In the solution | Assigned to the Azure backend's application user. Create, read, write and append on `alex_pushmessagelog` (user level); append-to on contact (organization level), needed to set the contact lookup. No delete: TTL removes rows. Plus the default privileges Dataverse adds to every new role. |
| **Push Inbox Log Reader** (ours) | In the solution | For marketers and admins. Read on `alex_pushmessagelog` (organization level), plus the default privileges Dataverse adds to every new role. |

### 3.7 Field descriptions (written into solution metadata)

**Rule:** every table, column, environment variable and message part we ship must have a non-empty **Description** in the solution metadata. The text below is that description, written for a marketer or CIJ admin rather than a developer. Where it goes:

- Table and column descriptions go in `Entities/<table>/Entity.xml` (`<Descriptions><Description description="…" languagecode="1033"/>`). Dataverse shows them as the field's help text ([Column descriptions](https://learn.microsoft.com/power-apps/maker/data-platform/create-edit-field-portal)). The sample leaves these empty ([Microsoft sample solution](https://download.microsoft.com/download/44685e3a-8b09-4caa-be78-12bc6867c8d1/SampleCustomChannel.zip)), so don't copy it.
- Message part `msdyn_displayname` and `msdyn_description` go in `Customizations.xml` ([Message parts](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-custom-channel-message-parts)). They can be translated through the channel definition locale keys `ChannelMessagePart.<part>.DisplayName` and `.Description` ([Custom channel localization](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-custom-channel-localization-admin)).
- The channel definition's `msdyn_description` and the environment variable's description go in their own XML files ([Channel definition](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-define-channel-definition), [Environment variables](https://learn.microsoft.com/power-apps/maker/data-platform/environmentvariables)).
- `msdyn_description` of a message part is limited to **100 characters** (import fails otherwise), so part descriptions are short.
- Every label and description exists in **1033 (English) and 1037 (Hebrew)**. Table, column, form, view and environment variable labels come through the translation file ([Translate table and column text](https://learn.microsoft.com/power-apps/maker/data-platform/export-customized-entity-field-text-translation)); channel definition and message part labels come through `msdyn_channeldefinitionlocale` ([Custom channel localization](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-custom-channel-localization-admin)). Workflow: [localization.md](localization.md).
- The solution check (`tests/solution/check_solution.py`, run by the pre-commit hook) blocks the commit if any label is empty, if a 1037 label has no Hebrew letters, or if a 1033 label has Hebrew letters.

**Table `alex_pushchannelinstance`**: display name *Push sender* / *שולח פוש*, plural *Push senders* / *שולחי פוש*. Description: "Connection settings for sending push notifications and app inbox messages from Customer Insights - Journeys." / "הגדרות חיבור לשליחת התראות פוש והודעות לתיבת ההודעות באפליקציה מתוך Customer Insights - Journeys."

All 21 columns, including the system columns that come with the table (generated from `Entity.xml`):

| Display name (1033) | Display name (1037) | Logical name | Type | Description (1033) | Description (1037) |
|---|---|---|---|---|---|
| App key | מפתח אפליקציה | `alex_appkey` | nvarchar | Identifies the mobile or web app that receives these messages. Your app team provides it. Use only letters, numbers and hyphens. | מזהה את אפליקציית המובייל או אפליקציית האינטרנט שמקבלת את ההודעות. צוות האפליקציה מספק אותו. יש להשתמש באותיות לועזיות, ספרות ומקפים בלבד. |
| Channel API key | מפתח API של הערוץ | `alex_channelapikey` | nvarchar | Secret key used to connect to the Azure messaging service. Only administrators can see it. For test environments only; production connects without a stored key. | מפתח סודי לחיבור לשירות ההודעות ב-Azure. רק מנהלי מערכת יכולים לראות אותו. לסביבות בדיקה בלבד; בסביבת הייצור החיבור מתבצע ללא מפתח שמור. |
| Default inbox expiry (days) | תוקף ברירת מחדל בתיבת ההודעות (ימים) | `alex_defaultexpirydays` | int | How many days a message stays in the app inbox if the message doesn't set its own value. 1 to 180; default 30. | מספר הימים שהודעה נשארת בתיבת ההודעות באפליקציה, כאשר ההודעה עצמה לא קובעת ערך. בין 1 ל-180; ברירת מחדל 30. |
| Name | שם | `alex_name` | nvarchar | A friendly name for this push sender. Marketers see it when they choose a channel in a journey. | שם ידידותי לשולח הפוש. אנשי השיווק רואים אותו כשהם בוחרים ערוץ במסע לקוח. |
| Push sender ID | מזהה שולח פוש | `alex_pushchannelinstanceid` | primarykey | System-generated unique ID of this push sender. You don't need to enter it. | מזהה ייחודי של שולח הפוש שהמערכת יוצרת. אין צורך להזין אותו. |
| Created By | נוצר על-ידי | `createdby` | lookup | The user who created this push sender. | המשתמש שיצר את שולח הפוש. |
| Created On | תאריך יצירה | `createdon` | datetime | Date and time this push sender was created. | התאריך והשעה שבהם נוצר שולח הפוש. |
| Created By (Delegate) | נוצר על-ידי (נציג) | `createdonbehalfby` | lookup | The user who created this push sender on behalf of another user. | המשתמש שיצר את שולח הפוש בשם משתמש אחר. |
| Import Sequence Number | מספר רצף ייבוא | `importsequencenumber` | int | Set automatically when the record is created by a data import. Used to track the import. | נקבע אוטומטית כאשר הרשומה נוצרת בייבוא נתונים. משמש למעקב אחר הייבוא. |
| Modified By | שונה על-ידי | `modifiedby` | lookup | The user who last changed this push sender. | המשתמש ששינה לאחרונה את שולח הפוש. |
| Modified On | תאריך שינוי | `modifiedon` | datetime | Date and time this push sender was last changed. | התאריך והשעה שבהם שולח הפוש שונה לאחרונה. |
| Modified By (Delegate) | שונה על-ידי (נציג) | `modifiedonbehalfby` | lookup | The user who last changed this push sender on behalf of another user. | המשתמש ששינה לאחרונה את שולח הפוש בשם משתמש אחר. |
| Record Created On | תאריך יצירת הרשומה המקורי | `overriddencreatedon` | datetime | Original creation date when the record was migrated from another system. | תאריך היצירה המקורי, כאשר הרשומה הועברה ממערכת אחרת. |
| Owner | בעלים | `ownerid` | owner | The user or team that owns this push sender and is responsible for it. | המשתמש או הצוות שבבעלותם שולח הפוש והאחראים עליו. |
| Owning Business Unit | יחידה עסקית בעלים | `owningbusinessunit` | lookup | The business unit of the owner. Set automatically from the owner. | היחידה העסקית של הבעלים. נקבעת אוטומטית לפי הבעלים. |
| Owning Team | צוות בעלים | `owningteam` | lookup | Unique identifier for the team that owns the record. | המזהה הייחודי של הצוות שבבעלותו הרשומה. |
| Owning User | משתמש בעלים | `owninguser` | lookup | Unique identifier for the user that owns the record. | המזהה הייחודי של המשתמש שבבעלותו הרשומה. |
| Status | מצב | `statecode` | state | Status of the Push sender | המצב של שולח הפוש |
| Status Reason | סיבת המצב | `statuscode` | status | Reason for the status of the Push sender | הסיבה למצב של שולח הפוש |
| Time Zone Rule Version Number | מספר גרסה של כלל אזור הזמן | `timezoneruleversionnumber` | int | For internal use only. | לשימוש פנימי בלבד. |
| UTC Conversion Time Zone Code | קוד אזור זמן להמרת UTC | `utcconversiontimezonecode` | int | Time zone code that was in use when the record was created. | קוד אזור הזמן שהיה בשימוש בעת יצירת הרשומה. |

**Environment variable**

| Display name | Schema name | Type | Description (Dataverse metadata) |
|---|---|---|---|
| Push channel service address / כתובת שירות ערוץ הפוש | `alex_ChannelApiBaseUrl` | Text | Web address of the Azure push and inbox service for this environment, for example https://contoso-push.azurewebsites.net. Set it once when the solution is installed. / כתובת האינטרנט של שירות הפוש ותיבת ההודעות ב-Azure עבור סביבה זו, לדוגמה https://contoso-push.azurewebsites.net. יש להגדיר אותה פעם אחת בעת התקנת הפתרון. |

**Channel definition** (`msdyn_displayname` / `msdyn_description`): *Push + Inbox* / "Send push notifications that are also saved to the customer's in-app inbox." Hebrew: *Push ותיבת הודעות* / "שליחת התראות Push שנשמרות גם בתיבת ההודעות של הלקוח באפליקציה."

**Message parts** (what marketers see in the message editor)

| Name (`msdyn_name`) | Type | Display name 1033 / 1037 | Description 1033 (`msdyn_description`, ≤100 chars) | Description 1037 (locale key) |
|---|---|---|---|---|
| `title` | Plain text, required, 65 | Title / כותרת | Headline on the notification and the inbox subject. Up to 65 characters. | הכותרת בהתראה ונושא ההודעה בתיבת ההודעות. עד 65 תווים. |
| `body` | Plain text, required, 1000 | Message / הודעה | Main message text. The notification may shorten it; the inbox shows it in full. | טקסט ההודעה. ההתראה עשויה לקצר אותו; תיבת ההודעות מציגה אותו במלואו. |
| `imageId` | Image | Image / תמונה | Optional image from the asset library, shown with the message. | תמונה אופציונלית מספריית הנכסים, שמוצגת עם ההודעה. |
| `actionUrl` | Plain text, 1000 | Tap action link / קישור לפעולה בהקשה | Where the customer goes on tap: a web page or a screen in the app. | לאן הלקוח מגיע בהקשה: דף אינטרנט או מסך באפליקציה. |
| `deliveryMode` | Options, required | Delivery mode / אופן מסירה | Alert + Inbox, Inbox only (silent), or Alert only (not saved to the inbox). | Alert + Inbox, ‏Inbox only (שקטה) או Alert only (לא נשמרת בתיבת ההודעות). |
| `inboxExpiryDays` | Number | Inbox expiry (days) / תפוגה בתיבת ההודעות (ימים) | Days the message stays in the inbox (max 180). Leave empty for the channel default. | מספר הימים שההודעה נשארת בתיבת ההודעות (עד 180). ריק = ברירת המחדל של הערוץ. |
| `replaceKey` | Plain text, 100 | Replace key / מפתח החלפה | Optional. A newer message with the same key replaces the earlier one. | אופציונלי. הודעה חדשה עם אותו מפתח מחליפה את ההודעה הקודמת. |

## 4. Solution folder layout (unpacked, `pac solution unpack` format) ([SolutionPackager](https://learn.microsoft.com/power-platform/alm/solution-packager-tool), [Microsoft sample solution](https://download.microsoft.com/download/44685e3a-8b09-4caa-be78-12bc6867c8d1/SampleCustomChannel.zip))

```text
solution/AlexPushInboxChannel/
  Other/
    Solution.xml                      root components: msdyn_channelinstance (behavior=1), alex_* entities, plug-in assembly, env var
    Customizations.xml                msdyn_channeldefinitions, msdyn_channelmessageparts, msdyn_channeldefinitionlocales (lowercase GUIDs)
    Relationships.xml                 + <EntityRelationship Name="msdyn_ChannelInstance_extendedentityid_alex_pushchannelinstance" />
    Relationships/
      alex_pushchannelinstance.xml   HAND-AUTHORED polymorphic relationship (section 3.4)
      BusinessUnit.xml Owner.xml SystemUser.xml Team.xml   generated by pac
  Entities/
    msdyn_ChannelInstance/Entity.xml  shell: msdyn_extendedentityId attribute only
    alex_pushchannelinstance/
      Entity.xml
      FormXml/main/{form-guid}.xml    GUID referenced by msdyn_channeldefinitionexternalformid
      SavedQueries/...
  customapis/
    alex_PushOutbound/customapi.xml
      customapirequestparameters/payload/customapirequestparameter.xml
      customapiresponseproperties/response/customapiresponseproperty.xml
    alex_PushDeliveryReport/...      same shape
  PluginAssemblies/AlexPushInboxPlugins-{guid}/AlexPushInboxPlugins.dll(.data.xml)
  Roles/Push Inbox Channel Admin.xml, Push Inbox Channel Integration.xml   (planned)
  environmentvariabledefinitions/alex_ChannelApiBaseUrl/environmentvariabledefinition.xml
deploy/
  post-import.sh                      AddPrivilegesRole on CIJ's service roles (idempotent) ([AddPrivilegesRole](https://learn.microsoft.com/power-apps/developer/data-platform/webapi/reference/addprivilegesrole))
tools/l10n/                           translation export/fill/import/verify (localization.md)
tests/solution/
  check_solution.py                   CI: relationship file + Relationships.xml line + shell entity + lowercase GUIDs + 1033/1037 labels
```

**ALM rule:** the unpacked folder in Git is the source of truth. Always build with `pac solution pack`, run the structure tests first, and never rely on re-exporting the relationship from an environment.

## 5. Azure data model

### 5.1 Cosmos DB for NoSQL, database `pushinbox` (serverless for development)

| Container | Partition key | `id` | TTL | Purpose |
|---|---|---|---|---|
| `inboxItems` | `/recipientKey` | `replaceKey ? hash(appKey + replaceKey) : messageId` | Container `defaultTtl = -1`, so items don't expire unless they set `ttl`. Each item sets `ttl = expiryDays × 86400` ([Cosmos DB time to live](https://learn.microsoft.com/azure/cosmos-db/how-to-time-to-live)). | The recipient's inbox. All reads are single-partition ([Cosmos DB partitioning](https://learn.microsoft.com/azure/cosmos-db/partitioning-overview)). Using a deterministic `id` makes "replace" a plain upsert. |
| `dispatches` | `/idempotencyId` | `idempotencyId` | 30 days | Idempotency: a create conflict means the request is a duplicate, so the API returns the stored result. If the record is still `Pending` (the enqueue failed), a CIJ retry re-enqueues it instead. The Dispatcher records each step (`storedOn`, `pushedOn` + `pushedCount`, `reportedOn` or `reportError`), so a Service Bus redelivery skips the push that already happened and only retries the delivery report. Also maps `requestId` and `messageId` for the delivery report. This replaces the sample's `cr65f_requestmessagemapping` Dataverse table ([Microsoft sample solution](https://download.microsoft.com/download/44685e3a-8b09-4caa-be78-12bc6867c8d1/SampleCustomChannel.zip)), so there are no per-message Dataverse writes ([Dataverse API limits](https://learn.microsoft.com/power-apps/developer/data-platform/api-limits)). |
| `identities` | `/subject` | `subject` | None | Maps the app user's identity (`iss` + `sub` from the app's IdP token) to `recipientKey`. Filled at device registration (section 6). |

`inboxItems` schema:

```json
{
  "id": "string",
  "recipientKey": "contact_6f9619ff-8b86-d011-b42d-00c04fc964ff",
  "appKey": "contoso-retail",
  "messageId": "guid (returned to CIJ as MessageId)",
  "requestId": "string", "idempotencyId": "string",
  "channelDefinitionId": "guid", "customerJourneyId": "string|null",
  "title": "string", "body": "string", "imageUrl": "string|null", "actionUrl": "string|null",
  "deliveryMode": "alertInbox|inbox|alert",
  "replaceKey": "string|null",
  "state": "unread|read|deleted",
  "createdAt": "ISO-8601", "readAt": "ISO-8601|null",
  "ttl": 2592000
}
```

Alert-only messages don't create an `inboxItems` document. Only the `dispatches` record is kept.

### 5.2 Azure Notification Hubs installations ([Notification Hubs installations](https://learn.microsoft.com/azure/notification-hubs/notification-hubs-push-notification-registration-management), [Notification Hubs tag expressions](https://learn.microsoft.com/azure/notification-hubs/notification-hubs-tags-segment-push-message))

| Field | Value |
|---|---|
| `installationId` | A stable ID generated by the client per browser or device |
| `platform` | `browser` (PWA, Web Push/VAPID ([Notification Hubs browser push](https://learn.microsoft.com/azure/notification-hubs/browser-push))), `fcmv1`, or `apns` |
| `pushChannel` | Browser: `endpoint`, `p256dh`, and `auth`. Mobile: the platform token. |
| `tags` | **Set by the Inbox API only, never by the client:** `rk:{recipientKey}` and `app:{appKey}`. Tags are at most 120 characters, using alphanumeric characters and `_ @ # . : -`, with at most 60 per registration ([Notification Hubs tag expressions](https://learn.microsoft.com/azure/notification-hubs/notification-hubs-tags-segment-push-message)). |

The send targets the tag expression `rk:{recipientKey} && app:{appKey}`. AND-only expressions can reference up to 10 tags ([Notification Hubs tag expressions](https://learn.microsoft.com/azure/notification-hubs/notification-hubs-tags-segment-push-message)).

### 5.3 Azure ER diagram

```mermaid
erDiagram
  CIJ_contact ||--o| identity : "recipientKey = contact_{id}"
  identity ||--o{ anh_installation : "tags rk:{recipientKey}"
  CIJ_contact ||--o{ inboxItem : "partition /recipientKey"
  dispatch ||--o| inboxItem : "messageId"
  dispatch }o..|| CIJ_outbound_request : "idempotencyId, requestId"
  alex_pushchannelinstance ||--o{ inboxItem : "appKey"

  inboxItem {
    string id PK
    string recipientKey "partition key"
    string state
    int ttl
  }
  dispatch {
    string idempotencyId PK
    string requestId
    string messageId
    string status
  }
  identity {
    string subject PK
    string recipientKey
  }
  anh_installation {
    string installationId PK
    string platform
    string tags
  }
```

## 6. Recipient identity mapping (CIJ ↔ Azure ↔ device)

1. **At send:** the Channel API computes `recipientKey = lower({MarketingAppContext.UserEntityType}_{MarketingAppContext.UserId})`, for example `contact_<guid>`. `UserId` is the ID of the record receiving the message ([Custom channel custom APIs](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-custom-channel-custom-api)). 
2. **Test sends:** `MarketingAppContext` values are null for test sends ([Custom channel custom APIs](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-custom-channel-custom-api)). The Channel API then uses the `To` value typed in the test-send box, normalized the same way. It reaches only a device registered with that same value. (Implemented this way; the `identities` mapping below isn't implemented.)
3. **At device registration (planned, not implemented):** today `POST /devices` takes `recipientKey` from the caller ([developer integration](developer-integration.md#how-your-app-gets-the-recipient-key)). The target design:
   - The app user calls `POST /devices` with their IdP token.
   - The Inbox API validates the token. It resolves `recipientKey` by finding the CIJ contact through a configured **verified** claim, for example a verified email or a customer ID. The match goes against a Dataverse alternate key ([Alternate keys](https://learn.microsoft.com/power-apps/developer/data-platform/define-alternate-keys-entity)), such as a customer-number column. It uses one Dataverse read per first registration, then caches in `identities` ([Dataverse API limits](https://learn.microsoft.com/power-apps/developer/data-platform/api-limits)).
   - It upserts the ANH installation with server-set tags (section 5.2).
4. **Leads:** the same flow works with `UserEntityType = lead`. Contact-to-lead merges are out of scope for v1.

## 7. Build rules

1. Hand-author the relationship file and its `Relationships.xml` line. Never use the XrmToolBox Polymorphic Lookup Creator ([Custom channel instance](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-define-custom-channel-instance)).
2. Ship the `msdyn_ChannelInstance` shell entity as a root component with `behavior="1"` ([Microsoft sample solution](https://download.microsoft.com/download/44685e3a-8b09-4caa-be78-12bc6867c8d1/SampleCustomChannel.zip)).
3. Keep `alex_pushchannelinstance` a **Standard** table ([Create elastic tables with code](https://learn.microsoft.com/power-apps/developer/data-platform/create-elastic-tables)).
4. Use fresh, lowercase GUIDs everywhere ([Channel definition](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-define-channel-definition), [Message parts](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-custom-channel-message-parts)).
5. Grant **Marketing Services User Extensible Role** Read on the config table after import ([Channel instance account and Extensible role](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-define-channel-instance-account), [AddPrivilegesRole](https://learn.microsoft.com/power-apps/developer/data-platform/webapi/reference/addprivilegesrole)). Don't rely on the Cxp role, which may be locked ([Custom channel instance](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-define-custom-channel-instance), [Managed properties](https://learn.microsoft.com/power-platform/alm/managed-properties-alm)).
6. `msdyn_supportsaccount = 0`, with no account table ([Custom channels in CIJ](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-extend-outreach-custom-channels), [Channel definition](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-define-channel-definition)).
7. Outbound endpoint = `/` + custom API unique name ([Channel definition](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-define-channel-definition)).
8. Custom APIs: one String `payload` in, one String `response` out, unbound, no custom steps ([Custom channel custom APIs](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-custom-channel-custom-api), [Custom API columns](https://learn.microsoft.com/power-apps/developer/data-platform/customapi-table-columns)).
9. Finish the outbound plug-in well under 20 s; de-duplicate on `IdempotencyId` in Azure ([Custom channel custom APIs](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-custom-channel-custom-api), [Plug-in external call timeout](https://learn.microsoft.com/power-apps/developer/data-platform/best-practices/business-logic/set-timeout-for-external-calls-from-plug-ins)).
10. Define message parts only in `Customizations.xml`, never by hand in an environment ([Message parts](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-custom-channel-message-parts)).
11. The image part is type `image` (`192350004`), and the outbound plug-in resolves the `msdyncrm_file` ID to `msdyncrm_blobcdnuri` ([Message parts](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-custom-channel-message-parts), [Asset library](https://learn.microsoft.com/dynamics365/customer-insights/journeys/upload-images-files)).
12. No plain-text credentials in Dataverse; no secret-type environment variables from plug-ins ([Key Vault secret environment variables](https://learn.microsoft.com/power-apps/maker/data-platform/environmentvariables-azure-key-vault-secrets), [Column-level security](https://learn.microsoft.com/power-platform/admin/field-level-security)).
13. Every table, column, environment variable, channel definition and message part has a non-empty description in English (1033) and Hebrew (1037); 1037 is never a copy of 1033 ([Message parts](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-custom-channel-message-parts), [Custom channel localization](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-custom-channel-localization-admin), [Column descriptions](https://learn.microsoft.com/power-apps/maker/data-platform/create-edit-field-portal), [Translate table and column text](https://learn.microsoft.com/power-apps/maker/data-platform/export-customized-entity-field-text-translation)).
14. Only the Inbox API sets Notification Hubs tags ([Notification Hubs installations](https://learn.microsoft.com/azure/notification-hubs/notification-hubs-push-notification-registration-management), [Notification Hubs tag expressions](https://learn.microsoft.com/azure/notification-hubs/notification-hubs-tags-segment-push-message)).
15. Enable container TTL in Cosmos DB (`defaultTtl = -1`) so per-item `ttl` works ([Cosmos DB time to live](https://learn.microsoft.com/azure/cosmos-db/how-to-time-to-live)).
16. Build only from Git with `pac solution pack`; CI structure tests run first ([SolutionPackager](https://learn.microsoft.com/power-platform/alm/solution-packager-tool)).

## 8. Message log (elastic table `alex_pushmessagelog`)

> **Status: implemented and verified in a development environment** (real CIJ journey sends, test sends, and rejected sends). The log is the solution's **reference record**. It is deliberately a plain, append-only event log; what you build on top of it (summary tables, reports, Fabric) is your choice. See [8.8 Options for partners and implementers](#88-options-for-partners-and-implementers).

**Purpose:** one Dataverse row per message event, so admins can audit what was sent, to whom, and with what outcome. It adds the channel-side detail that CIJ analytics don't hold: rejections and their reason, the delivery mode, the inbox **Read** event, and app-side landings from a tracked link. Message content (title, body, image, URLs) is **never** stored.

**Why elastic:** the log is append-heavy, high-volume and expiring. Learn recommends elastic tables for large volumes with automatic removal after a time period, and standard tables when you need strong consistency, transactions or complex joins ([Elastic tables](https://learn.microsoft.com/power-apps/maker/data-platform/create-edit-elastic-tables), [Elastic tables for developers](https://learn.microsoft.com/power-apps/developer/data-platform/elastic-tables)). The log needs none of those.

### 8.1 Table

| Property | Value |
|---|---|
| Logical name | `alex_pushmessagelog` (entity set `alex_pushmessagelogs`) |
| Display name (1033 / 1037) | Push message log / יומן הודעות פוש |
| Table type | Elastic |
| Ownership | User-owned. Rows are owned by the backend's application user; marketers read them through the **Push Inbox Log Reader** role. |
| Change tracking | Enabled, so the table can be selected in Link to Fabric / Azure Synapse Link ([Link to Fabric](https://learn.microsoft.com/power-apps/maker/data-platform/fabric-link-to-data-platform)). |
| Forms and views | Main form, quick-create form and the default views, labelled in English and Hebrew. No charts: elastic tables don't support them. |

### 8.2 Columns

Every column has English and Hebrew display names and descriptions in the solution (`Entities/alex_PushMessageLog/Entity.xml`).

| Logical name | Type | Content |
|---|---|---|
| `alex_pushmessagelogid` | Primary key | Event ID. Deterministic for one-time events (see 8.4), random for repeatable ones. |
| `alex_name` | Text (100) | Summary, for example `Delivered – 3f2a8c1d`. |
| `partitionid` | System | = `alex_messageid`. All events of one message are in one logical partition. |
| `ttlinseconds` | System | Retention; Dataverse deletes the row when it expires. |
| `alex_eventtype` | Choice | See 8.3. |
| `alex_messageid` | Text | Message ID the Channel API returned to CIJ (also the inbox item ID). |
| `alex_requestid` | Text (100) | CIJ `RequestId`. Empty for test sends. |
| `alex_idempotencyid` | Text (100) | CIJ `IdempotencyId`, or a generated `gen-…` value when CIJ sends none. |
| `alex_recipientlogid` | Text | Pseudonymous recipient ID: first 12 hex characters of SHA-256 of the recipient key. Same value as in Application Insights. The raw recipient key is not stored. |
| `alex_ContactId` | Lookup → contact | Resolved when the recipient key is `contact_<guid>`. Empty for test sends and leads. |
| `alex_appkey` | Text (100) | App key of the channel instance. |
| `alex_deliverymode` | Choice | Alert + Inbox / Inbox only / Alert only. |
| `alex_istestsend` | Yes/No | CIJ test send. |
| `alex_pnssuccesscount`, `alex_pnsfailurecount` | Whole number | Devices the push service accepted / rejected. Filled on Dispatched rows **of test sends only**: Notification Hubs returns per-device outcomes only in test mode on the Free tier ([Notification Hubs FAQ](https://learn.microsoft.com/azure/notification-hubs/notification-hubs-push-notification-faq)). |
| `alex_detail` | Text (500) | Reason or error code, for example `NoDevice`, `EmptyMessage`, `not reported to CIJ (test send)`. No content or personal data. |
| `alex_eventon` | Date and time (UTC) | When the event happened. |
| `alex_customerjourneyid` | Text (100) | CIJ journey ID, when present. |
| `alex_channeldefinitionid` | Text | CIJ channel definition ID. |

### 8.3 Event types and who writes them

| Event (value) | Written by | When | Row ID |
|---|---|---|---|
| Accepted (626210000) | Channel API | Message queued; CIJ receives Sent. | Once per message |
| Rejected (626210001) | Channel API | Validation failed (`MissingAppKey`, `EmptyMessage`, `InvalidDeliveryMode`, `MissingRecipient`); CIJ receives NotSent. | Random (no message ID exists yet) |
| Dispatched (626210002) | Dispatcher | Inbox item stored and/or push handed to Notification Hubs in this delivery. Not written again when a redelivered queue message skips steps that are already done. | Once per message |
| Delivered (626210003) | Dispatcher | The Delivered report was sent to CIJ, or skipped for a test send (noted in Detail). | Once per message |
| Not delivered (626210004) | Dispatcher | The NotDelivered report was sent or skipped; reason in Detail. | Once per message |
| Read (626210005) | Inbox API (`POST inbox/{id}/read`) | First time the recipient opens the message in the app inbox. | Once per message |
| Clicked (626210006) | Inbox API (`POST inbox/{id}/click`) | Every time the app opens from a tracked link of this message (see [developer-integration](developer-integration.md#3-how-to-report-clicks)). Like CIJ "Total clicks"; count distinct `alex_messageid` for unique clicks. | Random (one row per landing) |

**Not in the log:** *Blocked* (notification permission revoked). It needs device-level reporting that is not implemented; see [known-limitations](known-limitations.md).

### 8.4 Write path

```mermaid
flowchart LR
  CA[Channel API] -->|event| Q[(Service Bus<br/>messagelog)]
  DI[Dispatcher] -->|event| Q
  IA[Inbox API<br/>read / click] -->|event| Q
  Q --> LW[LogWriter<br/>Function]
  LW -->|Upsert, managed identity| DV[(Dataverse<br/>alex_pushmessagelog)]
```

- **Never blocks a send.** Producers put a small event on a separate `messagelog` queue (best effort; a failure is only logged). The `LogWriter` Function writes to Dataverse. A Dataverse outage or throttling delays the log, never the push, the inbox or the CIJ report.
- **Idempotent.** The row is upserted (`PATCH alex_pushmessagelogs(alex_pushmessagelogid=…,partitionid='…')`). One-time events use a name-based GUID from `messageId + eventType`, so a redelivered event updates the same row ([Use elastic tables with code](https://learn.microsoft.com/power-apps/developer/data-platform/use-elastic-tables)). Elastic tables don't support alternate keys ([Elastic tables](https://learn.microsoft.com/power-apps/maker/data-platform/create-edit-elastic-tables)).
- **Retries.** Queue settings: max delivery count 10, lock 1 minute, message TTL 7 days, dead-letter on expiration. Dataverse 429/5xx throw and are retried by Service Bus ([API limits](https://learn.microsoft.com/power-apps/developer/data-platform/api-limits)).
- **Identity.** The Function App's managed identity is a Dataverse application user ([Application users](https://learn.microsoft.com/power-platform/admin/manage-application-users)) with the **Push Inbox Log Writer** role.
- **Turning it off.** Remove the `MESSAGE_LOG_QUEUE` app setting; producers then skip the log.

### 8.5 Retention (TTL)

| App setting | Default | Applies to |
|---|---|---|
| `MESSAGE_LOG_TTL_DAYS` | 180 (15,552,000 s) | Real sends |
| `MESSAGE_LOG_TEST_TTL_DAYS` | 7 (604,800 s) | Test sends and rejected sends without a CIJ request |

TTL is counted from the row's last modification ([Elastic tables](https://learn.microsoft.com/power-apps/maker/data-platform/create-edit-elastic-tables)). Rows already exported to Synapse Link or Fabric are not deleted there; apply retention in the destination as well ([Elastic tables for developers](https://learn.microsoft.com/power-apps/developer/data-platform/elastic-tables)).

### 8.6 Relationships and security

- `alex_ContactId` is N:1 to contact with `Cascade.None`, the only option for elastic tables ([Create elastic tables with code](https://learn.microsoft.com/power-apps/developer/data-platform/create-elastic-tables)). Deleting a contact doesn't delete its log rows; TTL does. For a data-subject erasure request, delete the rows by `alex_ContactId` and by `alex_recipientlogid`.
- No channel-instance lookup: `alex_appkey` + `alex_channeldefinitionid` identify the instance.
- Roles: see [3.6](#36-security-roles).

### 8.7 Elastic limitations that matter here

From [Elastic tables](https://learn.microsoft.com/power-apps/maker/data-platform/create-edit-elastic-tables):

| Limitation | Effect |
|---|---|
| No charts, business rules or rollup columns; limited aggregates (for example no group by + order by) | Counts per journey or status are computed outside the table. |
| **The Power BI Dataverse connector doesn't support elastic tables** | Report through Fabric / Synapse Link, or through the Web API. |
| No filters on related tables | Filter on the log's own columns (`alex_ContactId`, `alex_customerjourneyid`), not on contact fields. |
| No alternate keys, duplicate detection, table sharing or cascade | Handled by deterministic IDs, the roles and TTL. |
| No table-data import/export | Bulk export goes through Synapse Link / Fabric or the API. |
| Counts against Dataverse **log** capacity | Plan with your admin ([Capacity](https://learn.microsoft.com/power-platform/admin/capacity-storage)). Roughly 3–4 rows per delivered message, plus Read and Clicked rows. |

### 8.8 Options for partners and implementers

The log is the reference record. Pick what fits the customer:

| Option | How | Notes |
|---|---|---|
| **A. Use the log as is** | Model-driven views on `alex_pushmessagelog`, filtered by contact, journey or event type. Add the table to the customer's app and grant **Push Inbox Log Reader**. | Zero build. No charts. |
| **B. Summary table (one row per message × contact)** | A standard table (for example Sent / Delivered / Read / Clicked timestamps) maintained from the log by a Function on the same queue or by a Power Automate flow on log row creation. | Standard tables support charts, rollups, alternate keys and the Power BI connector. Mind API limits; use `UpsertMultiple` for volume ([Bulk operations](https://learn.microsoft.com/power-apps/developer/data-platform/bulk-operations), [API limits](https://learn.microsoft.com/power-apps/developer/data-platform/api-limits)). Uses database capacity. |
| **C. Fabric / Synapse Link + Power BI** | Add `alex_pushmessagelog` to Link to Fabric or Azure Synapse Link ([Link to Fabric](https://learn.microsoft.com/power-apps/maker/data-platform/fabric-link-to-data-platform)); report in Power BI over the lakehouse. | Keeps history beyond TTL. Change tracking is already enabled on the table. Elastic-table export through these links: **Unverified in this solution**; test before you rely on it. |
| **D. CIJ's own interaction data** | For Sent, Delivered, Bounced and Link Clicked, CIJ already records custom-channel interactions (`CustomSent`, `CustomDelivered`, `CustomBounced`, `CustomLinkClicked`, …) ([CDM: Customer Insights – Journeys](https://learn.microsoft.com/common-data-model/schema/core/applicationcommon/foundationcommon/crmcommon/solutions/customerinsightsjourneys/overview), [CustomLinkClicked](https://learn.microsoft.com/common-data-model/schema/core/applicationcommon/foundationcommon/crmcommon/solutions/customerinsightsjourneys/customlinkclicked)). Access them through CIJ Fabric integration ([Fabric integration](https://learn.microsoft.com/dynamics365/customer-insights/journeys/fabric-integration)) or Customer Insights – Data ([Journeys interaction data](https://learn.microsoft.com/dynamics365/customer-insights/data/journeys-interaction-data-segments-measures)). | System of record for journey analytics. Has no Read event and no rejection reasons; join with the log on `RequestId` / message ID for those. |
