using Microsoft.Data.Sqlite;
using WorldAudit.Application.Abstractions;
using WorldAudit.Application.Configuration;
using WorldAudit.Application.Services;
using WorldAudit.Domain;
using WorldAudit.Infrastructure.Persistence;
using WorldAudit.Mod;
using WorldAudit.Tests.TestSupport;

namespace WorldAudit.Tests;

public sealed class RollbackReliabilityTests
{
    [Fact]
    public async Task Upgrade_CancelsUnsafeLegacyPlansWithoutDeletingHistory()
    {
        var options = Options();
        long jobId;
        await using (var old = await WorldAuditRuntime.CreateAsync(options)) jobId = (await CreateJob(old.Repository)).Id;
        var factory = new SqliteConnectionFactory(options);
        await using (var connection = await factory.OpenConnectionAsync())
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "ALTER TABLE block_events DROP COLUMN layer; ALTER TABLE rollback_job_entries DROP COLUMN reverse_source; DELETE FROM schema_migrations WHERE version = 8;";
            await command.ExecuteNonQueryAsync();
        }
        await using var upgraded = await WorldAuditRuntime.CreateAsync(options);
        Assert.Equal(RollbackJobState.Cancelled, (await upgraded.Repository.GetRollbackJobAsync(jobId))!.State);
        Assert.Single(await upgraded.Repository.GetRollbackPlanEntriesAsync(jobId));
        Assert.Equal(0, await upgraded.MaintenanceService.GetBlockingRollbackJobCountAsync());
    }

    private static readonly BlockPosition Position = new(1, 64, 1);
    private static BlockAuditEvent Event(string actor = "Alice") => new(null, "main", Position, DateTimeOffset.UtcNow,
        new AuditActor(actor, actor), AuditCause.Player, BlockAuditAction.Place, "game:air", "game:stone");
    private static WorldAuditOptions Options() => new() { DatabasePath = Path.Combine(Path.GetTempPath(), $"wa-reliable-{Guid.NewGuid():N}.db") };

    private static async Task<RollbackJob> CreateJob(IAuditRepository repository)
    {
        await repository.WriteBlockEventsAsync([Event()]);
        var source = Assert.Single(await repository.GetBlockHistoryAsync(new("main", Position)));
        return await repository.CreateRollbackJobAsync(
            new("main", new("Admin", "admin"), RollbackJobOperation.Rollback, "test", RollbackJobState.Previewed, 1, 0, 1, source.OccurredAt),
            [new(RollbackJobEntryTargetType.Block, source.Id!.Value, 0)]);
    }

    [Fact]
    public async Task Tick_RetriesFailedResultPersistenceWithoutRepeatingMutation()
    {
        var job = new RollbackJob(1, "main", new("Admin", "admin"), RollbackJobOperation.Rollback, "test", RollbackJobState.Previewed,
            DateTimeOffset.UtcNow, null, null, null, 1, 0, 1, 0, 0, 0, 0, 0, 0, null, null);
        var entry = new RollbackBlockPlanEntry(1, 1, 1, "main", RollbackJobOperation.Rollback, Position, new(0, 2, 0),
            DateTimeOffset.UtcNow, "game:air", null, 0, "game:stone");
        var attempts = 0;
        var applies = 0;
        RollbackBatchEntryUpdate? saved = null;
        var repository = ProxyFactory.Create<IAuditRepository>(p =>
        {
            p.SetHandler(nameof(IAuditRepository.GetRollbackJobAsync), _ => Task.FromResult<RollbackJob?>(job));
            p.SetHandler(nameof(IAuditRepository.GetRollbackPlanEntriesAsync), _ => Task.FromResult<IReadOnlyList<RollbackPlanEntry>>([entry]));
            p.SetHandler(nameof(IAuditRepository.UpdateRollbackJobStateAsync), args => { job = job with { State = (RollbackJobState)args![1]! }; return Task.CompletedTask; });
            p.SetHandler(nameof(IAuditRepository.RecordRollbackBatchAsync), args =>
            {
                if (++attempts == 1) return Task.FromException(new IOException("temporary disk failure"));
                saved = Assert.Single((IReadOnlyList<RollbackBatchEntryUpdate>)args![1]!);
                job = job with { AppliedBlockCount = 1 };
                return Task.CompletedTask;
            });
        });
        var applier = ProxyFactory.Create<IRollbackBlockApplier>(p => p.SetHandler(nameof(IRollbackBlockApplier.ApplyAsync), _ =>
        {
            applies++;
            return Task.FromResult(new RollbackBlockApplyResult(RollbackBlockApplyStatus.Applied, true, "game:stone", ActualBlockCode: "game:air"));
        }));
        var writer = ProxyFactory.Create<IAuditWriter>(p => p.SetHandler(nameof(IAuditWriter.FlushAsync), _ => Task.CompletedTask));
        var coordinator = new RollbackExecutionCoordinator(repository, writer, applier,
            ProxyFactory.Create<IRollbackContainerApplier>(_ => { }), new WorldMutationGuard(), new());
        await coordinator.QueueApplyAsync(1);
        await Assert.ThrowsAsync<IOException>(() => coordinator.ProcessTickAsync());
        Assert.True(coordinator.HasActiveJobs());
        Assert.Equal(RollbackJobState.Running, job.State);
        await coordinator.ProcessTickAsync();
        Assert.Equal(1, applies);
        Assert.Equal(2, attempts);
        Assert.Equal(RollbackJobState.Completed, job.State);
        Assert.Equal("game:stone", saved!.BlockMutation!.OldBlockCode);
        Assert.Equal("game:air", saved.BlockMutation.NewBlockCode);
    }

    [Fact]
    public async Task Batch_AtomicallyPersistsMutationAndCountsAndIsSafeToRetry()
    {
        var options = Options();
        await using var runtime = await WorldAuditRuntime.CreateAsync(options);
        var job = await CreateJob(runtime.Repository);
        var entry = Assert.Single(await runtime.Repository.GetRollbackBlockPlanEntriesAsync(job.Id));
        var update = new RollbackBatchEntryUpdate(entry.JobEntryId, entry.TargetType, RollbackJobEntryResult.Applied,
            BlockMutation: Event("#rollback") with { OldBlockCode = "game:stone", NewBlockCode = "game:air", SourceJobId = job.Id, SourceEventId = entry.SourceEventId });
        var factory = new SqliteConnectionFactory(options);
        await using (var connection = await factory.OpenConnectionAsync())
        {
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER fail_batch BEFORE UPDATE OF applied_block_count ON rollback_jobs BEGIN SELECT RAISE(ABORT, 'injected failure'); END;";
            await command.ExecuteNonQueryAsync();
        }
        await Assert.ThrowsAsync<SqliteException>(() => runtime.Repository.RecordRollbackBatchAsync(job.Id, [update]));
        Assert.Equal(0, (await runtime.Repository.GetRollbackJobAsync(job.Id))!.AppliedBlockCount);
        Assert.Single(await runtime.Repository.GetRollbackBlockPlanEntriesAsync(job.Id));
        Assert.Single(await runtime.Repository.GetBlockHistoryAsync(new("main", Position)));
        // Allocate different lookup IDs after the failed transaction to expose poisoned caches.
        await runtime.Repository.WriteBlockEventsAsync([Event("Other") with { Position = new(20, 64, 20) }]);
        await using (var connection = await factory.OpenConnectionAsync())
        {
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TRIGGER fail_batch;";
            await command.ExecuteNonQueryAsync();
        }
        await runtime.Repository.RecordRollbackBatchAsync(job.Id, [update]);
        await runtime.Repository.RecordRollbackBatchAsync(job.Id, [update]);
        Assert.Equal(1, (await runtime.Repository.GetRollbackJobAsync(job.Id))!.AppliedBlockCount);
        var history = await runtime.Repository.GetBlockHistoryAsync(new("main", Position));
        Assert.Equal(2, history.Count);
        Assert.Equal("#rollback", Assert.Single(history.Where(e => e.SourceJobId == job.Id)).Actor.Name);
    }

    [Theory]
    [InlineData(RollbackBlockApplyStatus.Applied)]
    [InlineData(RollbackBlockApplyStatus.Failed)]
    public async Task Undo_UsesActualMutationSnapshotsEvenWhenCompensationFailed(RollbackBlockApplyStatus status)
    {
        var applier = ProxyFactory.Create<IRollbackBlockApplier>(p => p.SetHandler(nameof(IRollbackBlockApplier.ApplyAsync), _ =>
            Task.FromResult(new RollbackBlockApplyResult(status, true, "game:stone",
                PreviousBlockEntitySnapshot: [7], ActualBlockCode: "game:air", ActualBlockEntitySnapshot: [8]))));
        await using var runtime = await WorldAuditRuntime.CreateAsync(Options(), applier);
        var job = await CreateJob(runtime.Repository);
        await runtime.RollbackCoordinator.QueueApplyAsync(job.Id);
        await runtime.ProcessRollbackTickAsync();
        var undo = await runtime.RollbackPlanner.PreviewUndoAsync(job.Id, new("Admin", "admin"));
        var entry = Assert.Single(await runtime.Repository.GetRollbackBlockPlanEntriesAsync(undo.Id));
        Assert.Equal(RollbackJobOperation.Restore, undo.Operation);
        Assert.Equal("game:stone", entry.TargetBlockCode);
        Assert.Equal(new byte[] { 7 }, entry.TargetBlockEntitySnapshot);
        Assert.Equal("game:air", entry.ExpectedBlockCode);
        Assert.Equal(new byte[] { 8 }, entry.ExpectedBlockEntitySnapshot);
        Assert.Equal(1, entry.Layer);
    }

    [Fact]
    public async Task Restart_PausesInterruptedJobsAndCancelReleasesMaintenance()
    {
        var options = Options();
        long jobId;
        await using (var first = await WorldAuditRuntime.CreateAsync(options))
        {
            jobId = (await CreateJob(first.Repository)).Id;
            // A persisted running job with no in-memory coordinator simulates an interrupted server.
            await first.Repository.UpdateRollbackJobStateAsync(jobId, RollbackJobState.Running);
        }
        await using var restarted = await WorldAuditRuntime.CreateAsync(options);
        Assert.Equal(RollbackJobState.Paused, (await restarted.Repository.GetRollbackJobAsync(jobId))!.State);
        Assert.False(restarted.RollbackCoordinator.HasActiveJobs());
        Assert.Equal(1, await restarted.MaintenanceService.GetBlockingRollbackJobCountAsync());
        await restarted.RollbackCoordinator.CancelAsync(jobId);
        Assert.Equal(0, await restarted.MaintenanceService.GetBlockingRollbackJobCountAsync());
        Assert.Equal(RollbackJobState.Cancelled, (await restarted.RollbackCoordinator.QueueApplyAsync(jobId))!.State);
        Assert.False(restarted.RollbackCoordinator.HasActiveJobs());
    }

    [Fact]
    public async Task Cancel_DisposesUnappliedPreviewAndRejectsOtherWorlds()
    {
        var options = new WorldAuditOptions { DatabasePath = Options().DatabasePath, WorldId = "main" };
        await using var runtime = await WorldAuditRuntime.CreateAsync(options);
        var job = await CreateJob(runtime.Repository);
        await runtime.RollbackCoordinator.CancelAsync(job.Id);
        Assert.Equal(0, await runtime.MaintenanceService.GetBlockingRollbackJobCountAsync());
        var other = await runtime.Repository.CreateRollbackJobAsync(
            new("other-save", new("Admin", "admin"), RollbackJobOperation.Rollback, "test", RollbackJobState.Previewed, 0, 0, 0, null), []);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.RollbackCoordinator.QueueApplyAsync(other.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.RollbackCoordinator.CancelAsync(other.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.RollbackPlanner.PreviewUndoAsync(other.Id, new("Admin", "admin")));
    }

    [Fact]
    public async Task CancelledTick_RetainsUnappliedEntriesAndPersistsCompletedMutation()
    {
        using var cancellation = new CancellationTokenSource();
        var applies = 0;
        var applier = ProxyFactory.Create<IRollbackBlockApplier>(p => p.SetHandler(nameof(IRollbackBlockApplier.ApplyAsync), _ =>
        {
            if (++applies == 1) cancellation.Cancel();
            return Task.FromResult(new RollbackBlockApplyResult(RollbackBlockApplyStatus.Applied, true, "game:stone", ActualBlockCode: "game:air"));
        }));
        await using var runtime = await WorldAuditRuntime.CreateAsync(Options(), applier);
        var firstJob = await CreateJob(runtime.Repository);
        var source = Assert.Single(await runtime.Repository.GetRollbackBlockPlanEntriesAsync(firstJob.Id));
        var job = await runtime.Repository.CreateRollbackJobAsync(
            new("main", new("Admin", "admin"), RollbackJobOperation.Rollback, "test", RollbackJobState.Previewed, 2, 0, 1, source.SourceOccurredAt),
            [new(RollbackJobEntryTargetType.Block, source.SourceEventId, 0), new(RollbackJobEntryTargetType.Block, source.SourceEventId, 1)]);
        await runtime.RollbackCoordinator.QueueApplyAsync(job.Id);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.ProcessRollbackTickAsync(cancellation.Token));
        Assert.Equal(1, applies);
        await runtime.ProcessRollbackTickAsync();
        Assert.Equal(2, applies);
        Assert.Equal(2, (await runtime.Repository.GetRollbackJobAsync(job.Id))!.AppliedBlockCount);
        Assert.Equal(2, (await runtime.Repository.GetBlockHistoryAsync(new("main", Position))).Count(e => e.SourceJobId == job.Id));
    }
}
