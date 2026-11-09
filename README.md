# SmartDrones — Planet Crafter drone logistics overhaul

A BepInEx mod that replaces The Planet Crafter's drone dispatch logic with a
priority-aware, distance-aware scheduler. It keeps the stock behavior you
expect while fixing the places the vanilla logistics quietly fall apart.

## What it does

Vanilla drone logistics has a handful of frustrating failure modes:

- **Task starvation.** New logistics tasks effectively jump the queue, so a
  task that's been waiting the longest can sit unserved indefinitely while
  fresher work keeps getting picked up ahead of it.
- **Drones crossing the map for nothing.** A drone will fly across the planet
  to fetch an item that was sitting in a container right next to the
  destination the whole time.
- **One idle planet stalls the whole fleet.** When a planet has nothing to do,
  the dispatch pass can break in a way that starves drones on *other* planets
  that still have work.
- **No notion of priority at pickup.** Nothing lets an urgent request be served
  before a routine one — it's all down to iteration order.

SmartDrones rewrites the dispatch pass to address all of these while staying
faithful to how the stock game moves items.

## Features

1. **Priority + age task scoring.** The default `PriorityThenAge` mode scores
   each pending task by its request priority and how long it has been waiting,
   with an anti-starvation guarantee: a task's effective score climbs as it
   ages, so nothing can be held back forever. Both halves are tunable via
   `PriorityWeight` and `AgeWeight`.
2. **Nearest-supply binding.** When a task can be fulfilled from more than one
   source container, the drone draws from the *closest* one instead of whatever
   happened to come first in iteration.
3. **Nearest-task assignment.** Idle drones are matched to work by distance,
   with selectable assignment modes: `FIFO`, `NearestFirst`,
   `FifoWithNearestTieBreak`, and `PriorityThenAge`.
4. **Cross-planet stall fix.** An idle or empty planet no longer breaks the
   dispatch loop — every planet's drones keep getting serviced independently.
5. **Dual-role containers.** A container can act as both a supply and a demand
   for the same item, enabling buffer/relay chains (pull an item into a staging
   container, then push it onward) — with an anti-ping-pong guard so an item
   can't bounce endlessly between two containers that both want it.

## Safety

This mod replaces the full dispatch pass, but it does so conservatively:

- The replacement is a faithful **1:1 port of the stock dispatch semantics**,
  with the fixes above layered on top — it is not a from-scratch reinvention of
  how items move.
- Every dispatch pass runs inside a guard that **falls back to the stock
  coroutine on any error**, and **auto-disables the replacement after 3
  consecutive failures** so a bad edge case can never brick your logistics.
- **Everything is config-toggleable** — each feature can be turned off
  independently to return to stock behavior for that piece.
- It touches no save data and patches nothing permanently. **Remove the mod and
  the game is exactly as it was.**

## Install

1. Install **BepInEx 5.4.23+ (x64)** into your Planet Crafter game directory
   (unzip it so `BepInEx/`, `winhttp.dll`, and `doorstop_config.ini` sit next to
   the game executable). Run the game once to let BepInEx generate its folders.
2. **Linux / Steam Deck / Proton only:** in Steam, set the game's launch option
   to:

   ```
   WINEDLLOVERRIDES="winhttp=n,b" %command%
   ```

   (BepInEx bootstraps through `winhttp` under Proton; without this override it
   won't load.)
3. Drop **`SmartDrones.dll`** into `BepInEx/plugins/`.
4. Launch the game. On first run the mod writes its config to
   `BepInEx/config/`.

## Build from source

Requires the **.NET SDK 8 or newer**.

1. Set `GameDir` (and optionally `BepInExDir`) in `SmartDrones.csproj` to point
   at your install — see the comment block in that file. You can also pass them
   on the command line.
2. Build:

   ```
   dotnet build -c Release
   ```

   or with inline paths:

   ```
   dotnet build -c Release "-p:GameDir=/path/to/The Planet Crafter"
   ```

3. The built `SmartDrones.dll` lands in `bin/Release/`. Copy it into
   `BepInEx/plugins/`.

## Config reference

All keys live in `BepInEx/config/` and default to:

| Key | Default |
| --- | --- |
| `Patches.FullDispatchReplacement` | `true` |
| `Patches.StarvationFixNearestTask` | `true` |
| `Patches.AllowDualRoleGroups` | `true` |
| `Dispatch.AssignmentMode` | `PriorityThenAge` |
| `Dispatch.PriorityWeight` | `1000` |
| `Dispatch.AgeWeight` | `1` |
| `Dispatch.NearestTieBreakAgeWindow` | `50` |
| `Logging.VerboseAssignmentLog` | `false` |

`AssignmentMode` accepts `FIFO`, `NearestFirst`, `FifoWithNearestTieBreak`, or
`PriorityThenAge`. Setting `Patches.FullDispatchReplacement=false` returns the
whole system to stock behavior.

## Compatibility

Built against the version of The Planet Crafter current as of November 2026.
Single-player; the multiplayer host path is untested.

## Vanilla bugs this works around

Decompilation analysis surfaced three genuine bugs in the stock logistics that
this mod routes around:

- **Task starvation via dictionary-order pickup.** Stock pickup iterates tasks
  in dictionary/insertion order rather than by age, so a long-waiting task can
  be perpetually skipped in favor of newer ones.
- **Cross-planet dispatch break.** An idle/empty planet can break out of the
  dispatch loop early, starving drones on other planets that still have work.
- **Silent cargo destruction on incomplete deposit.** On an incomplete deposit,
  stock logic can discard the carried items rather than returning them —
  quietly destroying cargo.

## Credits

Developed by the repository owner. The decompilation analysis of the stock
dispatch code and the mod implementation were done in collaboration with
Claude (Anthropic).
