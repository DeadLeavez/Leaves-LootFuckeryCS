# Leaves-LootFuckery — Handoff

Last updated: 2026-08-04. Read `4.0 Mods\HANDOFF.md` first for project-wide rules.

---

## 1. What the mod does

Granular per-map, per-category and per-item **loose-loot probability adjustment** — multiply
how often keycards, barter items, etc. spawn on each map. Also a diagnostic mode that generates
many simulated raids and dumps the resulting loot frequency table.

Single class, `src/LootFuckery.cs` (~490 lines).

## 2. Lifecycle

`[Injectable(TypePriority = OnLoadOrder.PostDBModLoader + 1)]`, `IOnLoad` only. Two independent
features, each config-gated: `editProbabilities` (the real feature) and `dataDump` (diagnostic).
See LEWDS handoff §2 — LEWDS deliberately loads *after* this mod so its floor-raise wins.

## 3. Probability editing (`editProbabilities`)

For each map in `loot.jsonc`'s `mapSpecific`, registers a transformer on `location.LooseLoot`
(the SPT-4.0 `AddTransformer` mechanism — direct writes to `LazyLoad.Value` are discarded; this
is the same approach SPT's own SeasonalEventService uses). The transformer:
- Scales `SpawnpointCount.Mean` by the map's `totalLootMultiplier` (overall density).
- For each spawnpoint, `AdjustPoint` multiplies each root item's `RelativeProbability` (its
  weight in the point's item distribution) by that item's resolved multiplier, and scales the
  point's own `Probability` by the **average** of its items' multipliers.

**Multiplier resolution priority** (`GetProbability`), most specific wins:
map-specific item → global item → map-specific category → global category. No match → 0
(logged only if `harmlessErrorLogging`).

**The 0.99 cap matters:** a point at Probability ≥ 1 becomes force-spawned and stops counting
toward the map's max-loot-spawns budget, so it's clamped to 0.99. Don't remove this.

## 4. Data dump (`dataDump`, diagnostic)

Simulates raids to measure actual loot frequency. `GenerateRuns` calls
`locationLifecycleService.GenerateLocationAndLoot(fakeSessionId, map)` `timesToGenerate` times
and tallies item occurrences. A **fake MongoId session** is safe: that service only uses the
session id to look up in-progress raid adjustments, which returns empty for an unknown id, and
loot generation itself doesn't touch the session.

Output (to `output/`, via LeavesUtils' timestamped-write helper) can be sorted, categorized,
name-localized, and compact-formatted per config. `RunAllMaps` loops every map; otherwise
`mapToGenerate`.

Historical note: this dump used to also write `<map>_probability.json` files into the mod
folder (`SaveSpawnpointProbabilities`) — those were the stale files found in the live LeavesUtil
folder earlier. Purely diagnostic; safe to delete, nothing reads them back.

## 5. Real-run capture (`captureRealRuns`) + web UI

Two features added 2026-08-04. Both config-gated, both independent of the dump/probability code.

**Real-run capture** records EVERY real raid's generated loot to disk, full fidelity, kept forever —
the data source for the web-UI run list and a future per-raid loot MAP. Mechanism: a Harmony postfix
(SPT's own `AbstractPatch`, NOT raw HarmonyLib) on
`LocationLifecycleService.GenerateLocationAndLoot(sessionId, name, generateLoot)` → `LocationBase`. That
one method is the shared choke point for BOTH vanilla SPT and **Fika** (Fika changes the client endpoint
and prefix-patches `StartLocalRaid`, but its override still calls `GenerateLocationAndLoot` itself, once
per raid, caching the layout for all coop players). See auto-memory `reference_fika_loot_generation_hook`.

- `GenerateLocationAndLootPatch` enables in `LootFuckery.OnLoad` (only when `captureRealRuns`). Static
  patch → reaches the `RealRunCapture` singleton via `ServiceLocator.ServiceProvider.GetService<>()`
  (`[Obsolete]` but current supported way; pragma-suppressed).
- **Sim vs real:** the `dataDump` sim ALSO calls the patched method 250×. Two guards keep only real
  raids: (1) `RealRunCapture.SuppressCapture` is set around `GenerateMap`'s `GenerateRuns` call; (2) the
  session must resolve to a real PMC (`GetPmcProfile`) — the sim uses a random fake `new MongoId()`.
- **Storage:** full generated `.Loot` (positions + item trees, not summarised) → minified JSON at
  `output/history/<map>/<yyyyMMdd_HHmmss>_<player>.json`, plus one line appended to
  `output/history/index.jsonl` (`{map,file,capturedUtc,player,session,lootPoints,itemCount}`) that the
  web list reads without opening the big run files. `SPTarkov.Reflection` (4.0.11) added as a package ref
  for `AbstractPatch` (not copied to output, like the other SPT packages).

**Raid-END extraction capture** (added 2026-08-04) records what each player carried OUT, per raid. Second
patch `EndLocalRaidPatch` (postfix on `LocationLifecycleService.EndLocalRaid`, route
`/client/match/local/end`; fires per-player on extract, coop included). `RealRunCapture.RecordExtraction`
intersects the player's submitted post-raid inventory item ids against each recent raid's spawn ids
(`recentRuns`, an in-memory `runFile→HashSet<id>` cache filled in `WriteRun`, capped at 30). The run with
the largest intersection IS their raid — **self-linking**, because a raid's spawn ids are unique to that
one generation. This is the only reliable link: session and `ServerId` don't bridge start→end (the
headless hosts/generates the loot and never calls EndLocalRaid; `ServerId` = `"{loc}.{side} {timestamp}"`
is generated per-call, not shared across coop players). EMPIRICALLY VERIFIED: spawn ids survive Fika
unchanged (29 exact matches on a real Fika raid) — see auto-memory `reference_fika_loot_generation_hook`.
- **Output:** one line per extract appended to `output/history/extractions.jsonl`
  (`{runFile,map,player,session,exitStatus,extractedUtc,count,broughtOut:[{id,tpl}]}`). Run files stay
  immutable; extractions accumulate append-only.
- **Deliberately partial:** players who brought nothing captured out (died, empty-handed, or a raid from
  before this server start) produce no match and are **skipped** — by design (user call). So the eventual
  UI label should be "looting/survived players", not "all players". Scope so far = capture only; NOT yet
  surfaced in the web UI.

**Forensic gear snapshots** (added 2026-08-05, flag `captureGearSnapshots`, independent of `captureRealRuns`)
dump each player's **entire profile inventory tree** — equipment + every container's contents, raw and
untouched — at raid start and end, to catch items that vanish. Built to chase a disappearing key. Unlike
the loot capture, this needs no id-intersection: each real player's own start and end share their
`sessionId`, which is the pairing key. Three snapshots per raid:
- **entry** — third patch `StartLocalRaidPatch` (postfix on `LocationLifecycleService.StartLocalRaid`,
  route `/client/match/local/start`) → `RecordEntry`, reads the server's stored PMC via `GetPmcProfile`
  (menu item-moving has already synced the loadout by then).
- **exit-submitted** — the client's submitted post-raid inventory (`request.Results.Profile.Inventory.Items`).
- **exit-server** — the server's PMC *after* SPT processed the raid (the `EndLocalRaid` postfix runs after
  the original). Both exit snapshots come from `RecordExit`, also called from `EndLocalRaidPatch`.

Diff entry→exit-submitted for an in-raid loss, exit-submitted→exit-server for a server-processing loss.
**EMPIRICALLY CONFIRMED** (headless Fika raid, 2026-08-05): the real player's client hits
`/client/match/local/start`; the headless uses `/fika/raid/headless/start` + `/fika/raid/create` and never
does, so `StartLocalRaid` only fires for real players (headless carries no gear — exactly right). All coop
here is headless, so every real player is a joiner and provably fires the entry hook. `EndLocalRaidPatch`
is now enabled when EITHER flag is on; `StartLocalRaidPatch` only when `captureGearSnapshots` is on.
- **Output:** `output/history/gear/<player>/<ts>_<phase>_<session8>.json` (full raw `Item[]`, minified) +
  one line per snapshot in `output/history/gear/index.jsonl`
  (`{phase,file,capturedUtc,player,session,exitStatus,itemCount}`).

**Web UI** — `LootWebApi` (`IHttpListener`, prefix `/lootfuckery/`, mirrors `QuestrandomizerRerollApi`),
two separate pages served from `assets/ui/` (read fresh per request; `assets\**` build glob added):
- `/lootfuckery/ui/dump` — the large-scale dump viewer (`dump.html`, adapted from `output/lootviewer.html`
  with a server-dump picker + the Leaves-WebUI rail tag). `dump.html` is the canonical served copy now;
  `output/lootviewer.html` remains as the standalone file:// version.
  - **Barter tiers (soft dep on Leaves-BarterEconomy):** on load, `dump.html` fetches
    `/bartereconomy/api/tiers` (id→tier). If BarterEconomy isn't installed the fetch fails and the tier
    UI is simply omitted. When present it adds per-item **Tier** + **Tier val** (avg×tier) columns, three
    top-line stats (loose/container/total tier value), and two sort modes (tier, tier value). Sort is
    driven by both the dropdown and clickable column headers (they stay in sync via `#sort`).
- `/lootfuckery/ui/history` — real-run list (`history.html`, sortable/filterable; each row links to the
  raw run). Rich per-run rendering is DEFERRED to the map-view work (this pass = capture + list only).
- API: `/api/dumps` (list `loot_*.json`), `/api/dump?file=`, `/api/runs` (index.jsonl newest-first),
  `/api/run?file=<map>/<name>` (traversal-guarded to `output/history/`).
- Opted into the shared rail: added a `lootfuckery` row to `Leaves-WebUI/assets/ui/rail.txt`.

**Map assets (DynamicMaps, for the planned loot-overlay view)** — `assets/maps/<Map>/` holds a `.jsonc`
(bounds/rotation/layers) plus `Layers/*.svg`, sourced from the-hideout DynamicMaps (Shebuka /
acidphantasm). Served by `LootWebApi`:
- `/lootfuckery/api/maps` → `[{ id, config }]`, one entry per folder that contains a `.jsonc`
  (`config` is the jsonc's URL under `/lootfuckery/maps/`). The client reads that jsonc — which carries
  `DisplayName`/`Author`/`AuthorLink` and names its layer SVGs — so attribution data comes straight
  from the served files, no server-side jsonc parsing needed.
- `/lootfuckery/maps/<relative>` → one asset off `assets/maps`, with **two guards**: path-traversal
  (must resolve inside `assets/maps`) and an **extension allowlist** (`.svg`/`.jsonc`/`.json` only).
- **Licence handling (CC BY-NC-SA 4.0):** each map ships its `Shebuka-LICENSE*.md` /
  `acidphantasm-LICENSE.md` + the root `map_and_data_credits.txt` **on disk** (distribution keeps the
  notices, "-MODIFIED BY" filenames flag changes = the SA/attribution obligation). The extension
  allowlist means `.md`/`.txt` always 404, so those files ship but are never browsable. SVGs are
  streamed **verbatim** (no modification → ShareAlike safe). **TODO for the UI step:** the map viewer
  must surface attribution to the user (author name + `AuthorLink` from the jsonc + a link to the
  CC BY-NC-SA 4.0 licence); that's the "reasonable manner" attribution CC BY requires once maps are
  shown. Mod stays non-commercial (NC).

## 5b. Icebreaker (modded map) — staged, currently inert

Manimal's **Icebreaker** map mod reuses the vanilla **Suburbs** location slot (log:
`Suburbs slot rebound to Icebreaker`), so its `Base.Id` is `Suburbs`. A neutral `"suburbs"`
entry is staged in `loot.jsonc` (lowercase key — matched against `Base.Id.ToLower()`).

It does **nothing yet**: `getLocationNames()` iterates `GetLocations().GetDictionary()` and only
keeps locations whose `LooseLoot != null`, and Icebreaker runs **loot isolation (suspend/restore)** —
its loose loot is only live during an Icebreaker raid, so at our `OnLoad` the slot is skipped
(confirmed 2026-08-06: the map never appears in the LEWDS "below floor" / LootFuckery "Adjusting"
startup lists). Same reason LEWDS doesn't floor it. Nothing errors — the map is simply not touched.

To actually adjust its loot later we'd need to reach it *past* Icebreaker's isolation (understand its
suspend/restore, likely a per-raid `GenerateLocationAndLoot` swap — a startup `AddTransformer` on the
suspended LazyLoad won't survive the swap). Deferred until requested.

## 5c. Location-data cache (`cacheLocationData`, perf)

Fixes a big slowdown vs the old TS server: `location.LooseLoot` / `StaticContainers` / `StaticLoot` are
`LazyLoad<T>` (`SPTarkov.Server.Core.Utils.Json.LazyLoad`), whose `.Value` getter **re-parses the file
off disk on EVERY access** - there is NO cache despite the docstring claiming one. `GenerateLocationLoot`
reads LooseLoot once, StaticContainers **three times** and StaticLoot once per generation, so one raid is
five disk-parses; the data dump multiplies that by `timesToGenerate`. Streets' `looseLoot.json` is ~97 MB.

`LocationDataCache.Install` (called from the first `OnUpdate`, so every mod's transformers are already
registered) **swaps** each of those LazyLoads for a caching one: parse+transform the original ONCE, keep a
"master", hand out a fresh copy per access. A swap - not a Harmony patch on `LazyLoad<T>.Value` - because
that getter is a shared-generic method and patching a closed-generic getter silently no-ops (tried it, did
nothing).

- **Copies are required:** loot generation is DESTRUCTIVE (overwrites each chosen point's item pool with
  the rolled item, `LocationLootGenerator.cs:844` etc.), so the master can never be handed out directly.
- **Loose loot uses a STRUCTURAL copy, not a deep clone.** A FastCloner deep clone of the 97 MB graph is
  ~2.1 s (millions of tiny objects + string-interning setters), which was the entire per-run cost. Since
  generation only reassigns `LooseLoot.Spawnpoints`/`.SpawnpointsForced` and each chosen point's
  `Template.Items`/`Template.Root`, `CloneLooseLootForGeneration` duplicates ONLY those wrapper objects
  (`record ... with { }`) and shares the read-only bulk (`ItemDistribution`, the `Items`/`SptLootItem`
  objects, `SpawnpointCount`, positions) by reference. Result on Streets: steady-state generation dropped
  from ~2284 ms to ~260 ms/run (~9x); the file parse is now paid once per server lifetime, not per access.
  Static containers/loot are small and read-only during generation, so they keep a plain deep clone.
- **Tradeoff / risk:** the parsed loot stays resident in RAM (like the TS server did). The structural copy
  is built against the mutations visible in `GenerateDynamicLoot`/`GetForcedDynamicLoot` - if a future SPT
  version mutates loose loot elsewhere, shared data could corrupt across runs (first suspect if a raid ever
  yields empty/garbled loot). `GenerateRuns` logs a `first: … one-time parse, steady-state avg: …` line.

## 6. Files

| File | Role |
|---|---|
| `src/LootFuckery.cs` | Probability editing + data dump. Enables the capture patch, guards the sim loop. Installs the location-data cache on first OnUpdate. |
| `src/LocationDataCache.cs` | Swaps location LazyLoads for caching ones (parse-once); structural copy for loose loot. See §5c. |
| `src/RealRunCapture.cs` | Singleton: gates real vs sim, writes run files + index.jsonl; matches extracts → extractions.jsonl. |
| `src/GenerateLocationAndLootPatch.cs` | `AbstractPatch` postfix on `GenerateLocationAndLoot` (raid start) → `RealRunCapture`. |
| `src/StartLocalRaidPatch.cs` | `AbstractPatch` postfix on `StartLocalRaid` (raid entry) → `RealRunCapture.RecordEntry` (gear snapshot). |
| `src/EndLocalRaidPatch.cs` | `AbstractPatch` postfix on `EndLocalRaid` (raid end) → `RecordExtraction` (loot) + `RecordExit` (gear). |
| `src/LootWebApi.cs` | `IHttpListener` serving the two pages + JSON API under `/lootfuckery/`. |
| `assets/ui/dump.html` | Served dump viewer (server-dump picker + rail). |
| `assets/ui/history.html` | Served real-run list. |
| `src/Config.cs` | Feature toggles + dump options (`captureRealRuns`, `timesToGenerate`, `categorize`, ...). |
| `config/config.jsonc` | The toggles. |
| `config/loot.jsonc` | The actual per-map/global item & category multipliers (the meat). |
| `config/loot - noadjust.jsonc` | A neutral (all-1.0) reference copy to diff against / reset from. |
| `output/` | Dump results; `output/history/` = captured real runs (runtime-written, survives rebuilds). |
