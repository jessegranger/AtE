---
name: poe-memory
description: Inspect live Path of Exile process memory through AtE's MemoryServerPlugin (loopback HTTP) to reverse engineer component/struct layouts and find offsets. Use when asked to find, confirm or expand offsets in Offsets.cs, investigate what bytes of a component mean, or locate timers/counters/pointers in game memory.
---

# poe-memory

AtE's `MemoryServerPlugin` (`AtE.Core/Plugins/ReverseEngineering/MemoryServerPlugin.cs`) serves a **read-only** HTTP API on `127.0.0.1:7788`
from inside the overlay process, which already holds the game's process handle. Use it with `curl`. It never writes game memory.

## Setup

- The plugin is off by default. It is enabled by `[MemoryServerPlugin] Enabled=True` in `Settings.ini` (next to the exe, `AtE.Windows/bin/Debug/net9.0-windows/`), or the Enabled checkbox in the overlay console.
- Check it: `curl -s http://127.0.0.1:7788/status` (prints pid, module base, `ingame`, `player` addresses). `/help` lists endpoints.
- Plugin code changes need the overlay restarted, and the exe locks its DLLs. If *you* launched the overlay, `taskkill //F //IM AssistantToTheExile.exe`, `dotnet build AtE.Windows/AtE.Windows.csproj`, relaunch from the output dir, wait ~15 s. If the *user* launched it, ask them to close it first. Restarting the overlay does not affect the game.
- The game process can stay paused through restarts; entity ids stay valid while it is paused.

## Address expressions (the `at=` parameter)

A root followed by steps. Roots: `0xHEX`, `base`, `gameroot`, `ingame`, `player`, `ent(ID)`, `comp(ID,ComponentName)`.
Steps: `+N`, `-N` (decimal or 0x), `*` (dereference an 8-byte pointer).
Examples: `ingame+0x92C`, `comp(12950,DiesAfterTime)+0x20*`, `0x5D8F465D8B0+0x740*` (array head of an ArrayHandle).
In a URL write `+` as `%2B` (a bare `+` becomes a space, which is also accepted and treated as `+`).

## Endpoints

| Endpoint | Purpose |
|---|---|
| `/status` | attach state, base/size, key addresses, entity count, overlay clock |
| `/ents?filter=<path substring>&comp=<Name>&id=<id>&limit=30` | find entities; with `comp` shows that component's address; with `id` lists all component addresses |
| `/read?at=EXPR&len=0x80&depth=0` | annotated hexdump, one row per 8 bytes: hex, i32 pair, f32 pair, tag (module+RVA, vtable, `ptr->"ascii"`, `ptr->u"utf16"`, i64). `depth=N` also dumps pointees. Pipe through `awk '!/00 00 00 00 00 00 00 00/'` to hide zero rows |
| `/track?at=EXPR&len=0x40&ms=50&dur=10000` | sample one region over time; reports only slots that change, as i32/i64/f32/f64 with slope/s, r2, % flat intervals, unit hints (`~ms`, `per-sec`...). Monotonic/linear first |
| `/record?at=A&at=B&len=0x40&len=0x100&ms=100&dur=30000&name=label` | same sampling, written to `memdumps/<name>-<time>.jsonl` beside the exe (header line with meta, then `{"t":ms,"hex":[...]}`). For long runs / offline analysis in Python |
| `/info?id=ID&skill=<owner skill name>` | ground truth for an entity: buffs (Timer/MaxTime), animation controller `TimeSpentAnimating`, duration-ish stats, owner skill `MinionDuration` (skill name lookup is unreliable; `animate_weapon` was not found) |
| `/scan?id=ID&value=1.75,3000&tol=0.01&kind=f32\|i32\|both&depth=1&len=0x400` | find values across every component of an entity (plus the animated object's components) and pointees. Reports label paths like `Comp+0x20*+0x38` |
| `/hunt?filter=AnimatedWeapon&dur=85.6&rate=30&tol=45&player=1` | for each matching entity predict deadline/spawn/remaining/elapsed values (ticks, ms, s) from SimulationTicks + `TimeSpentAnimating` + known duration and print TSV hits (`ent T elapsed label type value candidate delta`). Very noisy at wide `tol` (small ints and entity ids match by chance); aggregate offline and trust only low-variance deltas. `player=1` also scans the player's graph (heavy; the overlay crashed once during such a run, cause not established) |
| `/dump?filter=AnimatedWeapon&depth=1&len=0x400&limit=40` | raw hex of every matching entity's component graph, header `#ENT id T game_ms elapsed`. ~5 MB for 15 entities and it stalls the overlay main thread for several seconds, so save to a file with `-o` and analyze offline. Use `corr.py` (below) on it |
| `/gsnap?id=ID&name=a&depth=1&len=0x400` | snapshot all components + pointees (+ animated object) under a label |
| `/gdiff?name=a[&name2=b][&top=1][&match=Comp]` | re-read now (or vs snapshot b) and list changed 4-byte slots. **Use `top=1`** (component bodies only) first: pointee regions are full of heap-reuse noise |

Long calls (`/track`, `/record`) block for `dur`; use `timeout` on the Bash call to match.

## Method

Reverse engineering here is differential. Form a hypothesis, run a controlled experiment, look at what changed.

1. **Establish ground truth** with `/info` / `/scan` before hunting: a value you can compute independently (elapsed animation time, skill stat, game clock).
2. **Find things that move:** `/track` over the region, or `/gsnap` → change one thing (the user unpauses N seconds, refreshes, casts) → `/gdiff top=1`. A baseline diff while the game stays paused tells you what is noise.
3. **Classify slots** from the shape: constant = pointer/flags/type; counts at 1.0/s (or 1000/s) = elapsed/remaining time; constant but jumps when the thing is refreshed = deadline or start stamp (the interesting kind).
4. **Confirm by arithmetic**: candidate must satisfy a relation you can predict (e.g. `deadline - now ≈ MinionDuration`, delta == real elapsed). Two independent confirmations before writing it down.
5. **Distinguish per-entity from shared:** compare several entities of the same type. A pointer that is identical across entities (e.g. DiesAfterTime `+0x20`) points at shared static data, not per-entity state.
6. **Record**: write confirmed offsets into `AtE.Core/PoE/Offsets.cs` with a name, the game version (`Offsets.PoEVersion`), and a comment on the evidence. Mark unconfirmed ones as unknown rather than guessing.

## Offline correlation across entities (`corr.py`)

When many entities of one type exist, a per-entity stamp can be found without knowing units: `curl -s .../dump -o d.txt; python .claude/skills/poe-memory/corr.py d.txt real 0.95 40`.
It fits every 4/8-byte slot (i32/f32/i64/f64) against each entity's creation time (`game_ms - elapsed*1000`) and prints slots with |Pearson r| above the threshold. Entity ids correlate ~0.95 with creation order, so r in the 0.95 range on `Entity+0x88` or an Actor/Life pointee chain is noise; look for r>0.99 and confirm that the value is constant per entity across two dumps. Many labels are the same memory reached through pointer cycles; deduplicate mentally.

## Cautions

- **Paused game.** The user can pause so state does not move (and so a minion does not expire during analysis). Simulation time stops, but the real-time clock below keeps running.
- **Int vs long.** `/track` analyzes both 4- and 8-byte slots and unwraps overflow. A real int64 shows as an i64 slot *and* its low i32; the high dword of a ms counter shows as an i64 with slope ~1000*2^32/s. A float slot also shows a meaningless i32 slope (bit noise); trust the typed view that is linear in sensible units.
- **Entities are main-thread objects.** The plugin does lookups on the overlay tick; requests wait up to 5 s for a frame. If you get "timed out waiting for the main thread", the overlay is stalled or not attached.
- **Entity ids change.** A refreshed minion may be a *new* entity (Animate Weapon is: id 12950 -> 13046). Re-find it via `/ents?filter=` or the owner's DeployedObjects array. A freed entity's memory gets reused, so diffs against an old snapshot of a dead entity are garbage.
- **Read-only, local only.** The server rejects requests with an `Origin` header or non-loopback `Host`. Do not add write endpoints without the user asking.

## Known facts (PoE 3.29.3, confirmed live)

- `InGameState+0x92C`: int32 millisecond counter, 1000/s, real-time (does **not** stop when the game is paused). `+0x934`: same clock as float seconds. `+0x930`: float last-frame duration (~0.017). `+0x928`: int32, constant 0. Named in `Offsets.InGameState`: `GameTimeMs`, `GameTimeSec`, `FrameDeltaSec`.
- Player `Actor+0x740`: ArrayHandle (head, tail) of `DeployedObjectsArrayEntry` (12-byte entries: EntityId, SkillId, unknown int). Entry `i` is at `head + 12*i`.
- `DiesAfterTime` component is only 0x28 bytes: vtable, owner entity (+0x08), second vtable (+0x18), pointer (+0x20) to a *shared* object (same address across entities; contains small records, an effect-related texture reference). No per-entity timer was found in it, nor anywhere else in the minion's components, for Animate Weapon (checked across ~5 simulated seconds and across a refresh).
- `ClientAnimationController+0x1A4` `TimeSpentAnimating` is simulation time (stops when paused).

- `InGameState_Data.SimulationTicks` int32 at `ingame+0x218*+0xB18` (found by another session). Counts up in irregular steps, measured 24.7-27.7/s (not a clean 30/s), and **resets on area change**. Do not assume a fixed tick rate when converting to seconds.
- Animated object's `AnimatedRender+0x1E8` is a per-entity constant float = the animated object's creation time in seconds on the animation clock (`TimeSpentAnimating == animClockNow - this`; verified over 27 AnimatedWeapon observations; slope 1.0/s vs creation real time; unchanged between dumps). It is the animated object's start stamp, so it encodes the same information as `TimeSpentAnimating` and does not fix refresh desync by itself.
- No creation tick/deadline for an AnimatedWeapon was found in the entity's own components (depth 1), the animated object, or the owner's DeployedObjects entry (checked with `/hunt` over 16 entities and `corr.py` over 15).

## Open questions

- Where (if anywhere) a client-visible duration/deadline for minions lives. Next experiment: a minion type that refreshes *in place* (same entity id), diffed before/after the refresh.
- A simulation-time clock (stops on pause) to compare deadlines against; `InGameState+0x92C` is real time.
