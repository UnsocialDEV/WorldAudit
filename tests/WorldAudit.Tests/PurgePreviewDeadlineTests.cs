using System.Diagnostics;
using WorldAudit.Application.Configuration;
using WorldAudit.Infrastructure.Persistence;

namespace WorldAudit.Tests;

public sealed class PurgePreviewDeadlineTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Preview_InterruptsLongScan_AndCleansPooledConnection(bool callerCancels)
    {
        var factory = new SqliteConnectionFactory(new WorldAuditOptions
        {
            DatabasePath = Path.Combine(Path.GetTempPath(), $"wa-preview-deadline-{Guid.NewGuid():N}.db")
        });
        await using (var connection = await factory.OpenConnectionAsync())
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE VIEW block_events AS
                WITH RECURSIVE counter(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM counter WHERE x < 1000000000)
                SELECT x AS occurred_at_ms FROM counter;
                CREATE TABLE container_transactions (occurred_at_ms INTEGER);
                CREATE TABLE rollback_jobs (state_code TEXT, completed_at_ms INTEGER, created_at_ms INTEGER);
                """;
            await command.ExecuteNonQueryAsync();
        }

        var repository = new SqliteAuditMaintenanceRepository(factory);
        using var cancellation = new CancellationTokenSource();
        if (callerCancels) cancellation.CancelAfter(TimeSpan.FromMilliseconds(100));
        var timer = Stopwatch.StartNew();
        var read = Task.Run(() => repository.PreviewPurgeAsync(DateTimeOffset.UtcNow, cancellation.Token));
        if (callerCancels)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(TimeSpan.FromSeconds(3)));
        else
            await Assert.ThrowsAsync<TimeoutException>(() => read.WaitAsync(TimeSpan.FromSeconds(13)));
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(callerCancels ? 3 : 13));

        await using (var connection = await factory.OpenConnectionAsync())
        {
            using var command = connection.CreateCommand();
            command.CommandText = "DROP VIEW block_events; CREATE TABLE block_events (occurred_at_ms INTEGER); INSERT INTO block_events VALUES (1);";
            await command.ExecuteNonQueryAsync();
        }
        var preview = await repository.PreviewPurgeAsync(DateTimeOffset.UtcNow);
        Assert.Equal(1, preview.BlockEventCount);
        Assert.Equal(1, (await repository.PreviewPurgeAsync(DateTimeOffset.UtcNow)).BlockEventCount);
    }
}
