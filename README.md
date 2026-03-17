# WorldAudit

WorldAudit is a server-side Vintage Story mod that records block and container mutations into SQLite, exposes in-game lookup and inspect commands, and can plan or apply rollback and restore jobs.

Audit and remediation date: 2026-03-17

## Current Snapshot

- Mod id: `worldaudit`
- Version: `0.1.0`
- Side: `Server`
- Persistence: SQLite in WAL mode
- Default packaged native runtimes: `win-x64`, `linux-x64`, `linux-arm64`, `osx-x64`
- Tests passing after remediation: `65 / 65`

## What It Does

- Captures block mutations with actor, cause, action, before/after block codes, and optional block entity snapshots.
- Captures container transactions with before/after inventory snapshots and per-slot line deltas.
- Exposes `/wa` chat commands for inspect, lookup, near, rollback, restore, undo, apply, jobs, status, reload, consumer pause/resume, and purge.
- Builds rollback and restore preview jobs from historical audit data.
- Applies rollback jobs incrementally on future ticks with block and container appliers.
- Runs retention, WAL checkpoint, and SQLite optimize maintenance work.

## Remediation Status

The original high-risk findings from the audit have been implemented in code:

- Rollback apply now treats block and container mutations as all-or-nothing operations, with explicit revert attempts and failure reporting when a revert cannot be completed cleanly.
- The queued writer now guarantees `FlushAsync()` completes or throws instead of hanging on consumer faults.
- Gameplay callback paths now use synchronous enqueue APIs where appropriate and catch/log failures at the Vintage Story boundary instead of throwing into the engine loop.
- Packaging now includes multiple native SQLite runtime targets by default and validates that the Vintage Story API reference path is explicitly resolvable.
- The large repository and command classes were split into focused collaborators.

## Architecture

The project is organized into clear layers:

- `WorldAudit/Domain`
  - Immutable records and query models such as `BlockAuditEvent`, `ContainerAuditTransaction`, `RollbackJob`, and rollback plan types.
- `WorldAudit/Application`
  - Use-case services and interfaces for capture, query, maintenance, rollback planning, rollback execution, parsing, and formatting.
- `WorldAudit/Infrastructure`
  - SQLite persistence, schema migration, connection setup, native library bootstrap, and queued writer implementation.
- `WorldAudit/Integration`
  - Vintage Story adapters that translate engine callbacks into application capture models and rollback apply operations.
- `WorldAudit/Mod`
  - Mod bootstrap, config loading, runtime binding, reload orchestration, and runtime composition.
- `WorldAudit/Presentation`
  - Chat command registration, handler modules, and chat/table formatting.

### Key Runtime Collaborators

- `WorldAudit/Infrastructure/Persistence/SqliteAuditRepository.cs`
  - Thin `IAuditRepository` adapter over focused internal stores.
- `WorldAudit/Infrastructure/Persistence/SqliteAuditWriteStore.cs`
  - Persists block and container writes.
- `WorldAudit/Infrastructure/Persistence/SqliteAuditQueryStore.cs`
  - Serves history and lookup queries.
- `WorldAudit/Infrastructure/Persistence/SqliteAuditRollbackStore.cs`
  - Manages rollback jobs, plan entries, and batch result recording.
- `WorldAudit/Infrastructure/Persistence/SqliteAuditLookupResolver.cs`
  - Resolves and caches lookup ids.
- `WorldAudit/Infrastructure/Persistence/SqliteAuditRecordReader.cs`
  - Maps SQLite rows back into domain records.
- `WorldAudit/Presentation/Commands/WorldAuditChatCommands.cs`
  - Registration/composition root for `/wa`.
- `WorldAudit/Presentation/Commands/WorldAuditInspectCommandHandler.cs`
  - Inspect mode and inspect history display.
- `WorldAudit/Presentation/Commands/WorldAuditLookupCommandHandler.cs`
  - Lookup and near commands.
- `WorldAudit/Presentation/Commands/WorldAuditRollbackCommandHandler.cs`
  - Rollback, restore, undo, apply, jobs, and job detail commands.
- `WorldAudit/Presentation/Commands/WorldAuditAdminCommandHandler.cs`
  - Status, reload, consumer, and purge commands.
- `WorldAudit/Mod/WorldAuditRuntimeBinder.cs`
  - Creates and wires Vintage Story bridges and tick listeners.
- `WorldAudit/Mod/WorldAuditRuntimeReloader.cs`
  - Rebuilds runtime state during config reload.

## Runtime Flow

### Block Audit Flow

1. Vintage Story raises placement, break, use, or block behavior callbacks.
2. `VintageStoryBlockEventBridge` and `VintageStoryBlockMutationObserver` convert those callbacks into block mutation observations.
3. `BlockMutationCaptureService` classifies the observation and queues a `BlockAuditEvent`.
4. `ChannelAuditWriter` batches writes and persists them through the repository.

### Container Audit Flow

1. `VintageStoryContainerEventBridge` tracks inventory open, close, and slot changes.
2. The bridge snapshots before/after state and builds line-item deltas.
3. `ContainerTransactionCaptureService` converts that into a `ContainerAuditTransaction`.
4. `ChannelAuditWriter` persists the transaction asynchronously.

### Rollback Flow

1. `/wa rollback`, `/wa restore`, or `/wa undo` creates a preview job.
2. `RollbackPlanner` selects source records and creates ordered plan entries.
3. `/wa apply <jobId>` queues the preview for execution.
4. `RollbackExecutionCoordinator` applies entries on future ticks through the block and container appliers.
5. Synthetic rollback or restore mutations are re-audited.

## Command Reference

The mod registers `/worldaudit` with root alias `/wa`.

| Command | Purpose |
| --- | --- |
| `/wa inspect` | Toggle inspect mode. Use, open, or break a block/container to view history. |
| `/wa lookup [filters]` | Lookup audit history around the player position. |
| `/wa near [filters]` | Lookup audit history using the configured default radius. |
| `/wa rollback [filters]` | Create a rollback preview job. |
| `/wa restore [filters]` | Create a restore preview job. |
| `/wa undo <jobId>` | Create a preview job that inverts a completed rollback or restore job. |
| `/wa apply <jobId>` | Queue a preview job for execution. |
| `/wa jobs` | Show recent rollback jobs. |
| `/wa job <jobId>` | Show one rollback job in detail. |
| `/wa status` | Show database, queue, maintenance, and rollback status. |
| `/wa reload` | Reload config and rebuild the runtime. |
| `/wa consumer <pause|resume>` | Pause or resume database consumption. |
| `/wa purge t:<age>` | Preview a purge by age. |
| `/wa purge confirm t:<age>` | Execute the purge. |

### Lookup Filter Syntax

- `t:<time>` time range
- `r:<radius>` radius
- `u:<user|#cause>` actor or cause
- `a:<action>` block action
- `i:<codes>` include codes
- `e:<codes>` exclude codes
- `-b` exclude block history
- `-c` exclude container history

## Configuration

The mod reads and writes `worldaudit.json`.

| Setting | Default | Meaning |
| --- | --- | --- |
| `DatabasePath` | `worldaudit.db` | SQLite database path. Relative paths resolve under `AppContext.BaseDirectory`. |
| `WriterBatchSize` | `128` | Max items per queued flush. |
| `WriterMaxFlushDelayMilliseconds` | `250` | Max wait before a partial batch is flushed. |
| `DefaultPageSize` | `8` | Default page size for lookup output. |
| `DefaultNearRadius` | `5` | Default radius for `/wa near`. |
| `ChunkSize` | `32` | Chunk size used for chunk-based query and rollback grouping. |
| `SqliteBusyTimeoutMilliseconds` | `5000` | SQLite busy timeout. |
| `RollbackPreviewLimit` | `5000` | Max source records considered during preview. |
| `RollbackMaxBlocksPerTick` | `100` | Max rollback block entries processed per tick batch. |
| `RollbackTickIntervalMilliseconds` | `50` | Tick interval for rollback and maintenance processing. |
| `RetentionDays` | `0` | Audit retention. `0` disables age-based purge. |
| `CheckpointIntervalMinutes` | `30` | Scheduled checkpoint interval. |
| `CheckpointWalSizeMegabytes` | `64` | WAL size threshold that can trigger checkpointing. |
| `OptimizeIntervalHours` | `24` | SQLite optimize interval. |
| `SlowQueryThresholdMilliseconds` | `25` | Threshold for slow-query diagnostics. |
| `SlowFlushThresholdMilliseconds` | `50` | Threshold for slow flush diagnostics. |
| `EnableBlockAudit` | `true` | Enable block audit capture. |
| `EnableContainerAudit` | `true` | Enable container audit capture. |
| `EnableNaturalCauseAudit` | `true` | Enable natural cause classification. |
| `EnableFireCauseProvider` | `true` | Enable fire cause hints. |
| `EnableExplosionCauseProvider` | `true` | Enable explosion cause hints. |
| `EnableGravityCauseProvider` | `true` | Enable gravity cause hints. |
| `EnableDecayCauseProvider` | `true` | Enable decay cause hints. |
| `EnableSystemCauseProvider` | `true` | Enable system cause hints. |
| `EnableWorldgenCauseProvider` | `true` | Enable worldgen cause hints. |
| `VerboseDiagnostics` | `false` | Enable verbose diagnostics logging. |

## Build And Packaging

### Prerequisites

- .NET 8 SDK
- Vintage Story API assembly available for compile-time reference

### Set The Vintage Story API Path

The build resolves `VintagestoryAPI.dll` in this order:

1. `VintageStoryApiPath` MSBuild property
2. `VINTAGESTORY_API_PATH` environment variable
3. `$(APPDATA)\Vintagestory\VintagestoryAPI.dll`

If none of those resolve to a real file, the build fails fast with a clear error.

Example:

```powershell
dotnet build .\WorldAudit\WorldAudit.csproj -p:VintageStoryApiPath="C:\Games\Vintagestory\VintagestoryAPI.dll"
```

### Test

```powershell
dotnet test WorldAudit.slnx
```

### Publish And Package

```powershell
dotnet publish .\WorldAudit\WorldAudit.csproj -c Release
```

The publish target prepares:

- `WorldAudit/bin/Release/net8.0/publish/`
- `WorldAudit/bin/Release/net8.0/vintagestory-package/`
- `WorldAudit/bin/Release/net8.0/WorldAudit-vintagestory.zip`

### Override Native Runtime Packaging

Default packaged native SQLite runtimes:

- `win-x64`
- `linux-x64`
- `linux-arm64`
- `osx-x64`

Override them with:

```powershell
dotnet publish .\WorldAudit\WorldAudit.csproj -c Release -p:VintageStoryNativeRuntimeIdentifiers="win-x64;linux-x64"
```

## Testing Summary

Verified after remediation:

- `dotnet build WorldAudit.slnx`
- `dotnet test WorldAudit.slnx`
- `dotnet publish .\WorldAudit\WorldAudit.csproj -c Debug`

Result:

- `65 passed, 0 failed, 0 skipped`

Covered areas include:

- Block and container capture services
- Writer fault handling
- Lookup and purge parsing
- Formatter and inspect table output
- SQLite repository behavior
- Rollback workflow planning and persistence
- Maintenance behavior
- Packaging/build validation through publish targets

## Residual Risks

- The most engine-specific behavior still lives behind Vintage Story interfaces, so the direct rollback applier and live callback boundaries are less unit-tested than the pure application and repository layers.
- Rollback reversion is defensive and explicit now, but any failure to revert after a partial world mutation is still a high-severity inconsistency and should be treated as an operational incident.
- Packaging includes the common runtime targets by default, but dedicated CI packaging across every supported server environment would still add confidence.
