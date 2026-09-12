# Shared environment for the Vrox scripts. Source, don't execute.
#
# Puts tools/wasm-opt-shim ahead of the real wasm-opt so the SpacetimeDB CLI's
# `-all` feature flag is neutralised. See tools/wasm-opt-shim/wasm-opt for why.
VROX_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
export PATH="$VROX_ROOT/tools/wasm-opt-shim:$HOME/.dotnet:$HOME/.local/bin:$PATH"

# The apphost a `dotnet build` produces resolves the runtime through DOTNET_ROOT,
# not PATH. Without this a built executable dies instantly with
# "You must install .NET".
if [ -z "${DOTNET_ROOT:-}" ]; then
  for candidate in "$HOME/.dotnet" /usr/local/share/dotnet /opt/homebrew/opt/dotnet/libexec; do
    [ -x "$candidate/dotnet" ] && export DOTNET_ROOT="$candidate" && break
  done
fi

export VROX_MODULE="$VROX_ROOT/server/module"
export VROX_UNITY="$VROX_ROOT/unity"
export VROX_BINDINGS="$VROX_UNITY/Assets/Vrox/Generated"
export VROX_DB="${VROX_DB:-vrox}"
