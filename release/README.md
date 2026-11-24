# Solution releases

Ready-to-import packages of the Dataverse solution **Customer Insights Journeys - Push Inbox Channel** (`AlexPushInboxChannel`). Each version folder contains both package types, exported from the same source.

| File | Type | Use it for |
|---|---|---|
| `AlexPushInboxChannel_<version>_managed.zip` | Managed | **Test, UAT and production.** Components can't be edited directly, and the solution can be removed cleanly by deleting it. |
| `AlexPushInboxChannel_<version>.zip` | Unmanaged | **Development only**: an environment where you'll extend or customize the solution and later export your own managed package. |

Learn: [Solution concepts](https://learn.microsoft.com/power-platform/alm/solution-concepts-alm), [Import solutions](https://learn.microsoft.com/power-apps/maker/data-platform/import-update-export-solutions).

**Rules**
- Don't import the managed package into an environment that already has this solution as unmanaged. Power Platform doesn't support importing a managed solution into the environment that contains the originating unmanaged solution ([Solution concepts](https://learn.microsoft.com/power-platform/alm/solution-concepts-alm)).
- You can also build the unmanaged package yourself from `solution/AlexPushInboxChannel/` ([installation step 1](../docs/installation.md#1-get-the-solution-package)).

## 1.1.0.0

| File | SHA-256 |
|---|---|
| `AlexPushInboxChannel_1_1_0_0_managed.zip` | `2446cff834c78e983e682bd1c0e4b6d1b2d14f883b74ffd534a0bb4f95d52a3e` |
| `AlexPushInboxChannel_1_1_0_0.zip` | `1978767544a1e91147341c0671cb2777da549dc56479a9b5502d92f9fa305734` |

**Contents**
- The *Push + Inbox* channel definition with 7 message parts.
- The channel instance configuration table (`alex_pushchannelinstance`) with the *Push sender* form.
- The `alex_PushOutbound` and `alex_PushDeliveryReport` custom APIs and their plug-in.
- The `alex_ChannelApiBaseUrl` environment variable, as a definition with no value.
- The message log, an elastic table (`alex_pushmessagelog`), with its forms and views.
- The **Push Inbox Log Writer** and **Push Inbox Log Reader** roles.
- English (1033) and Hebrew (1037) labels throughout.

**What's not in the packages:** channel instances, app keys, Azure addresses, keys or secrets, and environment variable values. You create these per environment ([implementer guide](../docs/implementer-guide.md)).

**Changes since 1.0.0.0**
- Added the message log and its two roles.
- Added Hebrew names for the log's forms and views.
- Removed `contact` from the solution's components. The log's contact lookup is shipped with the log table, so the solution no longer carries the Contact entity or its ribbon.

**Validation**
- **Content scan: passed.** No environment URLs, keys or customer data. The environment variable ships without a value.
- **Solution checker: passed with one finding.** `pac solution check` on the managed package reported 0 critical, 0 high, 1 medium and 0 low ([Solution checker](https://learn.microsoft.com/power-apps/maker/data-platform/use-powerapps-checker)).
  - The medium finding is `web-use-strict-mode` in `alex_/scripts/pushchannelinstance.js`, a form script with no functional impact.
- **Unverified:** importing the managed package into a clean environment. Test it in a non-production environment first.
