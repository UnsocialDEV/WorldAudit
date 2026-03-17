using Microsoft.Data.Sqlite;
using WorldAudit.Application;
using WorldAudit.Application.Abstractions;

namespace WorldAudit.Infrastructure.Persistence;

public sealed class SqliteAuditMaintenanceRepository : IAuditMaintenanceRepository
{
    private static readonly string[] TerminalRollbackStates =
    [
        "completed",
        "completedwithconflicts",
        "failed"
    ];

    private readonly SqliteConnectionFactory _connectionFactory;

    public SqliteAuditMaintenanceRepository(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<SqliteMaintenanceSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT sqlite_version();";
        var version = (string?)await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? "unknown";
        return new SqliteMaintenanceSnapshot(version, GetWalSizeBytes());
    }

    public async Task<int> GetBlockingRollbackJobCountAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT COUNT(*)
            FROM rollback_jobs
            WHERE state_code NOT IN ({BuildStateList()});
            """;
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(result);
    }

    public async Task<PurgePreview> PreviewPurgeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var cutoffMs = cutoff.ToUnixTimeMilliseconds();

        var blockCount = await CountAsync(
            connection,
            "SELECT COUNT(*) FROM block_events WHERE occurred_at_ms < $cutoff;",
            cutoffMs,
            cancellationToken).ConfigureAwait(false);
        var containerCount = await CountAsync(
            connection,
            "SELECT COUNT(*) FROM container_transactions WHERE occurred_at_ms < $cutoff;",
            cutoffMs,
            cancellationToken).ConfigureAwait(false);
        var rollbackJobCount = await CountTerminalRollbackJobsAsync(connection, cutoffMs, cancellationToken).ConfigureAwait(false);

        return new PurgePreview(cutoff, blockCount, containerCount, rollbackJobCount);
    }

    public async Task<PurgeResult> ExecutePurgeAsync(
        DateTimeOffset cutoff,
        SqliteCheckpointMode checkpointMode,
        bool optimizeAfterCheckpoint,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var cutoffMs = cutoff.ToUnixTimeMilliseconds();

        var deletedBlockEvents = await ExecuteDeleteAsync(
            connection,
            transaction,
            "DELETE FROM block_events WHERE occurred_at_ms < $cutoff;",
            cutoffMs,
            cancellationToken).ConfigureAwait(false);
        var deletedContainerTransactions = await ExecuteDeleteAsync(
            connection,
            transaction,
            "DELETE FROM container_transactions WHERE occurred_at_ms < $cutoff;",
            cutoffMs,
            cancellationToken).ConfigureAwait(false);
        var deletedRollbackJobs = await ExecuteDeleteAsync(
            connection,
            transaction,
            $"""
            DELETE FROM rollback_jobs
            WHERE state_code IN ({BuildStateList()})
              AND COALESCE(completed_at_ms, created_at_ms) < $cutoff;
            """,
            cutoffMs,
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        var checkpointStarted = DateTimeOffset.UtcNow;
        var checkpointResult = await RunCheckpointAsync(checkpointMode, cancellationToken).ConfigureAwait(false);
        var checkpointDuration = DateTimeOffset.UtcNow - checkpointStarted;
        var optimizeDuration = TimeSpan.Zero;

        if (optimizeAfterCheckpoint)
        {
            var optimizeStarted = DateTimeOffset.UtcNow;
            await RunOptimizeAsync(cancellationToken).ConfigureAwait(false);
            optimizeDuration = DateTimeOffset.UtcNow - optimizeStarted;
        }

        return new PurgeResult(
            cutoff,
            deletedBlockEvents,
            deletedContainerTransactions,
            deletedRollbackJobs,
            CheckpointRan: true,
            OptimizeRan: optimizeAfterCheckpoint,
            CheckpointMode: checkpointResult.Mode,
            WalSizeBytesBeforeCheckpoint: checkpointResult.WalSizeBytesBefore,
            WalSizeBytesAfterCheckpoint: checkpointResult.WalSizeBytesAfter,
            CheckpointDuration: checkpointDuration,
            OptimizeDuration: optimizeDuration);
    }

    public async Task<SqliteCheckpointResult> RunCheckpointAsync(
        SqliteCheckpointMode mode,
        CancellationToken cancellationToken = default)
    {
        var walBefore = GetWalSizeBytes();

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = mode == SqliteCheckpointMode.Truncate
            ? "PRAGMA wal_checkpoint(TRUNCATE);"
            : "PRAGMA wal_checkpoint(PASSIVE);";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        return new SqliteCheckpointResult(mode, walBefore, GetWalSizeBytes());
    }

    public async Task RunOptimizeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA optimize;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private long GetWalSizeBytes()
    {
        return File.Exists(_connectionFactory.WalPath)
            ? new FileInfo(_connectionFactory.WalPath).Length
            : 0L;
    }

    private static async Task<int> CountAsync(
        SqliteConnection connection,
        string sql,
        long cutoffMs,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$cutoff", cutoffMs);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(result);
    }

    private static async Task<int> CountTerminalRollbackJobsAsync(
        SqliteConnection connection,
        long cutoffMs,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT COUNT(*)
            FROM rollback_jobs
            WHERE state_code IN ({BuildStateList()})
              AND COALESCE(completed_at_ms, created_at_ms) < $cutoff;
            """;
        command.Parameters.AddWithValue("$cutoff", cutoffMs);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(result);
    }

    private static async Task<int> ExecuteDeleteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        long cutoffMs,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$cutoff", cutoffMs);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string BuildStateList()
    {
        return string.Join(", ", TerminalRollbackStates.Select(static state => $"'{state}'"));
    }
}
