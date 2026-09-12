#!/usr/bin/env bash
# Follows the module's log output.
#
#   ./scripts/logs.sh          # follow
#   ./scripts/logs.sh 200      # last 200 lines, then exit
#
# The standalone server runs detached, so there is no terminal to look at — its
# stdout goes nowhere you can read. This is the only view of Log.Info from
# inside reducers, which is where kill reports and fight breakdowns are written.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/env.sh"

if [ -n "${1:-}" ]; then
  spacetime logs --server local "$VROX_DB" -n "$1"
else
  spacetime logs --server local "$VROX_DB" -f
fi
