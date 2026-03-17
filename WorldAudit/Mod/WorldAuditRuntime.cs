using WorldAudit.Application;
using WorldAudit.Application.Abstractions;
using WorldAudit.Application.Configuration;
using WorldAudit.Application.Services;
using WorldAudit.Domain;
using WorldAudit.Infrastructure.Persistence;
using WorldAudit.Infrastructure.Queuing;
using WorldAudit.Integration;
using WorldAudit.Presentation.Chat;

namespace WorldAudit.Mod;

public sealed class WorldAuditRuntime : IAsyncDisposable
{
    private readonly ChannelAuditWriter _auditWriter;
    private readonly WorldMutationGuard _mutationGuard;
    private readonly BlockMutationScopeManager _blockMutationScopes;
    private readonly IAuditMaintenanceService _maintenanceService;
    private readonly AuditPerformanceDiagnostics _diagnostics;
    private readonly object _maintenanceSync = new();
    private readonly TimeSpan _checkpointInterval;
    private readonly TimeSpan _optimizeInterval;
    private readonly long _walSizeThresholdBytes;
    private readonly string _walPath;
    private DateTimeOffset _nextCheckpointAtUtc;
    private DateTimeOffset _nextOptimizeAtUtc;

    private WorldAuditRuntime(
        WorldAuditOptions options,
        SqliteSchemaMigrator schemaMigrator,
        IAuditRepository repository,
        IAuditMaintenanceService maintenanceService,
        ChannelAuditWriter auditWriter,
        IBlockChangeCapture blockCapture,
        IBlockMutationSink blockMutationCapture,
        BlockAuditQueryService blockQueries,
        IContainerTransactionCapture containerCapture,
        ContainerAuditQueryService containerQueries,
        RollbackPlanner rollbackPlanner,
        RollbackExecutionCoordinator rollbackCoordinator,
        WorldMutationGuard mutationGuard,
        BlockMutationScopeManager blockMutationScopes,
        AuditPerformanceDiagnostics diagnostics,
        IResultFormatter resultFormatter)
    {
        Options = options;
        SchemaMigrator = schemaMigrator;
        Repository = repository;
        MaintenanceService = maintenanceService;
        AuditWriter = auditWriter;
        BlockCapture = blockCapture;
        BlockMutationCapture = blockMutationCapture;
        BlockQueries = blockQueries;
        ContainerCapture = containerCapture;
        ContainerQueries = containerQueries;
        RollbackPlanner = rollbackPlanner;
        RollbackCoordinator = rollbackCoordinator;
        ResultFormatter = resultFormatter;
        _auditWriter = auditWriter;
        _mutationGuard = mutationGuard;
        _blockMutationScopes = blockMutationScopes;
        _maintenanceService = maintenanceService;
        _diagnostics = diagnostics;
        _checkpointInterval = TimeSpan.FromMinutes(options.CheckpointIntervalMinutes);
        _optimizeInterval = TimeSpan.FromHours(options.OptimizeIntervalHours);
        _walSizeThresholdBytes = Math.Max(1L, options.CheckpointWalSizeMegabytes) * 1024L * 1024L;
        _walPath = $"{options.DatabasePath}-wal";
        _nextCheckpointAtUtc = DateTimeOffset.MinValue;
        _nextOptimizeAtUtc = DateTimeOffset.MinValue;
    }

    public WorldAuditOptions Options { get; }

    public SqliteSchemaMigrator SchemaMigrator { get; }

    public IAuditRepository Repository { get; }

    public IAuditMaintenanceService MaintenanceService { get; }

    public IAuditWriter AuditWriter { get; }

    public IBlockChangeCapture BlockCapture { get; }

    internal IBlockMutationSink BlockMutationCapture { get; }

    internal BlockMutationScopeManager BlockMutationScopes => _blockMutationScopes;

    public BlockAuditQueryService BlockQueries { get; }

    public IContainerTransactionCapture ContainerCapture { get; }

    public ContainerAuditQueryService ContainerQueries { get; }

    public RollbackPlanner RollbackPlanner { get; }

    public RollbackExecutionCoordinator RollbackCoordinator { get; }

    public IWorldMutationGuard MutationGuard => _mutationGuard;

    public IResultFormatter ResultFormatter { get; }

    public bool IsConsumerPaused => _auditWriter.IsPaused;

    public bool HasActiveRollbackJobs => RollbackCoordinator.HasActiveJobs();

    public static async Task<WorldAuditRuntime> CreateAsync(
        WorldAuditOptions? options = null,
        IRollbackBlockApplier? rollbackBlockApplier = null,
        IRollbackContainerApplier? rollbackContainerApplier = null,
        Action<string>? diagnosticsLogger = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new WorldAuditOptions();

        var connectionFactory = new SqliteConnectionFactory(options);
        var migrator = new SqliteSchemaMigrator(connectionFactory);
        await migrator.MigrateAsync(cancellationToken).ConfigureAwait(false);

        var diagnostics = new AuditPerformanceDiagnostics(options, diagnosticsLogger);
        var repository = new SqliteAuditRepository(connectionFactory, options, diagnostics);
        var maintenanceRepository = new SqliteAuditMaintenanceRepository(connectionFactory);
        var maintenanceService = new AuditMaintenanceService(maintenanceRepository);
        var auditWriter = new ChannelAuditWriter(repository, options, diagnostics);
        var mutationGuard = new WorldMutationGuard();
        var blockMutationScopes = new BlockMutationScopeManager();
        var blockMutationClassifier = new BlockMutationClassifierChain(options);
        var blockMutationCapture = new BlockMutationCaptureService(auditWriter, mutationGuard, blockMutationClassifier);
        var blockCapture = new BlockChangeCaptureService(auditWriter, mutationGuard);
        var blockQueries = new BlockAuditQueryService(repository, options);
        var containerCapture = new ContainerTransactionCaptureService(auditWriter);
        var containerQueries = new ContainerAuditQueryService(repository, options);
        var rollbackPlanner = new RollbackPlanner(repository, options);
        var rollbackCoordinator = new RollbackExecutionCoordinator(
            repository,
            auditWriter,
            rollbackBlockApplier ?? new NullRollbackBlockApplier(),
            rollbackContainerApplier ?? new NullRollbackContainerApplier(),
            mutationGuard,
            options);
        var formatter = new ChatAuditFormatter();

        return new WorldAuditRuntime(
            options,
            migrator,
            repository,
            maintenanceService,
            auditWriter,
            blockCapture,
            blockMutationCapture,
            blockQueries,
            containerCapture,
            containerQueries,
            rollbackPlanner,
            rollbackCoordinator,
            mutationGuard,
            blockMutationScopes,
            diagnostics,
            formatter);
    }

    public Task FlushAsync(CancellationToken cancellationToken = default)
    {
        return _auditWriter.FlushAsync(cancellationToken);
    }

    public void PauseConsumer()
    {
        _auditWriter.Pause();
    }

    public void ResumeConsumer()
    {
        _auditWriter.Resume();
    }

    public async Task<AuditStatusSnapshot> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var storageSnapshot = await _maintenanceService.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return new AuditStatusSnapshot(
            DatabasePath: Options.DatabasePath,
            SqliteVersion: storageSnapshot.SqliteVersion,
            WalSizeBytes: storageSnapshot.WalSizeBytes,
            IsConsumerPaused: IsConsumerPaused,
            WriterSnapshot: _auditWriter.Snapshot(),
            ActiveRollbackJobCount: RollbackCoordinator.GetActiveJobCount(),
            RetentionDays: Options.RetentionDays,
            LastMaintenanceRun: _maintenanceService.LastRun,
            QueryPerformance: _diagnostics.SnapshotQueries(),
            CheckpointPolicy: new AuditCheckpointPolicySnapshot(_checkpointInterval, _walSizeThresholdBytes, _optimizeInterval),
            LastCheckpoint: _diagnostics.SnapshotCheckpoint(),
            LastOptimize: _diagnostics.SnapshotOptimize(),
            IsWalThresholdExceeded: storageSnapshot.WalSizeBytes >= _walSizeThresholdBytes);
    }

    public Task<PurgePreview> PreviewPurgeAsync(TimeSpan age, DateTimeOffset? now = null, CancellationToken cancellationToken = default)
    {
        return _maintenanceService.PreviewPurgeAsync(ToCutoff(age, now), cancellationToken);
    }

    public async Task<PurgeResult> ExecutePurgeAsync(TimeSpan age, DateTimeOffset? now = null, CancellationToken cancellationToken = default)
    {
        var startedAt = now ?? DateTimeOffset.UtcNow;
        var cutoff = ToCutoff(age, now);

        await EnsureNoBlockingRollbackJobsAsync(cancellationToken).ConfigureAwait(false);
        await _auditWriter.FlushAsync(cancellationToken).ConfigureAwait(false);

        var result = await _maintenanceService.ExecutePurgeAsync(
            cutoff,
            SqliteCheckpointMode.Truncate,
            optimizeAfterCheckpoint: true,
            cancellationToken).ConfigureAwait(false);

        RecordPurgeDiagnostics(result, "explicit purge");
        AdvanceMaintenanceSchedule(startedAt, checkpointCompleted: true, optimizeCompleted: true);

        var summary = $"Purged {result.TotalDeletedCount} record(s), checkpointed WAL, optimized SQLite.";
        await _maintenanceService.RecordMaintenanceRunAsync(
            startedAt,
            DateTimeOffset.UtcNow,
            succeeded: true,
            retentionDays: Options.RetentionDays,
            cutoff: cutoff,
            summary: summary,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return result;
    }

    public async Task<MaintenanceRunStatus> ProcessMaintenanceTickAsync(
        bool force = false,
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default)
    {
        var effectiveNow = now ?? DateTimeOffset.UtcNow;
        var walSizeBytes = GetWalSizeBytes();
        var checkpointDueByTime = effectiveNow >= _nextCheckpointAtUtc;
        var checkpointDueByWal = walSizeBytes >= _walSizeThresholdBytes;
        var optimizeDue = effectiveNow >= _nextOptimizeAtUtc;
        var purgeDue = Options.RetentionDays > 0 && (force || checkpointDueByTime);

        if (!force && !checkpointDueByTime && !checkpointDueByWal && !optimizeDue)
        {
            return _maintenanceService.LastRun
                ?? new MaintenanceRunStatus(
                    effectiveNow,
                    effectiveNow,
                    true,
                    Options.RetentionDays,
                    Options.RetentionDays > 0 ? effectiveNow.AddDays(-Options.RetentionDays) : null,
                    "Maintenance not due.");
        }

        if (await _maintenanceService.GetBlockingRollbackJobCountAsync(cancellationToken).ConfigureAwait(false) > 0)
        {
            var skipped = new MaintenanceRunStatus(
                effectiveNow,
                DateTimeOffset.UtcNow,
                false,
                Options.RetentionDays,
                Options.RetentionDays > 0 ? effectiveNow.AddDays(-Options.RetentionDays) : null,
                "Maintenance skipped while rollback jobs are active.",
                "Active rollback jobs prevent maintenance.");
            _maintenanceService.RecordRun(skipped);
            _diagnostics.LogVerbose("Maintenance skipped because rollback jobs are active.");
            return skipped;
        }

        await _auditWriter.FlushAsync(cancellationToken).ConfigureAwait(false);

        var startedAt = effectiveNow;
        try
        {
            PurgeResult? purgeResult = null;
            SqliteCheckpointResult? checkpointResult = null;
            TimeSpan checkpointDuration = TimeSpan.Zero;
            TimeSpan optimizeDuration = TimeSpan.Zero;
            DateTimeOffset? cutoff = null;
            var checkpointReason = force
                ? "startup/reload"
                : checkpointDueByWal
                    ? "wal threshold"
                    : "scheduled interval";
            var optimizeReason = force ? "startup/reload" : "scheduled interval";

            if (purgeDue)
            {
                cutoff = effectiveNow.AddDays(-Options.RetentionDays);
                purgeResult = await _maintenanceService.ExecutePurgeAsync(
                    cutoff.Value,
                    SqliteCheckpointMode.Passive,
                    optimizeAfterCheckpoint: false,
                    cancellationToken).ConfigureAwait(false);
                RecordPurgeDiagnostics(purgeResult, "retention purge");
            }
            else if (force || checkpointDueByTime || checkpointDueByWal)
            {
                var checkpointStarted = DateTimeOffset.UtcNow;
                checkpointResult = await _maintenanceService.RunCheckpointAsync(SqliteCheckpointMode.Passive, cancellationToken).ConfigureAwait(false);
                checkpointDuration = DateTimeOffset.UtcNow - checkpointStarted;
                _diagnostics.RecordCheckpoint(checkpointReason, checkpointResult, checkpointDuration);
            }

            var optimizeRan = false;
            if (force || optimizeDue)
            {
                var optimizeStarted = DateTimeOffset.UtcNow;
                await _maintenanceService.RunOptimizeAsync(cancellationToken).ConfigureAwait(false);
                optimizeDuration = DateTimeOffset.UtcNow - optimizeStarted;
                _diagnostics.RecordOptimize(optimizeReason, optimizeDuration);
                optimizeRan = true;
            }

            AdvanceMaintenanceSchedule(effectiveNow, checkpointCompleted: purgeDue || force || checkpointDueByTime || checkpointDueByWal, optimizeCompleted: optimizeRan || force);

            var summary = BuildMaintenanceSummary(purgeResult, checkpointResult, checkpointReason, optimizeRan);
            return await _maintenanceService.RecordMaintenanceRunAsync(
                startedAt,
                DateTimeOffset.UtcNow,
                succeeded: true,
                retentionDays: Options.RetentionDays,
                cutoff: cutoff,
                summary: summary,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            return await _maintenanceService.RecordMaintenanceRunAsync(
                startedAt,
                DateTimeOffset.UtcNow,
                succeeded: false,
                retentionDays: Options.RetentionDays,
                cutoff: Options.RetentionDays > 0 ? effectiveNow.AddDays(-Options.RetentionDays) : null,
                summary: "Maintenance failed.",
                error: exception.Message,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    public Task ProcessRollbackTickAsync(CancellationToken cancellationToken = default)
    {
        return RollbackCoordinator.ProcessTickAsync(cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        return _auditWriter.DisposeAsync();
    }

    private async Task EnsureNoBlockingRollbackJobsAsync(CancellationToken cancellationToken)
    {
        if (await _maintenanceService.GetBlockingRollbackJobCountAsync(cancellationToken).ConfigureAwait(false) > 0)
        {
            throw new InvalidOperationException("Non-terminal rollback jobs prevent this operation.");
        }
    }

    private void RecordPurgeDiagnostics(PurgeResult result, string reason)
    {
        if (result.CheckpointRan && result.CheckpointMode.HasValue)
        {
            _diagnostics.RecordCheckpoint(
                reason,
                new SqliteCheckpointResult(
                    result.CheckpointMode.Value,
                    result.WalSizeBytesBeforeCheckpoint,
                    result.WalSizeBytesAfterCheckpoint),
                result.CheckpointDuration);
        }

        if (result.OptimizeRan)
        {
            _diagnostics.RecordOptimize(reason, result.OptimizeDuration);
        }
    }

    private void AdvanceMaintenanceSchedule(DateTimeOffset now, bool checkpointCompleted, bool optimizeCompleted)
    {
        lock (_maintenanceSync)
        {
            if (checkpointCompleted)
            {
                _nextCheckpointAtUtc = now + _checkpointInterval;
            }

            if (optimizeCompleted)
            {
                _nextOptimizeAtUtc = now + _optimizeInterval;
            }
        }
    }

    private long GetWalSizeBytes()
    {
        return File.Exists(_walPath)
            ? new FileInfo(_walPath).Length
            : 0L;
    }

    private static string BuildMaintenanceSummary(
        PurgeResult? purgeResult,
        SqliteCheckpointResult? checkpointResult,
        string checkpointReason,
        bool optimizeRan)
    {
        if (purgeResult is not null)
        {
            return optimizeRan
                ? $"Purged {purgeResult.TotalDeletedCount} record(s), checkpointed WAL, optimized SQLite."
                : $"Purged {purgeResult.TotalDeletedCount} record(s) and checkpointed WAL.";
        }

        if (checkpointResult is not null)
        {
            return optimizeRan
                ? $"Checkpointed WAL ({checkpointReason}) and optimized SQLite."
                : $"Checkpointed WAL ({checkpointReason}).";
        }

        return optimizeRan
            ? "Optimized SQLite."
            : "Maintenance completed.";
    }

    private static DateTimeOffset ToCutoff(TimeSpan age, DateTimeOffset? now)
    {
        if (age <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(age), "Purge age must be greater than zero.");
        }

        return (now ?? DateTimeOffset.UtcNow) - age;
    }

    private sealed class NullRollbackBlockApplier : IRollbackBlockApplier
    {
        public Task<RollbackBlockApplyResult> ApplyAsync(RollbackBlockPlanEntry entry, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new RollbackBlockApplyResult(RollbackBlockApplyStatus.Failed, false, null, "Rollback block applier has not been configured."));
        }
    }

    private sealed class NullRollbackContainerApplier : IRollbackContainerApplier
    {
        public Task<RollbackContainerApplyResult> ApplyAsync(RollbackContainerPlanEntry entry, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new RollbackContainerApplyResult(RollbackContainerApplyStatus.Failed, false, null, "Rollback container applier has not been configured."));
        }
    }
}
