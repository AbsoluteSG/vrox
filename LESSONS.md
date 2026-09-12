# Traps worth not rediscovering

Everything else from the previous build is gone. These five cost real time and
are properties of the toolchain, not of the design that was scrapped.

1. **`tools/wasm-opt-shim` is required.** The SpacetimeDB CLI invokes
   `wasm-opt -all`, and `-all` enables every post-MVP WebAssembly proposal;
   Binaryen then emits a module the host cannot parse ("invalid leading byte for
   external kind"). The shim swaps `-all` for a conservative feature set.
   `scripts/env.sh` puts it first on `PATH`.

2. **`DOTNET_ROOT` must be set for built executables.** The apphost resolves the
   runtime through it, not `PATH`. Without it a console app dies instantly with
   "You must install .NET".

3. **Unity defaults nullable reference types off.** A `csc.rsp` holding
   `-nullable:enable` next to the `.asmdef` turns it on. `.rsp` files take
   compiler arguments only — a `#` comment in one is parsed as a filename.

4. **Watch out for name collisions with Unity and with generated bindings.**
   `Grid` collides with `UnityEngine.Grid`; a `Vrox.Player` namespace collides
   with the generated `Player` table type. Both fail in confusing ways.

5. **Unity refuses batchmode while the editor has the project open**, and it does
   not run `Awake` on components added outside play mode.
