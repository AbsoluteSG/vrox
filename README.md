# Vrox

A player that moves and shoots, on a server. Small on purpose.

```
server/module/     SpacetimeDB module — players, shots, one cleanup timer
unity/Assets/Vrox/ the client — connection, movement, camera, shooting
scripts/           build, publish, generate bindings
```

| Input | Action |
|---|---|
| `WASD` | Move, relative to the current view |
| Mouse | Aim |
| Left click | Fire |
| `I` | Toggle auto-fire (on by default) |
| `Q` / `E` | Rotate the view |

## Running it

```bash
spacetime start
```
```bash
./scripts/publish-local.sh
```

Open `unity/` in Unity 6000.5.3f1, run **Vrox → Create Scene**, press Play.

## How it works

The server is authoritative and there is no client prediction. The client sends a
direction 20 times a second; the server decides where that puts you; the client
draws wherever the server says it is. **What you see is the truth** — if movement
feels wrong, something is actually wrong, rather than two simulations disagreeing.

### The camera

Parented to the player, so following costs no code and cannot drift. `VroxCamera`
only *rotates* it. Rotation is client-only — the server never hears about it —
which is what makes it safe to spin freely.

Every direction the player expresses goes through `ScreenToWorld` before it is
sent: WASD, and the vector to the cursor. Skip that and the controls stop
matching what is on screen the moment the view is turned.

### Shooting

A shot's row is written **once and never updated**. Position is a function of how
long it has been alive — `origin + dir * speed * t` — evaluated independently by
every client, so a bullet costs one insert and one delete however far it flies.
Moving projectiles by updating rows every tick is what makes this genre expensive
to network.

Rate of fire is enforced server-side, so calling the reducer faster buys nothing.
The client never invents a projectile of its own.

**Patterns** are three independent knobs, which is why a handful of kinds cover
most of the genre — rotate a shot, move where it starts, or shift where it is in
its wave:

| Pattern | What it does |
|---|---|
| `SingleShot` | One projectile. |
| `SpreadShot` | Fanned across an arc, centred on the aim. |
| `RingShot` | Evenly around a circle. |
| `ParallelShot` | Side by side, same direction, offset perpendicular to travel. |
| `HelixShot` | Same direction, wave phases spread evenly so the strands braid. |

Two weapon-level modifiers compose with any of them:

- **Spin** rotates the whole volley over time. `Ring + spin` is the classic
  spiral. Derived from the spawn timestamp rather than a shot counter, so it needs
  no per-player state and stays right across a disconnect.
- **Wave** makes each projectile weave. A helix is not a special path — it is
  several waving shots whose phases differ, so **a helix with zero wave amplitude
  draws every strand on the same straight line.** The weapon inspector warns about
  that combination.

Waving is still analytic: the row is written once, and the path is simply no
longer straight.

One timer, running once a second, deletes expired shots. It is the only scheduled
thing in the project.

**Known limit:** only the client evaluates the wave — the server stores its
parameters but never computes a projectile's position, because nothing can be hit
yet. When collision arrives, the server must use the same formula or the two will
disagree about where a bullet is.

**Known limit:** shot age is measured against the server's timestamp but compared
to the *local* clock. That is only correct while both are on the same machine.
A real server needs clock-offset estimation, and that is the next layer, not this
one.

### Three decisions worth keeping when this grows

- **The step is fixed, not time-scaled.** One input moves you `speed * 0.05`
  regardless of how long the frame took. Prediction only becomes possible if the
  same input always produces the same displacement; a step scaled by measured
  elapsed time can never be reproduced on the client.
- **The direction is normalised server-side.** Otherwise the length of the vector
  is a speed multiplier any client can set.
- **The send rate is the movement speed.** One input equals one server step, so
  sending twice as often would move twice as fast. Anything that rate-limits
  input is therefore also a speed limit, and getting it wrong looks exactly like
  lag.

## What to add next, one at a time

1. Other players rendered from the same table (it is already subscribed).
2. A camera that follows rather than being parented.
3. Client prediction, then reconciliation — and only with a way to *measure*
   disagreement, because that is what rubber-banding is.

Add one, play it, keep it or throw it away. The previous build added six layers
before anything was played and became impossible to diagnose.

See `LESSONS.md` for toolchain traps, and `CLAUDE.md` for the working rules this
project has earned the hard way — both are written for whoever picks this up next,
including me.
