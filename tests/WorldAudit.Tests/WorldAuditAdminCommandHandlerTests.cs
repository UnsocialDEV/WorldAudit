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
    [Fact]
    public async Task PurgePreview_ReturnsWhileDatabaseIsLocked_AndRepliesOnMainThread()
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
            messages.Add((string)args![1]!);
            return null;
        }));
        var context = new WorldAuditCommandContext(api, () => runtime, () => new WorldAuditModConfig(),
            new LookupCommandParser(), new PurgeCommandParser(), new InspectorStateService(),
            () => new ChatAuditFormatter(), () => "main", () => (true, ""));
        var handler = new WorldAuditAdminCommandHandler(context);
        var parser = ProxyFactory.Create<ICommandArgumentParser>(proxy =>
        {
            proxy.SetValue(nameof(ICommandArgumentParser.ArgCount), 1);
            proxy.SetHandler("GetValue", _ => "t:1s");
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
        Assert.Contains(messages, line => line.Contains("Total candidates: 0"));
        Assert.DoesNotContain(messages, line => line.Contains("failed:"));
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
