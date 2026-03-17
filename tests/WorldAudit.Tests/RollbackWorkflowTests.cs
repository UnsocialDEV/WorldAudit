using WorldAudit.Application.Abstractions;
using WorldAudit.Application.Configuration;
using WorldAudit.Application.Services;
using WorldAudit.Domain;
using WorldAudit.Integration;
using WorldAudit.Mod;

namespace WorldAudit.Tests;

public sealed class RollbackWorkflowTests : IAsyncLifetime
{
    private string _databasePath = null!;
    private WorldAuditRuntime _runtime = null!;
    private FakeRollbackBlockApplier _blockApplier = null!;
    private FakeRollbackContainerApplier _containerApplier = null!;

    public async Task InitializeAsync()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"worldaudit-rollback-{Guid.NewGuid():N}.db");
        _blockApplier = new FakeRollbackBlockApplier();
        _containerApplier = new FakeRollbackContainerApplier();
        _runtime = await WorldAuditRuntime.CreateAsync(
            new WorldAuditOptions
            {
                DatabasePath = _databasePath,
                WriterBatchSize = 2,
                WriterMaxFlushDelay = TimeSpan.FromMilliseconds(25),
                DefaultPageSize = 8,
                RollbackPreviewLimit = 100,
                RollbackMaxBlocksPerTick = 1
            },
            _blockApplier,
            _containerApplier).WaitAsync(TimeSpan.FromSeconds(5));
    }

    public async Task DisposeAsync()
    {
        await _runtime.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task RollbackPreview_CreatesPreviewJobWithReverseChronologicalEntries()
    {
        var center = new BlockPosition(0, 64, 0);
        var first = new BlockPosition(1, 64, 1);
        var second = new BlockPosition(2, 64, 2);

        await _runtime.BlockCapture.CaptureAsync(BlockChangeCaptureContext.PlayerPlaced(
            "main",
            first,
            new DateTimeOffset(2026, 3, 16, 11, 0, 0, TimeSpan.Zero),
            "Dayton",
            "player-1",
            "game:stonebrick"));

        await _runtime.BlockCapture.CaptureAsync(BlockChangeCaptureContext.PlayerPlaced(
            "main",
            second,
            new DateTimeOffset(2026, 3, 16, 11, 10, 0, TimeSpan.Zero),
            "Dayton",
            "player-1",
            "game:granite"));

        await _runtime.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));

        var filters = new LookupFilters(
            new AuditTimeRange(
                new DateTimeOffset(2026, 3, 16, 10, 30, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 3, 16, 11, 30, 0, TimeSpan.Zero)),
            Radius: 5,
            ActorName: "Dayton",
            Cause: AuditCause.Player,
            Action: BlockAuditAction.Place,
            PageSize: null,
            IncludeBlocks: true,
            IncludeContainers: false);

        var job = await _runtime.RollbackPlanner.PreviewRollbackAsync(
            "main",
            center,
            filters,
            new AuditActor("Admin", "admin-1"),
            "t:1h r:5 u:Dayton a:place -b").WaitAsync(TimeSpan.FromSeconds(5));

        var entries = await _runtime.Repository.GetRollbackBlockPlanEntriesAsync(job.Id).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RollbackJobOperation.Rollback, job.Operation);
        Assert.Equal(RollbackJobState.Previewed, job.State);
        Assert.Equal(2, job.PlannedBlockCount);
        Assert.Equal(2, entries.Count);
        Assert.True(entries[0].SourceOccurredAt > entries[1].SourceOccurredAt);
        Assert.Equal(second, entries[0].Position);
        Assert.Equal(first, entries[1].Position);
    }

    [Fact]
    public async Task RestorePreview_CreatesPreviewJobWithChronologicalEntries()
    {
        var center = new BlockPosition(0, 64, 0);
        var first = new BlockPosition(1, 64, 1);
        var second = new BlockPosition(2, 64, 2);

        await _runtime.BlockCapture.CaptureAsync(BlockChangeCaptureContext.PlayerPlaced(
            "main",
            first,
            new DateTimeOffset(2026, 3, 16, 11, 0, 0, TimeSpan.Zero),
            "Dayton",
            "player-1",
            "game:stonebrick"));

        await _runtime.BlockCapture.CaptureAsync(BlockChangeCaptureContext.PlayerPlaced(
            "main",
            second,
            new DateTimeOffset(2026, 3, 16, 11, 10, 0, TimeSpan.Zero),
            "Dayton",
            "player-1",
            "game:granite"));

        await _runtime.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));

        var filters = new LookupFilters(
            new AuditTimeRange(
                new DateTimeOffset(2026, 3, 16, 10, 30, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 3, 16, 11, 30, 0, TimeSpan.Zero)),
            Radius: 5,
            ActorName: "Dayton",
            Cause: AuditCause.Player,
            Action: BlockAuditAction.Place,
            PageSize: null,
            IncludeBlocks: true,
            IncludeContainers: false);

        var job = await _runtime.RollbackPlanner.PreviewRestoreAsync(
            "main",
            center,
            filters,
            new AuditActor("Admin", "admin-1"),
            "t:1h r:5 u:Dayton a:place -b").WaitAsync(TimeSpan.FromSeconds(5));

        var entries = await _runtime.Repository.GetRollbackBlockPlanEntriesAsync(job.Id).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RollbackJobOperation.Restore, job.Operation);
        Assert.Equal(2, entries.Count);
        Assert.True(entries[0].SourceOccurredAt < entries[1].SourceOccurredAt);
        Assert.Equal(first, entries[0].Position);
        Assert.Equal(second, entries[1].Position);
    }

    [Fact]
    public async Task RollbackApply_UsesTickBudgetAndWritesRollbackAuditEvents()
    {
        var center = new BlockPosition(0, 64, 0);
        var first = new BlockPosition(1, 64, 1);
        var second = new BlockPosition(2, 64, 2);

        await _runtime.BlockCapture.CaptureAsync(BlockChangeCaptureContext.PlayerPlaced(
            "main",
            first,
            new DateTimeOffset(2026, 3, 16, 11, 0, 0, TimeSpan.Zero),
            "Dayton",
            "player-1",
            "game:stonebrick"));

        await _runtime.BlockCapture.CaptureAsync(BlockChangeCaptureContext.PlayerPlaced(
            "main",
            second,
            new DateTimeOffset(2026, 3, 16, 11, 5, 0, TimeSpan.Zero),
            "Dayton",
            "player-1",
            "game:granite"));

        await _runtime.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));

        _blockApplier.SetCurrentBlock(first, "game:stonebrick");
        _blockApplier.SetCurrentBlock(second, "game:granite");

        var filters = new LookupFilters(
            new AuditTimeRange(
                new DateTimeOffset(2026, 3, 16, 10, 30, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 3, 16, 11, 30, 0, TimeSpan.Zero)),
            Radius: 5,
            ActorName: "Dayton",
            Cause: AuditCause.Player,
            Action: BlockAuditAction.Place,
            PageSize: null,
            IncludeBlocks: true,
            IncludeContainers: false);

        var job = await _runtime.RollbackPlanner.PreviewRollbackAsync(
            "main",
            center,
            filters,
            new AuditActor("Admin", "admin-1"),
            "t:1h r:5 u:Dayton a:place -b").WaitAsync(TimeSpan.FromSeconds(5));

        await _runtime.RollbackCoordinator.QueueApplyAsync(job.Id).WaitAsync(TimeSpan.FromSeconds(5));

        await _runtime.ProcessRollbackTickAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var runningJob = await _runtime.Repository.GetRollbackJobAsync(job.Id).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotNull(runningJob);
        Assert.Equal(RollbackJobState.Running, runningJob!.State);
        Assert.Equal(1, runningJob.AppliedBlockCount);
        Assert.Single(_blockApplier.AppliedEntries);

        await _runtime.ProcessRollbackTickAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var completedJob = await _runtime.Repository.GetRollbackJobAsync(job.Id).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotNull(completedJob);
        Assert.Equal(RollbackJobOperation.Rollback, completedJob!.Operation);
        Assert.Equal(RollbackJobState.Completed, completedJob!.State);
        Assert.Equal(2, completedJob.AppliedBlockCount);
        Assert.Equal("game:air", _blockApplier.GetCurrentBlock(first));
        Assert.Equal("game:air", _blockApplier.GetCurrentBlock(second));

        var firstHistory = await _runtime.BlockQueries.GetHistoryAsync("main", first).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(AuditCause.Rollback, firstHistory[0].Cause);
        Assert.Equal(job.Id, firstHistory[0].SourceJobId);
        Assert.Equal("game:air", firstHistory[0].NewBlockCode);
        Assert.NotNull(firstHistory[0].SourceEventId);
    }

    [Fact]
    public async Task RestoreApply_ReappliesOriginalStateAndWritesRestoreAuditEvents()
    {
        var position = new BlockPosition(3, 64, 3);

        await _runtime.BlockCapture.CaptureAsync(BlockChangeCaptureContext.PlayerPlaced(
            "main",
            position,
            new DateTimeOffset(2026, 3, 16, 11, 0, 0, TimeSpan.Zero),
            "Dayton",
            "player-1",
            "game:stonebrick"));

        await _runtime.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var sourceHistory = await _runtime.BlockQueries.GetHistoryAsync("main", position).WaitAsync(TimeSpan.FromSeconds(5));
        var sourceEventId = sourceHistory[0].Id;

        _blockApplier.SetCurrentBlock(position, "game:air");

        var filters = new LookupFilters(
            new AuditTimeRange(
                new DateTimeOffset(2026, 3, 16, 10, 30, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 3, 16, 11, 30, 0, TimeSpan.Zero)),
            Radius: 5,
            ActorName: "Dayton",
            Cause: AuditCause.Player,
            Action: BlockAuditAction.Place,
            PageSize: null,
            IncludeBlocks: true,
            IncludeContainers: false);

        var job = await _runtime.RollbackPlanner.PreviewRestoreAsync(
            "main",
            position,
            filters,
            new AuditActor("Admin", "admin-1"),
            "t:1h r:5 u:Dayton a:place -b").WaitAsync(TimeSpan.FromSeconds(5));

        await _runtime.RollbackCoordinator.QueueApplyAsync(job.Id).WaitAsync(TimeSpan.FromSeconds(5));
        await _runtime.ProcessRollbackTickAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("game:stonebrick", _blockApplier.GetCurrentBlock(position));

        var history = await _runtime.BlockQueries.GetHistoryAsync("main", position).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(AuditCause.Restore, history[0].Cause);
        Assert.Equal(job.Id, history[0].SourceJobId);
        Assert.Equal(sourceEventId, history[0].SourceEventId);
        Assert.Equal("game:stonebrick", history[0].NewBlockCode);
    }

    [Fact]
    public async Task RollbackApply_PassesBlockEntitySnapshotsToStatefulRestore()
    {
        var position = new BlockPosition(4, 64, 4);
        var snapshot = new byte[] { 10, 20, 30, 40 };

        await _runtime.AuditWriter.QueueBlockEventAsync(new BlockAuditEvent(
            Id: null,
            WorldId: "main",
            Position: position,
            OccurredAt: new DateTimeOffset(2026, 3, 16, 11, 0, 0, TimeSpan.Zero),
            Actor: new AuditActor("Dayton", "player-1"),
            Cause: AuditCause.Player,
            Action: BlockAuditAction.Break,
            OldBlockCode: "game:chest",
            NewBlockCode: "game:air",
            OldBlockEntitySnapshot: snapshot));

        await _runtime.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));
        _blockApplier.SetCurrentBlock(position, "game:air");

        var filters = new LookupFilters(
            new AuditTimeRange(
                new DateTimeOffset(2026, 3, 16, 10, 30, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 3, 16, 11, 30, 0, TimeSpan.Zero)),
            Radius: 5,
            ActorName: "Dayton",
            Cause: AuditCause.Player,
            Action: BlockAuditAction.Break,
            PageSize: null,
            IncludeBlocks: true,
            IncludeContainers: false);

        var job = await _runtime.RollbackPlanner.PreviewRollbackAsync(
            "main",
            position,
            filters,
            new AuditActor("Admin", "admin-1"),
            "t:1h r:5 u:Dayton a:break -b").WaitAsync(TimeSpan.FromSeconds(5));

        await _runtime.RollbackCoordinator.QueueApplyAsync(job.Id).WaitAsync(TimeSpan.FromSeconds(5));
        await _runtime.ProcessRollbackTickAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(_blockApplier.ReceivedSnapshots.TryGetValue(position, out var restoredSnapshot));
        Assert.Equal(snapshot, restoredSnapshot);
        Assert.Equal("game:chest", _blockApplier.GetCurrentBlock(position));
    }

    [Fact]
    public async Task RollbackApply_RecordsConflictsForStatefulRestoreFailures()
    {
        var position = new BlockPosition(5, 64, 5);
        var snapshot = new byte[] { 1, 2, 3 };

        await _runtime.AuditWriter.QueueBlockEventAsync(new BlockAuditEvent(
            Id: null,
            WorldId: "main",
            Position: position,
            OccurredAt: new DateTimeOffset(2026, 3, 16, 11, 0, 0, TimeSpan.Zero),
            Actor: new AuditActor("Dayton", "player-1"),
            Cause: AuditCause.Player,
            Action: BlockAuditAction.Break,
            OldBlockCode: "game:chest",
            NewBlockCode: "game:air",
            OldBlockEntitySnapshot: snapshot));

        await _runtime.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));
        _blockApplier.SetCurrentBlock(position, "game:air");
        _blockApplier.MarkConflict(position, "Stateful restore could not be applied cleanly.");

        var filters = new LookupFilters(
            new AuditTimeRange(
                new DateTimeOffset(2026, 3, 16, 10, 30, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 3, 16, 11, 30, 0, TimeSpan.Zero)),
            Radius: 5,
            ActorName: "Dayton",
            Cause: AuditCause.Player,
            Action: BlockAuditAction.Break,
            PageSize: null,
            IncludeBlocks: true,
            IncludeContainers: false);

        var job = await _runtime.RollbackPlanner.PreviewRollbackAsync(
            "main",
            position,
            filters,
            new AuditActor("Admin", "admin-1"),
            "t:1h r:5 u:Dayton a:break -b").WaitAsync(TimeSpan.FromSeconds(5));

        await _runtime.RollbackCoordinator.QueueApplyAsync(job.Id).WaitAsync(TimeSpan.FromSeconds(5));
        await _runtime.ProcessRollbackTickAsync().WaitAsync(TimeSpan.FromSeconds(5));

        var updatedJob = await _runtime.Repository.GetRollbackJobAsync(job.Id).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(updatedJob);
        Assert.Equal(RollbackJobState.CompletedWithConflicts, updatedJob!.State);
        Assert.Equal(1, updatedJob.ConflictBlockCount);
        Assert.Equal(0, updatedJob.AppliedBlockCount);
        Assert.Equal("game:air", _blockApplier.GetCurrentBlock(position));

        var conflicts = await _runtime.Repository.GetRollbackJobEntryDetailsAsync(job.Id, RollbackJobEntryResult.Conflict).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(conflicts);
        Assert.Equal(position, conflicts[0].Position);
        Assert.Contains("Stateful restore", conflicts[0].ErrorText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UndoPreview_CreatesInverseJobFromAppliedEntries()
    {
        var position = new BlockPosition(6, 64, 6);

        await _runtime.BlockCapture.CaptureAsync(BlockChangeCaptureContext.PlayerPlaced(
            "main",
            position,
            new DateTimeOffset(2026, 3, 16, 11, 0, 0, TimeSpan.Zero),
            "Dayton",
            "player-1",
            "game:stonebrick"));

        await _runtime.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));
        _blockApplier.SetCurrentBlock(position, "game:stonebrick");

        var filters = new LookupFilters(
            new AuditTimeRange(
                new DateTimeOffset(2026, 3, 16, 10, 30, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 3, 16, 11, 30, 0, TimeSpan.Zero)),
            Radius: 5,
            ActorName: "Dayton",
            Cause: AuditCause.Player,
            Action: BlockAuditAction.Place,
            PageSize: null,
            IncludeBlocks: true,
            IncludeContainers: false);

        var rollbackJob = await _runtime.RollbackPlanner.PreviewRollbackAsync(
            "main",
            position,
            filters,
            new AuditActor("Admin", "admin-1"),
            "t:1h r:5 u:Dayton a:place -b").WaitAsync(TimeSpan.FromSeconds(5));

        await _runtime.RollbackCoordinator.QueueApplyAsync(rollbackJob.Id).WaitAsync(TimeSpan.FromSeconds(5));
        await _runtime.ProcessRollbackTickAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("game:air", _blockApplier.GetCurrentBlock(position));

        var undoJob = await _runtime.RollbackPlanner.PreviewUndoAsync(
            rollbackJob.Id,
            new AuditActor("Admin", "admin-1")).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RollbackJobOperation.Restore, undoJob.Operation);
        Assert.Equal(1, undoJob.PlannedBlockCount);

        await _runtime.RollbackCoordinator.QueueApplyAsync(undoJob.Id).WaitAsync(TimeSpan.FromSeconds(5));
        await _runtime.ProcessRollbackTickAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("game:stonebrick", _blockApplier.GetCurrentBlock(position));
    }

    [Fact]
    public async Task ContainerRollbackPreview_CreatesContainerOnlyJob()
    {
        var position = new BlockPosition(7, 64, 7);
        var beforeSnapshot = CreateSnapshot(("game:torch", 0, 0));
        var afterSnapshot = CreateSnapshot(("game:torch", 0, 4));

        await _runtime.ContainerCapture.CaptureAsync(new ContainerTransactionCaptureContext(
            WorldId: "main",
            Position: position,
            OccurredAt: new DateTimeOffset(2026, 3, 16, 11, 0, 0, TimeSpan.Zero),
            ActorName: "Dayton",
            ActorExternalId: "player-1",
            Cause: AuditCause.Player,
            InventoryType: "chest",
            ContainerLabel: "game:chest",
            Lines: [new ContainerTransactionLineCapture("game:torch", 0, 4, 0, 4)],
            BeforeSnapshot: beforeSnapshot,
            AfterSnapshot: afterSnapshot));

        await _runtime.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));

        var job = await _runtime.RollbackPlanner.PreviewRollbackAsync(
            "main",
            position,
            new LookupFilters(
                new AuditTimeRange(
                    new DateTimeOffset(2026, 3, 16, 10, 30, 0, TimeSpan.Zero),
                    new DateTimeOffset(2026, 3, 16, 11, 30, 0, TimeSpan.Zero)),
                Radius: 5,
                ActorName: "Dayton",
                Cause: AuditCause.Player,
                Action: null,
                PageSize: null,
                IncludeCodes: ["game:torch"],
                ExcludeCodes: null,
                IncludeBlocks: false,
                IncludeContainers: true),
            new AuditActor("Admin", "admin-1"),
            "t:1h r:5 u:Dayton i:game:torch -c").WaitAsync(TimeSpan.FromSeconds(5));

        var entries = await _runtime.Repository.GetRollbackPlanEntriesAsync(job.Id).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, job.PlannedBlockCount);
        Assert.Equal(1, job.PlannedContainerCount);
        Assert.Single(entries);
        Assert.IsType<RollbackContainerPlanEntry>(entries[0]);
    }

    [Fact]
    public async Task MixedRollbackPreview_PersistsStableBlockAndContainerEntries()
    {
        var blockPosition = new BlockPosition(8, 64, 8);
        var containerPosition = new BlockPosition(9, 64, 9);
        var occurredAt = new DateTimeOffset(2026, 3, 16, 11, 0, 0, TimeSpan.Zero);

        await _runtime.BlockCapture.CaptureAsync(BlockChangeCaptureContext.PlayerPlaced(
            "main",
            blockPosition,
            occurredAt,
            "Dayton",
            "player-1",
            "game:stonebrick"));

        await _runtime.ContainerCapture.CaptureAsync(new ContainerTransactionCaptureContext(
            WorldId: "main",
            Position: containerPosition,
            OccurredAt: occurredAt,
            ActorName: "Dayton",
            ActorExternalId: "player-1",
            Cause: AuditCause.Player,
            InventoryType: "chest",
            ContainerLabel: "game:chest",
            Lines: [new ContainerTransactionLineCapture("game:torch", 0, 1, 0, 1)],
            BeforeSnapshot: CreateSnapshot(("game:torch", 0, 0)),
            AfterSnapshot: CreateSnapshot(("game:torch", 0, 1))));

        await _runtime.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));

        var job = await _runtime.RollbackPlanner.PreviewRollbackAsync(
            "main",
            blockPosition,
            new LookupFilters(
                new AuditTimeRange(occurredAt.AddMinutes(-5), occurredAt.AddMinutes(5)),
                Radius: 8,
                ActorName: "Dayton",
                Cause: AuditCause.Player,
                Action: null,
                PageSize: null,
                IncludeCodes: null,
                ExcludeCodes: null,
                IncludeBlocks: true,
                IncludeContainers: true),
            new AuditActor("Admin", "admin-1"),
            "mixed").WaitAsync(TimeSpan.FromSeconds(5));

        var entries = await _runtime.Repository.GetRollbackPlanEntriesAsync(job.Id).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, job.PlannedBlockCount);
        Assert.Equal(1, job.PlannedContainerCount);
        Assert.Equal(2, entries.Count);
        Assert.Equal(0, entries[0].ExecutionOrder);
        Assert.Equal(1, entries[1].ExecutionOrder);
    }

    [Fact]
    public async Task ContainerRollbackApply_RestoresSnapshotsAndWritesAudit()
    {
        var position = new BlockPosition(10, 64, 10);
        var beforeSnapshot = CreateSnapshot(("game:torch", 0, 0));
        var afterSnapshot = CreateSnapshot(("game:torch", 0, 4));

        await _runtime.ContainerCapture.CaptureAsync(new ContainerTransactionCaptureContext(
            WorldId: "main",
            Position: position,
            OccurredAt: new DateTimeOffset(2026, 3, 16, 11, 0, 0, TimeSpan.Zero),
            ActorName: "Dayton",
            ActorExternalId: "player-1",
            Cause: AuditCause.Player,
            InventoryType: "chest",
            ContainerLabel: "game:chest",
            Lines: [new ContainerTransactionLineCapture("game:torch", 0, 4, 0, 4)],
            BeforeSnapshot: beforeSnapshot,
            AfterSnapshot: afterSnapshot));

        await _runtime.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));
        _containerApplier.SetCurrentSnapshot(position, afterSnapshot);

        var job = await _runtime.RollbackPlanner.PreviewRollbackAsync(
            "main",
            position,
            new LookupFilters(
                new AuditTimeRange(
                    new DateTimeOffset(2026, 3, 16, 10, 30, 0, TimeSpan.Zero),
                    new DateTimeOffset(2026, 3, 16, 11, 30, 0, TimeSpan.Zero)),
                Radius: 5,
                ActorName: "Dayton",
                Cause: AuditCause.Player,
                Action: null,
                PageSize: null,
                IncludeCodes: ["game:torch"],
                ExcludeCodes: null,
                IncludeBlocks: false,
                IncludeContainers: true),
            new AuditActor("Admin", "admin-1"),
            "container rollback").WaitAsync(TimeSpan.FromSeconds(5));

        await _runtime.RollbackCoordinator.QueueApplyAsync(job.Id).WaitAsync(TimeSpan.FromSeconds(5));
        await _runtime.ProcessRollbackTickAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(beforeSnapshot, _containerApplier.GetCurrentSnapshot(position));

        var history = await _runtime.ContainerQueries.GetHistoryAsync("main", position).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(AuditCause.Rollback, history[0].Cause);
        Assert.Equal("#rollback", history[0].Actor.Name);
        Assert.Equal(job.Id, history[0].SourceJobId);
        Assert.Equal(beforeSnapshot, history[0].AfterSnapshot);
    }

    [Fact]
    public async Task RollbackApply_UsesChunkGroupedTickBudget()
    {
        var first = new BlockPosition(16, 64, 16);
        var second = new BlockPosition(17, 64, 16);
        var third = new BlockPosition(40, 64, 40);
        var occurredAt = new DateTimeOffset(2026, 3, 16, 11, 0, 0, TimeSpan.Zero);

        await _runtime.BlockCapture.CaptureAsync(BlockChangeCaptureContext.PlayerPlaced("main", first, occurredAt, "Dayton", "player-1", "game:stonebrick"));
        await _runtime.BlockCapture.CaptureAsync(BlockChangeCaptureContext.PlayerPlaced("main", second, occurredAt, "Dayton", "player-1", "game:stonebrick"));
        await _runtime.BlockCapture.CaptureAsync(BlockChangeCaptureContext.PlayerPlaced("main", third, occurredAt, "Dayton", "player-1", "game:stonebrick"));
        await _runtime.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));

        _blockApplier.SetCurrentBlock(first, "game:stonebrick");
        _blockApplier.SetCurrentBlock(second, "game:stonebrick");
        _blockApplier.SetCurrentBlock(third, "game:stonebrick");

        var runtime = await WorldAuditRuntime.CreateAsync(
            new WorldAuditOptions
            {
                DatabasePath = Path.Combine(Path.GetTempPath(), $"worldaudit-rollback-chunk-{Guid.NewGuid():N}.db"),
                WriterBatchSize = 2,
                WriterMaxFlushDelay = TimeSpan.FromMilliseconds(25),
                DefaultPageSize = 8,
                RollbackPreviewLimit = 100,
                RollbackMaxBlocksPerTick = 2
            },
            _blockApplier,
            _containerApplier).WaitAsync(TimeSpan.FromSeconds(5));

        await runtime.BlockCapture.CaptureAsync(BlockChangeCaptureContext.PlayerPlaced("main", first, occurredAt, "Dayton", "player-1", "game:stonebrick"));
        await runtime.BlockCapture.CaptureAsync(BlockChangeCaptureContext.PlayerPlaced("main", second, occurredAt, "Dayton", "player-1", "game:stonebrick"));
        await runtime.BlockCapture.CaptureAsync(BlockChangeCaptureContext.PlayerPlaced("main", third, occurredAt, "Dayton", "player-1", "game:stonebrick"));
        await runtime.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));

        var job = await runtime.RollbackPlanner.PreviewRollbackAsync(
            "main",
            first,
            new LookupFilters(new AuditTimeRange(occurredAt.AddMinutes(-1), occurredAt.AddMinutes(1)), 64, "Dayton", AuditCause.Player, BlockAuditAction.Place, null, null, null, true, false),
            new AuditActor("Admin", "admin-1"),
            "chunked").WaitAsync(TimeSpan.FromSeconds(5));

        await runtime.RollbackCoordinator.QueueApplyAsync(job.Id).WaitAsync(TimeSpan.FromSeconds(5));
        await runtime.ProcessRollbackTickAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, _blockApplier.AppliedEntries.Count);

        await runtime.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class FakeRollbackBlockApplier : IRollbackBlockApplier
    {
        private readonly Dictionary<BlockPosition, string> _currentBlocks = [];
        private readonly Dictionary<BlockPosition, string> _conflicts = [];

        public List<long> AppliedEntries { get; } = [];
        public Dictionary<BlockPosition, byte[]?> ReceivedSnapshots { get; } = [];

        public void SetCurrentBlock(BlockPosition position, string blockCode)
        {
            _currentBlocks[position] = blockCode;
        }

        public string GetCurrentBlock(BlockPosition position)
        {
            return _currentBlocks.TryGetValue(position, out var blockCode)
                ? blockCode
                : "game:air";
        }

        public void MarkConflict(BlockPosition position, string errorText)
        {
            _conflicts[position] = errorText;
        }

        public Task<RollbackBlockApplyResult> ApplyAsync(RollbackBlockPlanEntry entry, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_conflicts.TryGetValue(entry.Position, out var errorText))
            {
                return Task.FromResult(new RollbackBlockApplyResult(RollbackBlockApplyStatus.Conflict, false, GetCurrentBlock(entry.Position), errorText));
            }

            AppliedEntries.Add(entry.JobEntryId);
            var previous = GetCurrentBlock(entry.Position);
            var next = string.IsNullOrWhiteSpace(entry.TargetBlockCode) ? "game:air" : entry.TargetBlockCode!;
            var changed = !string.Equals(previous, next, StringComparison.OrdinalIgnoreCase);
            ReceivedSnapshots[entry.Position] = entry.TargetBlockEntitySnapshot;
            _currentBlocks[entry.Position] = next;
            return Task.FromResult(new RollbackBlockApplyResult(RollbackBlockApplyStatus.Applied, changed, previous));
        }
    }

    private sealed class FakeRollbackContainerApplier : IRollbackContainerApplier
    {
        private readonly Dictionary<BlockPosition, byte[]?> _currentSnapshots = [];

        public List<long> AppliedEntries { get; } = [];

        public void SetCurrentSnapshot(BlockPosition position, byte[]? snapshot)
        {
            _currentSnapshots[position] = snapshot;
        }

        public byte[]? GetCurrentSnapshot(BlockPosition position)
        {
            return _currentSnapshots.TryGetValue(position, out var snapshot)
                ? snapshot
                : null;
        }

        public Task<RollbackContainerApplyResult> ApplyAsync(RollbackContainerPlanEntry entry, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var previous = GetCurrentSnapshot(entry.Position);
            if (!SnapshotsEqual(previous, entry.ExpectedSnapshot))
            {
                return Task.FromResult(new RollbackContainerApplyResult(
                    RollbackContainerApplyStatus.Conflict,
                    false,
                    previous,
                    "Current container state no longer matches the expected rollback snapshot."));
            }

            AppliedEntries.Add(entry.JobEntryId);
            var changed = !SnapshotsEqual(previous, entry.TargetSnapshot);
            _currentSnapshots[entry.Position] = entry.TargetSnapshot;
            return Task.FromResult(new RollbackContainerApplyResult(RollbackContainerApplyStatus.Applied, changed, previous));
        }
    }

    private static byte[] CreateSnapshot((string ItemCode, int SlotId, int Quantity) slot)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(1);
        writer.Write(slot.SlotId);
        writer.Write(slot.ItemCode);
        writer.Write(slot.Quantity);
        writer.Write(0);
        writer.Flush();
        return stream.ToArray();
    }

    private static bool SnapshotsEqual(byte[]? left, byte[]? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null || left.Length != right.Length)
        {
            return false;
        }

        for (var index = 0; index < left.Length; index++)
        {
            if (left[index] != right[index])
            {
                return false;
            }
        }

        return true;
    }
}
