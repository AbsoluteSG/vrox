#!/usr/bin/env bash
# Compile-checks the Unity client without opening the editor.
#
# Unity refuses batchmode while the editor has the project open, which makes it
# impossible to verify a change while someone is working in the editor. This
# compiles the same sources against Unity's own managed reference assemblies and
# the SpacetimeDB SDK from NuGet, so an error is caught in seconds rather than
# after an editor round-trip.
#
# It is a syntax and API check, not a substitute for running the game: it cannot
# see asmdef boundaries, scene wiring, or anything Unity does at import time.
set -euo pipefail

UNITY_VERSION="${UNITY_VERSION:-6000.5.3f1}"
SDK_VERSION="${SDK_VERSION:-2.10.0}"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
MANAGED="/Applications/Unity/Hub/Editor/$UNITY_VERSION/Unity.app/Contents/Resources/Scripting/Managed/UnityEngine"

if [ ! -d "$MANAGED" ]; then
  echo "no Unity $UNITY_VERSION managed assemblies at $MANAGED" >&2
  exit 1
fi

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

# The Input System cannot be built from source outside Unity — it needs unsafe
# code and access to engine internals — so the check reuses a compiled copy.
# Normally that is this project's own; if Unity has not built it yet, any other
# local project on the same editor version has an identical one.
# Package assemblies come from whichever ScriptAssemblies folder has them. The
# packages themselves ship source that only Unity can compile — unsafe code and
# engine internals — so there is nothing to build here, only to borrow.
SCRIPT_ASM="$ROOT/unity/Library/ScriptAssemblies"
NEEDED="Unity.InputSystem UnityEngine.UI Unity.TextMeshPro"

missing() {
  for a in $NEEDED; do
    [ -f "$1/$a.dll" ] || return 0
  done
  return 1
}

if missing "$SCRIPT_ASM"; then
  for CANDIDATE in $(find "$HOME/Unity Projects" -maxdepth 3 -type d -name ScriptAssemblies 2>/dev/null); do
    if ! missing "$CANDIDATE"; then
      SCRIPT_ASM="$CANDIDATE"
      echo "note: borrowing package assemblies from $SCRIPT_ASM" >&2
      echo "      (this project has not been compiled by Unity yet)" >&2
      break
    fi
  done
fi
if missing "$SCRIPT_ASM"; then
  echo "note: some package assemblies are missing; open the Unity project once." >&2
fi

python3 - "$MANAGED" "$ROOT" "$SDK_VERSION" "$WORK" "$SCRIPT_ASM" <<'PY'
import glob, pathlib, sys
managed, root, sdk, work, script_asm = sys.argv[1:6]
# Only the package assemblies this project references, not the whole folder —
# a borrowed folder holds another project's own code too.
dlls = sorted(glob.glob(f"{managed}/*.dll"))
for name in ("Unity.InputSystem", "UnityEngine.UI", "Unity.TextMeshPro"):
    dlls += glob.glob(f"{script_asm}/{name}.dll")
refs = "\n".join(
    f'    <Reference Include="{pathlib.Path(d).stem}"><HintPath>{d}</HintPath><Private>false</Private></Reference>'
    for d in dlls)
pathlib.Path(f"{work}/check.csproj").write_text(f'''<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>netstandard2.1</TargetFramework>
    <Nullable>enable</Nullable>
    <LangVersion>9.0</LangVersion>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
    <AssemblySearchPaths>{{HintPathFromItem}};$(AssemblySearchPaths)</AssemblySearchPaths>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="SpacetimeDB.ClientSDK" Version="{sdk}" />
  </ItemGroup>
  <ItemGroup>
{refs}
  </ItemGroup>
  <ItemGroup>
    <Compile Include="{root}/unity/Assets/Vrox/**/*.cs" />
  </ItemGroup>
</Project>
''')
PY

cd "$WORK"
dotnet build -v quiet --nologo 2>&1 | grep -E 'error|warning CS|Build succeeded' || true
