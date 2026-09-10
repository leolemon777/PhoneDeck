#!/usr/bin/env bash
# PhoneDeck 统一开发构建便捷入口

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
"$SCRIPT_DIR/scripts/build.sh" "$@"
