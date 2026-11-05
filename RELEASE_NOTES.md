# WorldAudit Release Notes

## 1.1.1-pre.3 (unreleased)

- Retain loadable game types when optional dependencies cause `Assembly.GetTypes()` to throw `ReflectionTypeLoadException`. Log the assembly, retained/unavailable type counts and loader errors.
- Inspect methods only on block, block-entity and block-accessor types. Required hook failures continue to abort registration instead of silently disabling capture.
- Fix the startup failure with missing `csogg` and `OpenTK.Graphics`, which left commands available while capture never initialized.
- Run status and purge-preview database reads on a worker, returning replies on the game thread. Reject overlapping status/preview requests.
- Interrupt purge-preview scans after 10 seconds and limit lock waits to 5 seconds per command. Preview remains read-only; cancellation removes its native callback before returning the connection to the pool.
- Report a failed writer as `faulted` with its error instead of `running`.
- Confirmed purge and other synchronous database commands remain outside this fix. Validate maintenance against a copy of production data before using them on a busy server.

- Reject unrecognised filters and empty user filters instead of silently widening an admin selection.
- Verify restored native block-entity state and container slots after game callbacks; report unsuccessful verification and retain the existing rollback-on-error path.
- Display and retain the preview centre, radius and resolved UTC time range. Count chunks for container-only jobs, and show the installed version in `/wa status`.
- Add regressions for ignored filters, silently discarded inventory restoration, callbacks changing restored stacks, and a filled native trunk's contents and item attributes through break/rollback/undo.

Validated with 155 unit tests and 11 isolated native server scenarios on Linux/Vintage Story 1.22.7, with `csogg.dll` and `OpenTK.Graphics.dll` absent. Production installation and third-party mod compatibility remain unverified. This build cannot recreate events or inventory snapshots absent from historical data. Preserve the original world and audit database before installing it.

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

