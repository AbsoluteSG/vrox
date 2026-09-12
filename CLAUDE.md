# Working notes for Claude

Read this before changing anything. Every rule here comes from a real bug in this
project, named so it is checkable rather than a platitude. Toolchain traps are in
`LESSONS.md`; this file is about how to work.

## 1. One layer at a time, played before the next

The previous build accumulated prediction, reconciliation, interpolation, interest
management, weapons and a level pipeline **before anyone pressed Play once**. When
it finally felt wrong, six untested layers were candidates and it had to be
scrapped.

Add one thing. Have the user play it. Then add the next.

Never say a feature "works" when what was verified is that it compiles.

## 2. A schema change is three edits, not one

The single most repeated bug here, in two flavours.

**A table nobody subscribed to** is not an error — it is permanently empty, and
code reading it concludes the rows do not exist. Projectiles never drew (`shot`);
weapons could never be equipped (`weapon_def`).

**A column the client does not know about** is worse. The generated bindings still
describe the old layout, so rows decode with every field after the new one
shifted — a string lands where a timestamp should be and the SDK throws
`ArgumentOutOfRangeException` from `DateTime.AddTicks`, nowhere near the cause.

When changing the schema, do all three:
1. The table, column or reducer in `server/module/`.
2. **Regenerate bindings** — `publish-local.sh` now does this automatically, so
   prefer it over a bare `spacetime publish`.
3. A line in `VroxNet.Queries` if the client reads it.

Regenerating turns drift into a compile error, which is the whole point: after
the bindings updated, every stale `conn.Reducers.*` call failed to build
immediately instead of corrupting rows at runtime.

## 3. Never let the client decide what the server knows

Two bugs from this, both of which deadlocked permanently:

- `_equipped` cached the id we last *asked* to equip. The request failed, the
  cache remembered success, and it never retried.
- `WeaponDef.Find(id) is null` was read as "no such weapon". It also means "the
  client has not been told yet", and the two are indistinguishable client-side.

Reconcile against replicated server state, not a local record of intent. When the
server can answer authoritatively, ask it and handle the failure — do not
pre-empt it with a guess.

## 4. A fallback that hides a failure is worse than a crash

The server fired a default weapon when nothing was equipped. So "my weapon did not
equip" and "my weapon fires one bullet" looked identical, and the real failure was
invisible for three rounds of debugging.

Prefer doing nothing loudly. Unarmed now fires nothing, which makes a bullet proof
that equipping worked.

Same class: `loadtest.sh` printed a perfectly healthy tick while every bot had died
on startup. Always assert the thing you are measuring is actually present.

## 5. Verify with the instrument that matches the claim

- Compiles ≠ runs. The synthetic check cannot see asmdef boundaries; only Unity
  can. It passed while `Vrox.Editor` was missing an assembly reference.
- A bot ≠ the Unity client. Bots hold a steady send rate and never showed the
  input-budget ratchet that made real movement stick.
- A short run ≠ a long one. The token bucket decayed over minutes; a 20-second
  test showed zero declines and I called it fixed.

State exactly what was checked and what was not. "Verified against a bot, not by
playing" is an honest sentence.

## 6. Prefer server-side evidence over theory

The good debugging in this project all looked the same: read the actual log, query
the actual row, call the reducer directly and print the result. `InputBudgetUs`
sitting at 31,000 ended an argument that reasoning had not.

Do that *before* proposing a cause.

## 7. When the user says it is still broken, look for a second cause

Twice a real fix landed and the symptom stayed, because two bugs were stacked. Do
not re-explain the first fix. Assume something else is also wrong and go find it.

## 8. Comments must match the code

`VroxShots` claimed it used the server's clock while using the local one. A comment
that lies is worse than none — it stops the next reader from checking.

If a limitation is deliberate, say so in the comment and name what breaks.

## 9. Names collide

`Grid` with `UnityEngine.Grid`. A `Vrox.Player` namespace with the generated
`Player` table type. Both failed confusingly. Check generated bindings and
UnityEngine before naming anything general.

## 10. Deletion is permanent here

There is no git in this repo. Confirm scope before deleting anything, and say what
will be kept and why.
