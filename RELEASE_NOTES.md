# WorldAudit Release Notes

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

