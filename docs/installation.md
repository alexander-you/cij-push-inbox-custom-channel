# Installation

## Prerequisites

- **Customer Insights - Journeys** installed in the target environment. The `msdyn_channeldefinition` and `msdyn_channelinstance` tables must exist. ([Custom channels in CIJ](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-extend-outreach-custom-channels))
- **System Administrator** role in the target environment.
- **Tools:**
  - [Power Platform CLI](https://learn.microsoft.com/power-platform/developer/cli/introduction) (`pac`)
  - [Azure CLI](https://learn.microsoft.com/cli/azure/install-azure-cli) (`az login`; used for the post-import step)
  - Python 3
- **Hebrew (optional):** enable it in the environment to use the Hebrew labels. ([Enable languages](https://learn.microsoft.com/power-platform/admin/enable-languages))

## 1. Build the solution package

The unpacked solution in `solution/AlexPushInboxChannel/` is the source of truth. ([SolutionPackager](https://learn.microsoft.com/power-platform/alm/solution-packager-tool))

```bash
python3 tests/solution/check_solution.py
pac solution pack --zipfile out/AlexPushInboxChannel.zip --folder solution/AlexPushInboxChannel --packagetype Unmanaged
```

For production, import the unmanaged package into a development environment, then export it as managed with `pac solution export --managed` and deploy that package. ([Solution concepts](https://learn.microsoft.com/power-platform/alm/solution-concepts-alm))

## 2. Import

```bash
pac solution import --environment https://<org>.crm.dynamics.com --path out/AlexPushInboxChannel.zip --async
```

## 3. Grant CIJ read access to the channel configuration

```bash
deploy/post-import.sh https://<org>.crm.dynamics.com
```

The script grants Read (Organization) on `alex_pushchannelinstance` to **Marketing Services User Extensible Role**, as Microsoft documents. ([Channel instance account and Extensible role](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-define-channel-instance-account)) ([AddPrivilegesRole](https://learn.microsoft.com/power-apps/developer/data-platform/webapi/reference/addprivilegesrole))

- **Locked Cxp role:** it also tries **Cxp Channel Definitions Services User** ([Custom channel instance](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-define-custom-channel-instance)). Where that role is locked (`0x8004023b`), it prints a warning and continues.
- **Idempotent:** the script is safe to run more than once. Rerun it after Customer Insights - Journeys upgrades.

## 4. Deploy the Azure backend

The Azure backend is not published yet; see the roadmap in the [README](../README.md). It will be deployed with the Azure Developer CLI (`azd up`). ([azd templates](https://learn.microsoft.com/azure/developer/azure-developer-cli/azd-templates), [azd for Azure Functions](https://learn.microsoft.com/azure/developer/azure-developer-cli/function-app-deployments)) Until then, sends return `NotSent` with the reason `ChannelApiNotConfigured`.

## 5. Verify

1. **Solution check:** `python3 tests/solution/check_solution.py` prints `OK`.
2. **Relationship:** `GET EntityDefinitions(LogicalName='alex_pushchannelinstance')/OneToManyRelationships?$filter=ReferencingAttribute eq 'msdyn_extendedentityid'` returns one relationship with `CascadeDelete` = `RemoveLink`. ([Custom channel instance](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-define-custom-channel-instance))
3. **Channel definition:** `GET msdyn_channeldefinitions(b0d2cb5f-fc8a-4f71-88fd-3f494aee6df2)` returns `msdyn_outboundendpointurltemplate` = `/alex_PushOutbound`, and its `msdyn_channelmessageparts` returns 7 rows.
4. **CIJ settings:** **Push + Inbox** appears among the custom channels, and adding a channel instance opens the *Push sender* form. ([Custom channel instance](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-define-custom-channel-instance))
5. **Outbound API:** `POST alex_PushOutbound` with `{"payload":"{…}"}` returns `NotSent` / `ChannelApiNotConfigured` while the environment variable is empty.
6. **Hebrew labels:** `python3 tools/l10n/translations.py verify https://<org>.crm.dynamics.com` prints `problems: 0`. A user with Hebrew as UI language sees Hebrew, right-to-left forms and views. ([Personal options (language)](https://learn.microsoft.com/power-apps/user/set-personal-options))

## Uninstall

Delete the solution. The relationship uses `RemoveLink`, so existing CIJ channel instances lose their link to the config row but aren't deleted. Remove those channel instances in CIJ first. ([Custom channel instance](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-define-custom-channel-instance))
