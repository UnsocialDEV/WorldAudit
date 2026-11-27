using System.Diagnostics;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using WorldAudit.Application.Configuration;
using WorldAudit.Application.Services;
using WorldAudit.Infrastructure.Persistence;
using WorldAudit.Mod;
using WorldAudit.Presentation.Chat;
using WorldAudit.Presentation.Commands;
using WorldAudit.Tests.TestSupport;

namespace WorldAudit.Tests;

public sealed class WorldAuditAdminCommandHandlerTests
{
    [Theory]
    [InlineData("t:1s", "Total candidates: 0")]
    [InlineData("confirm t:1s", "Purged 0 record(s)")]
    public async Task Purge_ReturnsWhileDatabaseIsLocked_AndRepliesOnMainThread(string query, string expected)
    {
        var options = new WorldAuditOptions { DatabasePath = Path.Combine(Path.GetTempPath(), $"wa-admin-{Guid.NewGuid():N}.db") };
        await using var runtime = await WorldAuditRuntime.CreateAsync(options);
        var reply = new TaskCompletionSource<Action>(TaskCreationOptions.RunContinuationsAsynchronously);
        var messages = new List<string>();
        var logger = ProxyFactory.Create<ILogger>(_ => { });
        var events = ProxyFactory.Create<IServerEventAPI>(proxy => proxy.SetHandler("EnqueueMainThreadTask", args =>
        {
            reply.TrySetResult((Action)args![0]!);
            return null;
        }));
        var api = ProxyFactory.Create<ICoreServerAPI>(proxy =>
        {
            proxy.SetValue(nameof(ICoreServerAPI.Logger), logger);
            proxy.SetValue(nameof(ICoreServerAPI.Event), events);
        });
        var player = ProxyFactory.Create<IServerPlayer>(proxy => proxy.SetHandler("SendMessage", args =>
        {
            Assert.Equal(23, args![0]);
            messages.Add(ChatText.Read((string)args[1]!));
            return null;
        }));
        var context = new WorldAuditCommandContext(api, () => runtime, () => new WorldAuditModConfig(),
            new LookupCommandParser(), new PurgeCommandParser(), new InspectorStateService(),
            () => new ChatAuditFormatter(), () => "main", () => (true, ""), _ => 23);
        var handler = new WorldAuditAdminCommandHandler(context);
        var parser = ProxyFactory.Create<ICommandArgumentParser>(proxy =>
        {
            proxy.SetValue(nameof(ICommandArgumentParser.ArgCount), 1);
            proxy.SetHandler("GetValue", _ => query);
        });
        var args = new TextCommandCallingArgs { Caller = new Caller { Player = player }, Parsers = [parser] };
        await using var connection = await new SqliteConnectionFactory(options).OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = (Microsoft.Data.Sqlite.SqliteTransaction)transaction;
            command.CommandText = "DELETE FROM block_events;";
            await command.ExecuteNonQueryAsync();
        }

        var timer = Stopwatch.StartNew();
        var result = handler.HandlePurge(args);
        Assert.Equal(EnumCommandStatus.Deferred, result.Status);
        Assert.True(timer.Elapsed < TimeSpan.FromMilliseconds(250));
        Assert.Equal(EnumCommandStatus.Error, handler.HandlePurge(args).Status);
        await Task.Delay(100);
        Assert.False(reply.Task.IsCompleted);
        Assert.Empty(messages);

        await transaction.RollbackAsync();
        var sendReply = await reply.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Empty(messages);
        sendReply();
        Assert.Contains(messages, line => line.Contains(expected));
        Assert.DoesNotContain(messages, line => line.Contains("failed:"));
    }

    [Fact]
    public async Task UnreadableDatabase_StillReportsLostEventsAndFinishesFlushAndShutdown()
    {
        var options = new WorldAuditOptions { DatabasePath = Path.Combine(Path.GetTempPath(), $"wa-offline-{Guid.NewGuid():N}.db") };
        var runtime = await WorldAuditRuntime.CreateAsync(options);
        await using (var connection = await new SqliteConnectionFactory(options).OpenConnectionAsync())
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearPool(connection);
        }
        // The disposable file is no longer a readable SQLite database.
        await File.WriteAllTextAsync(options.DatabasePath, new string('x', 4096));
        var change = WorldAudit.Integration.BlockChangeCaptureContext.PlayerBroke("main", new(1, 64, 1),
            DateTimeOffset.UtcNow, "Player", "uid", "game:stone");
        await runtime.BlockCapture.CaptureAsync(change);
        var flushFailure = await Record.ExceptionAsync(() => runtime.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.NotNull(flushFailure);
        Assert.IsNotType<TimeoutException>(flushFailure);
        var status = await runtime.GetStatusAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var lines = WorldAuditCommandText.FormatStatus(status).ToArray();
        Assert.Contains(lines, line => line.Contains("Storage unavailable"));
        Assert.Contains(lines, line => line.Contains("Consumer: faulted"));
        Assert.Contains(lines, line => line.Contains("1 record(s) lost"));
        Assert.Contains(lines, line => line.Contains("WAL threshold unavailable"));
        var mod = new WorldAuditModSystem();
        const System.Reflection.BindingFlags lifecycle = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        typeof(WorldAuditModSystem).GetField("_runtime", lifecycle)!.SetValue(mod, runtime);
        await Task.Run(() => typeof(WorldAuditModSystem).GetMethod("OnGameWorldSave", lifecycle)!.Invoke(mod, null))
            .WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Run(mod.Dispose).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Status_ShowsFaultedWriterAndItsError()
    {
        var options = new WorldAuditOptions { DatabasePath = Path.Combine(Path.GetTempPath(), $"wa-status-{Guid.NewGuid():N}.db") };
        await using var runtime = await WorldAuditRuntime.CreateAsync(options);
        var status = await runtime.GetStatusAsync();
        var lines = WorldAuditCommandText.FormatStatus(status with
        {
            WriterSnapshot = status.WriterSnapshot with { LastError = "database or disk is full" }
        }).ToArray();
        Assert.Contains(lines, line => line.Contains("Consumer: faulted"));
        Assert.Contains(lines, line => line.Contains("Writer error: database or disk is full"));
    }
}
