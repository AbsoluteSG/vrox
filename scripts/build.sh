#!/usr/bin/env bash
# Builds the server module.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/env.sh"
spacetime build --module-path "$VROX_MODULE"
