#!/usr/bin/env bash
# Connects a Dataverse environment that has the AlexPushInboxChannel solution to the Azure backend deployed with `azd up`.
#   1. Sets the value of environment variable alex_ChannelApiBaseUrl to the Function App's /api address.
#      https://learn.microsoft.com/power-apps/maker/data-platform/environmentvariables
#   2. Creates (if missing) a Dataverse application user for the Function App's managed identity and gives it the
#      Basic User role, so the dispatcher can call the alex_PushDeliveryReport custom API.
#      https://learn.microsoft.com/power-platform/admin/manage-application-users
#   3. Optional: writes the function key to alex_channelapikey on one channel instance (test environments only).
# Safe to run more than once. Run it from the repository root after `azd up`.
# Usage: deploy/connect-azure.sh https://<org>.crm.dynamics.com [<alex_pushchannelinstance id>]
#   (requires: az login, azd, curl, python3)
set -euo pipefail
ORG="${1:?Usage: $0 https://<org>.crm.dynamics.com [channel-instance-id]}"
ORG="${ORG%/}"
INSTANCE_ID="${2:-}"
API="$ORG/api/data/v9.2"

RG="$(azd env get-value AZURE_RESOURCE_GROUP)"
FUNC="$(azd env get-value FUNCTION_APP_NAME)"
BASE_URL="$(azd env get-value API_BASE_URL)"
PRINCIPAL_ID="$(azd env get-value FUNCTION_PRINCIPAL_ID)"
APP_ID="$(az ad sp show --id "$PRINCIPAL_ID" --query appId -o tsv)"

TOKEN="$(az account get-access-token --resource "$ORG" --query accessToken -o tsv)"
H=(-H "Authorization: Bearer $TOKEN" -H "Accept: application/json" -H "Content-Type: application/json")
get() { curl -sf "${H[@]}" "$API/$1"; }
first() { python3 -c "import json,sys; v=json.load(sys.stdin)['value']; print(v[0]['$1'] if v else '')"; }
send() { local code; code="$(curl -s -o /dev/null -w '%{http_code}' -X "$1" "${H[@]}" "$API/$2" -d "$3")"; [[ "$code" == 2* ]] || { echo "$1 $2 failed (HTTP $code)" >&2; exit 1; }; }

# 1. Environment variable value (environment-specific, so it is not part of the solution).
DEF_ID="$(get "environmentvariabledefinitions?\$select=environmentvariabledefinitionid&\$filter=schemaname%20eq%20'alex_ChannelApiBaseUrl'" | first environmentvariabledefinitionid)"
[[ -n "$DEF_ID" ]] || { echo "alex_ChannelApiBaseUrl not found. Import the solution first." >&2; exit 1; }
VAL_ID="$(get "environmentvariablevalues?\$select=environmentvariablevalueid&\$filter=_environmentvariabledefinitionid_value%20eq%20$DEF_ID" | first environmentvariablevalueid)"
if [[ -n "$VAL_ID" ]]; then
  send PATCH "environmentvariablevalues($VAL_ID)" "{\"value\":\"$BASE_URL\"}"
else
  send POST environmentvariablevalues "{\"schemaname\":\"alex_ChannelApiBaseUrl\",\"value\":\"$BASE_URL\",\"EnvironmentVariableDefinitionId@odata.bind\":\"/environmentvariabledefinitions($DEF_ID)\"}"
fi
echo "alex_ChannelApiBaseUrl = $BASE_URL"

# 2. Application user for the managed identity.
BU_ID="$(get "businessunits?\$select=businessunitid&\$filter=_parentbusinessunitid_value%20eq%20null" | first businessunitid)"
USER_ID="$(get "systemusers?\$select=systemuserid&\$filter=applicationid%20eq%20$APP_ID" | first systemuserid)"
if [[ -z "$USER_ID" ]]; then
  send POST systemusers "{\"applicationid\":\"$APP_ID\",\"businessunitid@odata.bind\":\"/businessunits($BU_ID)\"}"
  USER_ID="$(get "systemusers?\$select=systemuserid&\$filter=applicationid%20eq%20$APP_ID" | first systemuserid)"
fi
ROLE_ID="$(get "roles?\$select=roleid&\$filter=name%20eq%20'Basic%20User'%20and%20_businessunitid_value%20eq%20$BU_ID" | first roleid)"
send POST "systemusers($USER_ID)/systemuserroles_association/\$ref" "{\"@odata.id\":\"$API/roles($ROLE_ID)\"}"
echo "Application user $USER_ID (managed identity app $APP_ID) has Basic User"

# 3. Function key on a channel instance (test environments only; see docs/security.md).
if [[ -n "$INSTANCE_ID" ]]; then
  KEY="$(az functionapp keys list -g "$RG" -n "$FUNC" --query functionKeys.default -o tsv)"
  send PATCH "alex_pushchannelinstances($INSTANCE_ID)" "{\"alex_channelapikey\":\"$KEY\"}"
  echo "Function key written to channel instance $INSTANCE_ID"
fi
