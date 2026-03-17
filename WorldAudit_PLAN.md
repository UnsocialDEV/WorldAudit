# WorldAudit Plan

## 1. Product Goal

WorldAudit should be a server-side Vintage Story audit and rollback mod modeled closely after CoreProtect, but designed around Vintage Story's API surface and performance constraints.

WorldAudit must:

- Log every meaningful block change with enough information to answer:
  - what block was there before
  - what block replaced it
  - when it happened
  - where it happened
  - who caused it
  - whether the cause was a player or a natural/system source such as fire, explosion, gravity, decay, rollback, restore, or unknown
- Log container transactions with enough information to answer:
  - who opened or modified the container
  - what items were taken or inserted
  - how many items changed
  - when the change happened
  - whether the transaction can be safely rolled back
- Support CoreProtect-like in-game commands for inspect, lookup, near, rollback, restore, purge, reload, and status.
- Use SQLite only.
- Keep write logging and read queries fast enough that normal gameplay does not lag the server.
- Run rollback planning off-thread and apply rollback work in small main-thread batches.
- Follow a modular architecture that can survive future features without turning into one large mod system file.


## 2. Core Design Principles

- Main-thread work must be tiny and predictable.
- SQLite must be used as an append-heavy audit store, not as an object graph database.
- No ORM should sit between WorldAudit and SQLite. Use raw prepared commands and explicit transactions.
- Read queries must use indexes built around the exact commands admins will run in game.
- Rollback execution must be two-phase:
  - Phase 1: async planning and event selection
  - Phase 2: tick-budgeted main-thread application
- Every feature should be built behind interfaces so future integrations and compatibility layers can be added without rewriting the core.
- In-game output must favor short, readable, color-coded lines over raw IDs or verbose debug text.
- If attribution is ambiguous, the system must say so clearly instead of inventing certainty.


## 3. Success Criteria

### Functional

- `/wa inspect` can show block and container history at the targeted location.
- `/wa lookup` can search by user, cause, time, radius, action, include list, and exclude list.
- `/wa rollback` can revert a radius within a time period and honor `-b` and `-c`.
- `/wa restore` can undo a prior rollback.
- Container history can show who took items, how many, and when.
- Natural causes can be displayed distinctly from player-caused edits.

### Performance

- Normal event logging does not perform SQLite I/O on the main server thread.
- Rollback planning does not perform block changes on an async thread.
- Rollback application uses a configurable operations-per-tick budget.
- Exact block history lookups stay index-driven even when the database grows into millions of rows.
- Radius lookups use a chunk-aware bounding-box strategy before final in-memory distance filtering.

### Reliability

- No audit data is lost during normal shutdown.
- Schema upgrades are versioned and repeatable.
- Rollback jobs are resumable or safely restartable.
- Rollback-applied edits are logged as rollback or restore actions instead of being mistaken for player grief.


## 4. CoreProtect Parity Scope

WorldAudit should intentionally mirror the CoreProtect mental model where it makes sense.

### Required parity

- Inspect mode toggle
- Lookup by time, user, radius, action, include, exclude
- Near lookup alias
- Rollback and restore
- Purge and status commands
- SQLite-backed storage
- Container transaction lookup
- Clear distinction between player and non-player causes

### Vintage Story-specific adaptation

- Server commands are slash commands in multiplayer.
- Rollback application must respect Vintage Story world access rules and only mutate blocks on the main thread.
- Natural-cause attribution will need a hybrid capture strategy because the official server event surface is strongly player-centered.
- Container tracking must handle block entity inventories and item stacks with attributes, not just item counts.


## 5. Recommended Command Surface

WorldAudit should use `/worldaudit` as the full command and `/wa` as the primary alias.

### Main commands

| Command | Purpose | Notes |
| --- | --- | --- |
| `/wa help` | Show command help | Short, paged help output |
| `/wa inspect` | Toggle inspector mode | Alias: `/wa i` |
| `/wa lookup <params>` | Query audit history | Alias: `/wa l` |
| `/wa near [t:<time>]` | Quick local lookup | Default radius 5 |
| `/wa rollback <params>` | Roll back selected data | Alias: `/wa rb` |
| `/wa restore <params>` | Restore rolled back data | Alias: `/wa rs` |
| `/wa undo <jobId>` | Invert a previous rollback or restore job | Convenience command |
| `/wa apply <jobId>` | Apply a previewed rollback job | Recommended safety step |
| `/wa status` | Show DB, queue, and worker status | Admin-facing |
| `/wa purge <params>` | Delete old audit data | Maintenance command |
| `/wa reload` | Reload config | Admin-facing |
| `/wa consumer <pause|resume>` | Pause or resume DB consumption | Useful during debugging |
| `/wa jobs` | Show active rollback jobs | Async rollback visibility |
| `/wa job <id>` | Show progress for one job | Includes preview or apply counts |
| `/wa cancel <id>` | Cancel a queued or active rollback | Safe cancellation points only |

### Common parameters

WorldAudit should stay close to CoreProtect-style filters:

- `u:<user>`: one or more players or synthetic causes
- `t:<time>`: time window, for example `t:2h`, `t:3d12h`, `t:30m-2h`
- `r:<radius>`: numeric radius around the caller
- `a:<action>`: action filter
- `i:<include>`: include blocks or items
- `e:<exclude>`: exclude blocks or items
- `p:<page>`: page selector if needed
- `#preview`: show what would be rolled back without changing the world
- `#verbose`: include extra technical details
- `#global`: global search or rollback scope if explicitly allowed

### Required WorldAudit flags

- `-b`: block edits only
- `-c`: container edits only

Flag behavior:

- No `-b` or `-c` supplied means both are included by default where that makes sense.
- `-b` restricts to block place, break, replace, and stateful block restoration.
- `-c` restricts to chest, vessel, crate, and other block entity inventory transactions.

### Synthetic actors and causes

To feel like CoreProtect and keep history readable, WorldAudit should expose synthetic cause actors:

- `#fire`
- `#explosion`
- `#gravity`
- `#liquid`
- `#decay`
- `#worldgen`
- `#system`
- `#rollback`
- `#restore`
- `#unknown`

This allows admin commands such as:

- `/wa lookup u:#fire t:12h`
- `/wa rollback u:#explosion t:10m r:30 -b`
- `/wa lookup u:PlayerOne,#fire t:1d r:20`


## 6. In-Game UX Requirements

The mod only succeeds if admins can read the data quickly under pressure.

### Inspector UX

- `/wa inspect` enables a lightweight mode.
- Left-clicking or breaking a block while inspect mode is active should show block history for that exact location.
- Right-clicking or using a container while inspect mode is active should prioritize container history at that location.
- Inspector results should default to newest first.
- Inspector state should be tracked per player and expire on disconnect.

### Output formatting rules

- Use concise action verbs: `placed`, `broke`, `replaced`, `took`, `inserted`, `rolled back`, `restored`.
- Show both relative and absolute time when space allows.
- Prefer friendly block and item names, with raw codes only in verbose mode.
- Show cause explicitly, for example `cause: player`, `cause: fire`, `cause: explosion`.
- Use stable color coding:
  - green for placements and inserts
  - red for breaks and removals
  - yellow for system or natural causes
  - aqua or blue for rollback and restore actions
- Default page size should be small enough to stay readable in chat, such as 6 to 8 lines.

### Example block output

```text
[WA] 2m ago Dayton broke Oak Planks -> Air at 123, 64, -45
[WA]      cause: player | world: main | action: break

[WA] 17m ago #fire burned Dry Grass -> Air at 123, 64, -45
[WA]      cause: fire | world: main | action: replace
```

### Example container output

```text
[WA] 8m ago Dayton took 18 Flax Fibers from Reed Chest
[WA]      slot: 3 | count: 32 -> 14 | cause: player

[WA] 11m ago Rowan inserted 4 Firewood into Reed Chest
[WA]      slot: 0 | count: 0 -> 4 | cause: player
```

### Example rollback preview output

```text
[WA] Preview job #41 ready.
[WA] Blocks: 132 | Containers: 7 | Chunks: 3 | Oldest edit: 1h 43m ago
[WA] Use /wa job 41 to inspect or /wa apply 41 to apply.
```


## 7. Recommended Architecture

WorldAudit should be modular even if it ships as a single mod assembly.

### Suggested solution layout

```text
WorldAudit.slnx
src/
  WorldAudit/
    Mod/
    Application/
    Domain/
    Infrastructure/
    Integration/
    Presentation/
    Shared/
tests/
  WorldAudit.Tests/
benchmarks/
  WorldAudit.Benchmarks/
```

If multiple runtime assemblies are practical for Vintage Story packaging, split the runtime into separate projects. If packaging becomes awkward, keep one runtime assembly but keep the same folder and namespace boundaries.

### Layer responsibilities

#### Mod

- Composition root
- Mod startup and shutdown
- Dependency wiring
- Command registration
- Lifetime of async workers

#### Domain

- Audit event models
- Cause classification types
- Rollback job models
- Filter and query objects
- Immutable value objects for block positions, actor IDs, and time ranges

#### Application

- Use cases and orchestration
- Inspect service
- Lookup service
- Rollback planner
- Rollback executor coordinator
- Container diff service
- Preview generation
- Permissions and validation

#### Infrastructure

- SQLite connection factory
- Repositories
- Prepared statement cache
- Queue and batching implementation
- Snapshot serialization
- Config persistence
- Diagnostics and metrics

#### Integration

- Vintage Story event adapters
- Container session tracking
- World mutation interception
- Natural-cause providers
- Main-thread scheduling adapter

#### Presentation

- Chat command handlers
- Result formatting
- Pagination state
- Localized strings

### Key interfaces

- `IAuditWriter`
- `IAuditRepository`
- `IBlockChangeCapture`
- `IContainerCapture`
- `ICauseClassifier`
- `IRollbackPlanner`
- `IRollbackExecutor`
- `IJobScheduler`
- `IResultFormatter`
- `ISnapshotSerializer`
- `IWorldMutationGuard`

This keeps the mod testable and allows future compatibility layers for other mods or custom block systems.


## 8. Capture Pipeline

### Main-thread capture philosophy

The game thread should only do four things:

- detect a relevant event
- gather the minimum world state needed to describe it correctly
- convert it into a small immutable audit envelope
- enqueue it to the async writer

It must not:

- run SQL
- wait on disk
- perform large object graph serialization unless absolutely necessary
- scan history during capture

### Block capture strategy

WorldAudit should use a layered strategy for block changes.

#### Player-driven block changes

Use the official server block events to capture player actions:

- pre-break capture to snapshot the old block before it disappears
- post-break capture to confirm the final world state
- post-place capture to record the placed block
- use-block events for inspect mode and container session context

Because the official API exposes both pre-break and post-break behavior, the implementation should capture before and after state rather than guessing.

#### Natural and system block changes

The official Vintage Story event surface is player-focused, so CoreProtect-level parity for natural causes will likely require a hybrid approach:

- use official events where cause is explicit
- add targeted adapters for known systems such as fire, explosions, gravity, liquid flow, or decay
- where official hooks stop, use a narrow interception layer around world mutation entry points or specific block behaviors
- if a change is observed but cannot be attributed safely, record `#unknown`

Important rule:

- never label an event as `#fire` or `#explosion` unless the cause was actually observed or inferred from a trusted source path

### Container capture strategy

Container audit must be treated as a first-class feature, not a later add-on.

#### What must be captured

- container location
- inventory ID or class
- actor
- open and close session times where available
- item delta per transaction
- slot index when available
- before and after counts
- full item stack data when needed for reliable rollback

#### Recommended design

Container logging should use two linked records:

1. A transaction record
   - actor
   - time
   - container position
   - before snapshot
   - after snapshot
   - action cause

2. One or more delta records
   - item code
   - quantity delta
   - slot
   - before and after quantity

The delta records power fast lookup output.

The snapshots power reliable rollback and restore.

This is the safest way to handle item stacks with attributes such as durability, perishability, temperature, custom data, or stack merges.

#### Attribution strategy

Container APIs expose inventory open and close events and slot modification signals, but slot modification alone is not sufficient for reliable player attribution.

WorldAudit should therefore combine:

- inventory opened and closed session tracking
- container position plus inventory ID mapping
- direct player-aware inventory operation hooks where available
- slot-diff fallback for cases that lack richer context

If multiple viewers are active and the actor cannot be determined safely, the record should be marked ambiguous instead of guessed.


## 9. SQLite Data Model

WorldAudit should use a normalized but performance-aware schema.

### Lookup tables

- `schema_migrations`
- `worlds`
- `actors`
- `causes`
- `actions`
- `blocks`
- `items`
- `inventory_types`

These tables reduce repeated text storage and keep fact rows smaller.

### Main fact tables

#### `block_events`

Minimum columns:

- `id`
- `world_id`
- `x`
- `y`
- `z`
- `chunk_x`
- `chunk_y`
- `chunk_z`
- `occurred_at_ms`
- `actor_id`
- `cause_id`
- `action_id`
- `old_block_id`
- `new_block_id`
- `old_block_entity_blob`
- `new_block_entity_blob`
- `flags`
- `source_job_id`
- `source_event_id`

Notes:

- Store both block IDs and optional block entity snapshots.
- `old_block_entity_blob` and `new_block_entity_blob` are required for stateful blocks such as containers, machines, and other block entities.
- `source_event_id` lets rollback and restore trace their origin.

#### `container_transactions`

Minimum columns:

- `id`
- `world_id`
- `x`
- `y`
- `z`
- `chunk_x`
- `chunk_y`
- `chunk_z`
- `occurred_at_ms`
- `actor_id`
- `cause_id`
- `inventory_type_id`
- `container_label`
- `before_snapshot_blob`
- `after_snapshot_blob`
- `flags`
- `source_job_id`

#### `container_transaction_lines`

Minimum columns:

- `id`
- `transaction_id`
- `item_id`
- `slot_id`
- `quantity_delta`
- `before_quantity`
- `after_quantity`
- `stack_before_blob`
- `stack_after_blob`

### Job tables

#### `rollback_jobs`

- job metadata
- requesting admin
- filter summary
- preview counts
- state
- started and completed timestamps
- cancel flag

#### `rollback_job_entries`

- job ID
- target event type
- target event ID
- execution order
- result
- error text if any

This makes rollback jobs inspectable, resumable, and reversible.


## 10. Indexing Strategy

Indexes must be built for the actual command patterns, not generic CRUD.

### Required block indexes

#### Exact location history

```sql
CREATE INDEX ix_block_events_exact
ON block_events (world_id, x, y, z, occurred_at_ms DESC);
```

This powers inspect mode and exact coordinate lookups.

#### Radius lookup prefilter

```sql
CREATE INDEX ix_block_events_radius
ON block_events (world_id, chunk_x, chunk_z, occurred_at_ms DESC, x, y, z);
```

Use this for bounding-box prefilter by chunk range, then apply exact radius math in memory.

#### Actor-time lookup

```sql
CREATE INDEX ix_block_events_actor_time
ON block_events (actor_id, occurred_at_ms DESC);
```

#### Cause-time lookup

```sql
CREATE INDEX ix_block_events_cause_time
ON block_events (cause_id, occurred_at_ms DESC);
```

### Required container indexes

```sql
CREATE INDEX ix_container_transactions_exact
ON container_transactions (world_id, x, y, z, occurred_at_ms DESC);

CREATE INDEX ix_container_transactions_actor_time
ON container_transactions (actor_id, occurred_at_ms DESC);

CREATE INDEX ix_container_lines_item
ON container_transaction_lines (item_id, transaction_id);
```

### Index guidance

- Prefer a few high-value multi-column indexes over many overlapping indexes.
- Recheck index usefulness with actual query plans once lookup commands exist.
- Run `PRAGMA optimize` on long-lived connections at startup and periodically afterward.
- Re-evaluate covering-index tradeoffs after benchmarks, because larger indexes improve reads but slow writes.


## 11. SQLite Runtime Strategy

### Connection model

- one dedicated writer connection
- short-lived or pooled read connections
- one maintenance path for checkpoints and optimization

### Required SQLite settings

Recommended starting point:

- `journal_mode=WAL`
- `synchronous=NORMAL` by default, configurable to `FULL`
- `foreign_keys=ON`
- `temp_store=MEMORY`
- `busy_timeout` configured
- `cache_size` tuned for server memory budget
- `mmap_size` benchmarked and enabled if beneficial on the deployment target

### WAL strategy

WAL is the correct default for this mod because it allows readers and the writer to overlap and keeps write transactions fast, but it must be managed deliberately.

Operational rules:

- do not keep read transactions open longer than necessary
- materialize lookup results quickly and dispose the reader
- run checkpoints on a maintenance path instead of letting a gameplay-critical write pay the checkpoint cost
- track WAL growth and emit warnings if reader starvation prevents checkpoints from completing

### SQLite version safety

Because WorldAudit depends heavily on WAL mode, pin the bundled SQLite version carefully.

Recommended rule:

- require SQLite `3.51.3` or newer, released on March 13, 2026, or an approved backport containing the March 3, 2026 WAL-reset fix

### No ORM rule

Do not use Entity Framework for the runtime audit path.

Reasons:

- too much abstraction over prepared command reuse
- too much control surrendered over transactions and query shape
- higher allocation overhead than necessary for a hot logging path

Use direct SQLite commands with cached prepared statements instead.


## 12. Async Write Pipeline

### Flow

1. Main-thread event adapter produces an audit envelope.
2. Envelope is pushed into an in-memory channel or queue.
3. A single consumer batches envelopes.
4. Batch is written in one SQLite transaction.
5. Small lookup caches are updated after commit if needed.

### Batching rules

- batch by count and by age
- flush immediately during shutdown
- keep transactions reasonably sized to avoid giant WAL spikes
- maintain a moving average for flush latency and queue depth

### Caching

Use small hot caches for:

- actor ID lookup
- block code to block ID
- item code to item ID
- inventory type ID

These caches should be write-through and thread-safe.

### Failure handling

- if one record in a batch fails, log enough detail to diagnose it
- continue if the failure is isolated and safe to skip
- if the database is unavailable, surface a loud admin warning and pause risky operations
- never silently drop audit events


## 13. Query Pipeline

### Exact location lookup

This is the most common admin action and must be optimized first.

Flow:

1. Determine exact targeted block position and world.
2. Query `block_events` or `container_transactions` by indexed position.
3. Apply time filter.
4. Format newest-first results.

### Radius lookup

Flow:

1. Convert radius into a bounding box.
2. Convert bounding box into chunk ranges.
3. Query candidate rows using `world_id + chunk range + time`.
4. Filter exact radius in memory.
5. Apply include, exclude, user, cause, and action filters.

### Pagination

- store paged result state per player for a short time window
- avoid rerunning expensive lookups for every page turn when possible
- cap retained page state to prevent memory leaks

### Query object design

Every user command should be converted into a strongly typed query object before reaching the repository.

Examples:

- `AuditLookupQuery`
- `BlockHistoryQuery`
- `ContainerHistoryQuery`
- `RollbackSelectionQuery`

This keeps parsing and SQL construction separate.


## 14. Rollback and Restore Design

Rollbacks must never be implemented as one giant blocking operation.

### Two-phase model

#### Phase 1: async planning

- parse and validate the command
- query matching block and container events
- build an ordered job plan
- resolve conflicts and no-op cases
- compute preview counts
- persist the job record

#### Phase 2: tick-budgeted application

- schedule small batches onto the main thread
- apply a limited number of block or container mutations each tick
- update job progress after each batch
- allow cancellation between safe batch boundaries

### Execution order

Rollback must apply events in reverse chronological order.

Restore must apply events in chronological order relative to the rollback history it is undoing.

### Block rollback rules

- use bulk block access APIs for batch efficiency
- group changes by chunk where possible
- restore block entities when the target block requires it
- mark rollback-generated edits with `#rollback` or `#restore`
- avoid recursive reprocessing by using a world mutation guard or execution context flag

### Container rollback rules

- reverse container transactions in reverse chronological order
- prefer snapshot-based restoration for fidelity
- if the current container state is incompatible with a clean rollback, surface a conflict result
- allow `-c` rollbacks independent of `-b`

### Conflict policy

Conflicts must be explicit. Examples:

- target chunk not loaded yet
- target container missing
- block was replaced by a new unrelated structure after the selected time range
- inventory contents are no longer compatible with a strict transactional rollback

Conflict handling strategy:

- skip and report by default
- allow an admin override mode later
- never silently destroy unrelated modern state

### Job lifecycle

States:

- `planned`
- `previewed`
- `queued`
- `running`
- `paused`
- `cancelled`
- `completed`
- `completed_with_conflicts`
- `failed`


## 15. Main-Thread Rollback Application Strategy

Vintage Story world writes should remain on the main thread even when planning is async.

### Scheduling strategy

- use a game tick listener or main-thread enqueue mechanism
- consume a fixed block budget per tick
- expose config for max block changes per tick and max container restores per tick
- dynamically reduce per-tick work if the server is already under load

### Chunk-aware batching

- sort job entries by chunk
- apply contiguous positions together
- commit per small chunk batch instead of per single block
- avoid loading too many chunk columns at once

### Suggested first implementation

- plan async
- apply 100 to 300 block changes per tick by default
- use lower container snapshot restore budgets because they are more stateful
- measure before raising defaults


## 16. Natural Cause Classification Plan

This is one of the most important differences between a basic logger and a real CoreProtect-style tool.

### Cause categories

- `player`
- `fire`
- `explosion`
- `gravity`
- `liquid`
- `decay`
- `worldgen`
- `system`
- `rollback`
- `restore`
- `unknown`

### Classifier design

Use a chain-of-responsibility classifier:

- `PlayerCauseClassifier`
- `RollbackCauseClassifier`
- `ExplicitSystemCauseClassifier`
- `NaturalCauseClassifier`
- `FallbackUnknownClassifier`

Each classifier should receive a mutation context and either classify it or pass it on.

### Why this matters

- It keeps cause logic out of command handlers.
- It allows future mods to contribute their own cause providers.
- It makes output consistent across inspect, lookup, and rollback previews.


## 17. Configuration Plan

Use a simple server-side config file, for example `worldaudit.json`.

### Initial config sections

- database path
- sqlite pragmas
- writer batch size
- writer max flush delay
- rollback max blocks per tick
- rollback max containers per tick
- default page size
- default near radius
- retention days
- ignored blocks
- ignored items
- ignored actors
- enable or disable natural-cause providers
- verbose diagnostics toggle

### Safe defaults

- logging enabled for blocks and containers
- rollback preview supported
- global rollbacks restricted to privileged admins
- conservative per-tick rollback budget


## 18. Permissions and Security

WorldAudit should define explicit privileges instead of one all-powerful switch.

Suggested privileges:

- `worldaudit.inspect`
- `worldaudit.lookup`
- `worldaudit.lookup.block`
- `worldaudit.lookup.container`
- `worldaudit.rollback`
- `worldaudit.restore`
- `worldaudit.purge`
- `worldaudit.reload`
- `worldaudit.status`
- `worldaudit.consumer`
- `worldaudit.admin`

Security rules:

- rollback, restore, purge, and reload require elevated privileges
- global rollbacks require either `worldaudit.admin` or a separate override privilege
- preview is allowed anywhere a real rollback is allowed


## 19. Testing Strategy

### Unit tests

- time filter parser
- command parameter parser
- include and exclude filter parsing
- cause classifier
- container diff engine
- rollback ordering
- pagination state

### Integration tests

- SQLite schema creation and migration
- hot-path inserts with prepared statements
- exact location lookup correctness
- radius lookup correctness
- rollback plan generation
- rollback conflict reporting

### Simulation tests

- repeated player place and break loops
- fire spread or equivalent natural-cause scenarios
- explosion scenarios
- multi-viewer container interaction
- rollback on large grief events

### Performance tests

- 1 million block rows
- 10 million block rows
- mixed block and container workloads
- queue depth under burst writes
- rollback preview speed for radius 10, 20, 50


## 20. Delivery Phases

### Phase 0: Foundation

- establish folders, layers, interfaces, and config
- choose SQLite provider and pin safe version
- build migration runner
- build writer queue and metrics

### Phase 1: Block Audit MVP

- player place and break capture
- exact location inspect
- lookup with time and radius filters
- readable chat output

### Phase 2: Container Audit MVP

- container session tracking
- transaction and delta storage
- inspect and lookup for containers
- `-c` filtering

### Phase 3: Rollback MVP

- async rollback planner
- tick-budgeted block rollback executor
- preview jobs
- progress reporting
- `-b` support

### Phase 4: Restore and History Integrity

- restore command
- undo by job ID
- rollback conflict reporting
- source event linking

### Phase 5: Natural Cause Parity

- fire and explosion attribution
- gravity and decay attribution
- system and worldgen tagging
- unknown fallback behavior cleanup

### Phase 6: Admin and Maintenance

- purge
- reload
- status
- consumer pause and resume
- retention and optimize workflow

### Phase 7: Performance Hardening

- benchmark and refine indexes
- tune checkpoint cadence
- reduce allocations in capture path
- add more diagnostics


## 21. Risks and Mitigations

### Risk: natural causes are not fully exposed by public events

Mitigation:

- build the classifier as a plug-in system
- start with trusted official hooks
- add narrow compatibility adapters for systems that need deeper interception
- fall back to `#unknown` instead of pretending certainty

### Risk: container attribution is ambiguous when multiple players interact at once

Mitigation:

- track open sessions
- use player-aware inventory operation paths where available
- preserve ambiguity explicitly if certainty is impossible

### Risk: long-lived readers cause WAL growth and slow reads

Mitigation:

- keep read queries short
- materialize paged results quickly
- run controlled checkpoints
- surface WAL size in `/wa status`

### Risk: rollback causes lag spikes

Mitigation:

- never apply the whole rollback in one pass
- group by chunk
- use configurable per-tick budgets
- expose pause, cancel, and progress commands

### Risk: snapshot storage grows too quickly

Mitigation:

- only store large block entity or stack blobs where rollback fidelity requires it
- normalize repeated text through lookup tables
- allow retention and purge policies


## 22. Recommended First-Build Definition of Done

The first serious WorldAudit release should not ship until all of the following are true:

- block place and break history is correct at exact coordinates
- inspect mode is comfortable to use in-game
- container lookups correctly show who took how many items and when
- rollback jobs plan asynchronously
- rollback application is chunked and tick-budgeted
- `-b` and `-c` both work
- rollback-generated changes are clearly tagged as rollback or restore actions
- database migrations are automatic
- `/wa status` reports queue depth, DB path, SQLite version, WAL size, and active jobs


## 23. Reference Notes

This plan is aligned with the following official documentation sources:

- CoreProtect command model: [https://docs.coreprotect.net/commands/](https://docs.coreprotect.net/commands/)
- Vintage Story server block events: [https://apidocs.vintagestory.at/api/Vintagestory.API.Server.IServerEventAPI.html](https://apidocs.vintagestory.at/api/Vintagestory.API.Server.IServerEventAPI.html)
- Vintage Story main-thread scheduling: [https://apidocs.vintagestory.at/api/Vintagestory.API.Common.IEventAPI.html](https://apidocs.vintagestory.at/api/Vintagestory.API.Common.IEventAPI.html)
- Vintage Story async server threads: [https://apidocs.vintagestory.at/api/Vintagestory.API.Server.IAsyncServerSystem.html](https://apidocs.vintagestory.at/api/Vintagestory.API.Server.IAsyncServerSystem.html)
- Vintage Story world block access APIs: [https://apidocs.vintagestory.at/api/Vintagestory.API.Common.IWorldAccessor.html](https://apidocs.vintagestory.at/api/Vintagestory.API.Common.IWorldAccessor.html)
- Vintage Story inventories and containers:
  - [https://apidocs.vintagestory.at/api/Vintagestory.API.Common.IInventory.html](https://apidocs.vintagestory.at/api/Vintagestory.API.Common.IInventory.html)
  - [https://apidocs.vintagestory.at/api/Vintagestory.API.Common.IBlockEntityContainer.html](https://apidocs.vintagestory.at/api/Vintagestory.API.Common.IBlockEntityContainer.html)
  - [https://apidocs.vintagestory.at/api/Vintagestory.API.Common.InventoryBase.html](https://apidocs.vintagestory.at/api/Vintagestory.API.Common.InventoryBase.html)
- SQLite WAL and performance guidance:
  - [https://sqlite.org/wal.html](https://sqlite.org/wal.html)
  - [https://sqlite.org/lang_analyze.html](https://sqlite.org/lang_analyze.html)
  - [https://sqlite.org/queryplanner.html](https://sqlite.org/queryplanner.html)
  - [https://sqlite.org/pragma.html](https://sqlite.org/pragma.html)
