#!/bin/sh
# Writes the PWA runtime config (API address) from the provisioned outputs.
set -e
cat > ./src/web/config.js <<JS
window.PUSH_INBOX_CONFIG = { apiBaseUrl: "${API_BASE_URL}" };
JS
echo "PWA config written: apiBaseUrl=${API_BASE_URL}"
