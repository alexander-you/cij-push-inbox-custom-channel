#!/usr/bin/env bash
# Points Git at the versioned hooks in .githooks (run once per clone).
set -euo pipefail
cd "$(git rev-parse --show-toplevel)"
chmod +x .githooks/*
git config core.hooksPath .githooks
echo "Git hooks enabled: $(git config core.hooksPath)"
