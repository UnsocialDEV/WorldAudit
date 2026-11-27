# WorldAudit Release Notes

## 1.1.1 - 2026-11-27

WorldAudit 1.1.1 is a stable server-side release for Vintage Story 1.22.7 (.NET 10). It includes the following changes since 1.1.0.

### Inspect, lookup and attribution

- Inspect shows storage visits with separate totals for items added and removed. Use `/wa i details <entryId> <page>` to see the original transfers, timestamps and slots. Older history groups consecutive transfers by the same player at the same storage across gaps of up to five seconds. Intervening actors and block changes end the group.
- Inspect, lookup and job details go to the requesting moderator’s WorldAudit chat tab. Inspect cycles through three entries per page, with the newest at the bottom. Holding an inspect click no longer advances multiple pages. Command help, errors, player input and item names are escaped for Vintage Story chat markup.
- Deferred inventory callbacks retain the initiating player on shared storage. Nested block operations can inherit a verified actor. Read-only inspection avoids unnecessary block-entity snapshots.
- Lookup and rollback share code, action, time, radius and dimension filters. Storage searches can match a moved item, storage label (including facing variants) or inventory type. Block action filters restrict mixed searches to blocks. Invalid and empty user filters produce an error.

### Rollback, restore and undo

- Previews flush pending audit writes and freeze every eligible match. `RollbackPreviewLimit` now sets the database write batch size. Job details retain the selection center, radius and UTC time range, count chunks for storage-only jobs, and show blocks, grouped storage visits and individual transactions on separate pages. Apply processes the full selection across all pages.
- Mixed jobs link a broken container to its inventory teardown. A filled trunk and its contents are recovered once, while independent withdrawals remain eligible. Apply verifies restored block entities and affected slots after game callbacks, rejects incomplete recorded stacks, reports conflicts, and checks planned entries for missing or wrong-world sources.
- Fire timer ticks no longer flood the writer, inspect pages or rollback jobs. Existing tick rows are filtered from history queries. House-fire rollbacks restore burned materials and skip flame placement and burnout. Rollback, restore and undo rebuild ground-storage piles extinguished and bombs unlit, preserving the pile’s contents and orientation and the bomb variant.
- Area rollbacks skip changes made by earlier WorldAudit jobs by default. Use `/wa undo <jobId>` for one job or select `u:#rollback` or `u:#restore` explicitly. Inspect and lookup retain the job history.

### Dedicated servers, performance and maintenance

- Dedicated servers can initialize capture when optional client libraries such as `csogg` and `OpenTK.Graphics` are missing. Startup logs identify unavailable types, and required hook failures remain visible.
- Status, lookup, nearby searches, rollback previews and purge commands run database work off the game thread. History queries filter and page IDs before loading snapshots. Purge previews interrupt scans after ten seconds and limit database lock waits to five seconds per command. Maintenance protects history needed by pending jobs.
- The writer bounds queued events and payload bytes, including in-flight batches. Concurrent flushes share pending work. Status reports the installed version, backlog, rejected events and writer faults. Previews warn when rejected events may have left gaps.
- The administrator guide now covers installation, telemetry settings, staff privileges, storage inspection, rollback examples, job review and maintenance. Regression coverage includes large mixed histories, delayed writes and callbacks, private moderator chat, and hazard recovery across a real save reload.

### Upgrade from 1.1.0

Stop the server and back up the world, audit database and `ModConfig/worldaudit.json` together. Replace the old mod in the server’s `Mods/` folder with the attached **WorldAudit-vintagestory.zip**, restart, and run `/wa status` to confirm version **1.1.1**. The ZIP includes native SQLite for Windows x64, Linux x64/ARM64 and macOS x64.

The database upgrade preserves recorded transactions and adds operation links and storage visit grouping. For a filled-container break recorded before operation linking, use `a:break` or `-b` to select the block repair. Existing jobs retain their selections.

### Release verification

205 automated tests and 24 disposable-server checks passed on Windows with Vintage Story 1.22.7, including reopening the same save. The attached ZIP is the package used for those server checks.

SHA-256 for `WorldAudit-vintagestory.zip` is `e7dc4e734f0518d15b1f005637f4fe4bf6b4bd603b8e3148b567387fdf326a68`.

[Full change comparison from 1.1.0](https://github.com/UnsocialDEV/WorldAudit/compare/Release-1.1.0...Release-1.1.1) · [Administrator guide](https://github.com/UnsocialDEV/WorldAudit/blob/Release-1.1.1/README.md)

## 1.1.0 - 2026-11-03

WorldAudit 1.1.0 improves player attribution, container history, and rollback safety for Vintage Story 1.22.7.

### Requirements and installation

- **Vintage Story 1.22.7**, server side. This release targets **.NET 10** and uses the Harmony library supplied with that game version. Other game versions are not verified.
- Download **WorldAudit-vintagestory.zip** from this release's assets. Stop the server, back up the world and audit database, replace the previous WorldAudit ZIP in the server's `Mods` directory, then restart. Keep only one version installed.
- Native SQLite libraries are bundled for Windows x64, Linux x64/ARM64, and macOS x64. Live server validation was performed on Windows x64; the other platforms were not exercised live.

### Player attribution and history

- Capture the acting player's name and UID around real server block, item, block-entity, and inventory operations. This fixes missing attribution for tree felling, multiblock doors, chiseling, block exchanges, and bulk changes.
- Attribute shared-chest transfers to the player moving the items, rather than the last player who viewed the inventory. Track containers before direct use and correctly rebind them after replacement, unload, or reload.
- Record paired-door state changes, including neighboring door links and toggles.
- Use explicit game contexts for fire, explosions, gravity, liquids, decay, growth, and world generation. Retain initiating player UIDs where the game provides them; unsupported or unknown provenance remains `#unknown`.
- Preserve dimension coordinates and distinguish solid and fluid block layers. Identify worlds by their stable save identifier instead of their display name.

### Rollback, restore, and undo

- Check the expected current block, layer, and block-entity state before applying a change. Refuse conflicting newer edits, missing snapshots, unloaded chunks, wrong-world jobs, and unsafe fluid displacement.
- Replay solid cells without gameplay removal callbacks deleting unplanned multiblock parts. Door undo no longer removes a neighboring part outside the job or creates collateral `#unknown` history.
- Restore native block-entity snapshots, including the 1.22.7 chisel snow-array serialization asymmetry and server collision boxes for rotated doors.
- Roll back only the changed container slots, preserving other players' contents and normal food aging. Record item replacements as removal and addition, and suppress duplicate ordinary events during replay.
- Build undo from the actual before/after changes. Persist those audit records and job results together; retry a failed database write without applying the world mutation twice.
- Pause interrupted jobs on restart. Inspect `/wa job <id>`, resume with `/wa apply <id>`, or discard remaining work with `/wa cancel <id>`. Canceling previews releases their maintenance protection; reload is blocked while rollback jobs are active.

### Upgrade notes

- Schema 8 retains existing history but cancels unfinished legacy plans whose save/layer ownership cannot be verified. Legacy block entries with an unknown layer cannot be safely replayed.
- Existing history stored under world names is retained separately and is not automatically reassociated with save identifiers. Previously missing snapshots and historical `#unknown` identities cannot be recovered.
- A hard crash between changing the world and committing its audit result cannot be atomic across the game save and SQLite. Restart pauses unfinished jobs, and expected-state checks reject conflicting replays.

### Validation

- **142 unit tests passed.**
- **8 scenarios passed on a fresh disposable Vintage Story 1.22.7 server**, with no server errors: ordinary attribution; tree logs and foliage; single doors; paired doors; rotated-door collision boxes; chisel conversion and voxel edits; shared-chest inventory packets; and protection of newer player changes. Applicable scenarios include rollback and undo checks.
- The driver uses real server players, game handlers, inventories, world state, and persisted SQLite history. Client UI/authentication, multiplayer timing races, and third-party mod combinations were not tested. Mods that bypass these hooks through raw chunk writes or custom protocols may still require integration.
- [Reproduce the server tests](https://github.com/UnsocialDEV/WorldAudit/blob/1acdd1580a375a2755a585b1c02ec5b24f875498/tests/WorldAudit.ServerTests/README.md).

Source commit: [`1acdd15`](https://github.com/UnsocialDEV/WorldAudit/commit/1acdd1580a375a2755a585b1c02ec5b24f875498).

The attached ZIP is the exact package tested on the disposable server. SHA-256:

```text
b35c5d13001ba9cb14c17bad0bf0a46a4cd52e1ed6ae991d1c32b07d8b1dff07
```

## 1.0.0 - 2026-03-17

Initial public release of WorldAudit for Vintage Story.

### Highlights

- Server-side audit logging for block changes and container transactions.
- SQLite-backed persistence with WAL mode for durable, local history storage.
- In-game admin tooling for inspect, lookup, nearby search, rollback, restore, undo, purge, reload, and runtime status.
- Preview-based rollback and restore workflows so admins can review jobs before applying world changes.
- Incremental rollback execution over future ticks to avoid forcing large corrections through a single frame.
- Synthetic re-auditing of rollback and restore actions so moderator actions stay visible in the audit trail.

### Included Features

- Block mutation auditing with actor, cause, action, timestamps, positions, before/after block codes, and optional block entity snapshots.
- Container transaction auditing with before/after inventory snapshots and per-slot line deltas.
- Filtered history lookup with support for time, radius, user, cause, action, include codes, and exclude codes.
- Inspect mode for quick in-world review of block and container history.
- Rollback job previews, restore job previews, and undo generation for completed jobs.
- Maintenance operations for retention, purge preview/confirm, WAL checkpointing, SQLite optimize, queue monitoring, and consumer pause/resume.
- Reloadable runtime configuration through `worldaudit.json`.

### Commands

- `/wa inspect`
- `/wa lookup [filters]`
- `/wa near [filters]`
- `/wa rollback [filters]`
- `/wa restore [filters]`
- `/wa undo <jobId>`
- `/wa apply <jobId>`
- `/wa jobs`
- `/wa job <jobId>`
- `/wa status`
- `/wa reload`
- `/wa consumer <pause|resume>`
- `/wa purge t:<age>`
- `/wa purge confirm t:<age>`

### Packaging And Compatibility

- Server-side mod.
- Targets `.NET 8`.
- Ships with packaged native SQLite runtime assets for `win-x64`, `linux-x64`, `linux-arm64`, and `osx-x64`.
- Distributable package output: `WorldAudit-vintagestory.zip`.

### Notes For Server Admins

- Start the server once after installation to generate `worldaudit.json`.
- Review retention, rollback rate, and default lookup settings before production use.
- Grant command access only to trusted admins or moderators.

