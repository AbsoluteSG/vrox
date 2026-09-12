#!/usr/bin/env bash
# Regenerates the Unity client's typed module bindings.
# Run after every schema change. The output is generated code -- never edit it.
#
# The Unity client and the load-test bot both compile the output, so this is the
# only place bindings come from.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/env.sh"

spacetime generate --lang csharp \
  --out-dir "$VROX_BINDINGS" \
  --module-path "$VROX_MODULE"
