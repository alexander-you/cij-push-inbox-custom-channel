#!/usr/bin/env bash
# Post-import step for the AlexPushInboxChannel solution (see docs/design-choices.md).
# Grants CIJ Read (Organization) on alex_pushchannelinstance so it can read the sender config during submission.
#   1. Required:    "Marketing Services User Extensible Role". It's customizable and held by the CIJ application user. https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-define-channel-instance-account
#   2. Best effort: "Cxp Channel Definitions Services User" (https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-define-custom-channel-instance). Skipped with a warning when locked (0x8004023b).
# Safe to run more than once.
# Usage: deploy/post-import.sh https://<org>.crm.dynamics.com   (requires: az login, curl, python3)
set -euo pipefail
ORG="${1:?Usage: $0 https://<org>.crm.dynamics.com}"
ORG="${ORG%/}"
API="$ORG/api/data/v9.2"
TOKEN="$(az account get-access-token --resource "$ORG" --query accessToken -o tsv)"
get() { curl -sf -H "Authorization: Bearer $TOKEN" -H "Accept: application/json" "$API/$1"; }
first() { python3 -c "import json,sys; v=json.load(sys.stdin)['value']; print(v[0]['$1'] if v else '')"; }
role_id() { get "roles?\$select=roleid&\$filter=name%20eq%20'${1// /%20}'%20and%20_parentroleid_value%20eq%20null" | first roleid; }

PRIV_ID="$(get "privileges?\$select=privilegeid&\$filter=name%20eq%20'prvReadalex_pushchannelinstance'" | first privilegeid)"
BU_ID="$(get "businessunits?\$select=businessunitid&\$filter=_parentbusinessunitid_value%20eq%20null" | first businessunitid)"
[[ -n "$PRIV_ID" ]] || { echo "Privilege prvReadalex_pushchannelinstance not found. Import the solution first." >&2; exit 1; }

# grant <role name> -> prints the resulting depth; returns 2 when the role is locked
grant() {
  local name="$1" rid resp code depth
  rid="$(role_id "$name")"
  [[ -n "$rid" ]] || { echo "Role '$name' not found. Is Customer Insights - Journeys installed?" >&2; return 1; }
  resp="$(curl -s -w '\n%{http_code}' -X POST "$API/roles($rid)/Microsoft.Dynamics.CRM.AddPrivilegesRole" \
    -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
    -d "{\"Privileges\":[{\"Depth\":\"Global\",\"PrivilegeId\":\"$PRIV_ID\",\"BusinessUnitId\":\"$BU_ID\"}]}")"
  code="${resp##*$'\n'}"
  if [[ "$code" != 2* ]]; then
    echo "$name: AddPrivilegesRole failed (HTTP $code): ${resp%$'\n'*}" >&2
    [[ "$resp" == *0x8004023b* ]] && return 2 || return 1
  fi
  depth="$(get "RetrieveRolePrivilegesRole(RoleId=$rid)" | python3 -c "import json,sys; print(next((p['Depth'] for p in json.load(sys.stdin)['RolePrivileges'] if p['PrivilegeId']=='$PRIV_ID'),'missing'))")"
  echo "$name: prvReadalex_pushchannelinstance = $depth"
  [[ "$depth" == "Global" ]]
}

grant "Marketing Services User Extensible Role"
grant "Cxp Channel Definitions Services User" || {
  rc=$?
  if [[ $rc -eq 2 ]]; then
    echo "WARNING: 'Cxp Channel Definitions Services User' is locked by Microsoft in this environment. Skipped." >&2
    echo "         CIJ still gets access through 'Marketing Services User Extensible Role'. See docs/design-choices.md." >&2
  else
    exit $rc
  fi
}
