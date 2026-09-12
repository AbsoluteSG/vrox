#!/usr/bin/env bash
# Publishes to the local standalone server (start it with `spacetime start`).
#
#   ./scripts/publish-local.sh            # normal publish
#   ./scripts/publish-local.sh --fresh    # wipe the database first
#
# Schema changes (adding a column, reordering fields) need a migration.
# During the slice we simply reset the world rather than write migrations, but
# that is destructive, so it is opt-in rather than the default.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/env.sh"

# macOS ships bash 3.2, where expanding an empty array under `set -u` is an
# error, so the flag is passed as a plain string rather than an array.
FRESH=""
[ "${1:-}" = "--fresh" ] && FRESH="--delete-data"

spacetime publish --module-path "$VROX_MODULE" --server local "$VROX_DB" --yes $FRESH

# Bindings are regenerated on every publish, not by hand.
#
# A schema the client does not know about is not a compile error — the generated
# code still describes the old layout, so rows decode with the fields shifted and
# a string lands where a timestamp should be. That surfaces as an unrelated crash
# deep in the SDK, which is a miserable way to learn you forgot a command.
#
# Regenerating here makes the two impossible to separate: a schema change either
# breaks the build immediately, or it works.
"$(dirname "${BASH_SOURCE[0]}")/gen-bindings.sh"
