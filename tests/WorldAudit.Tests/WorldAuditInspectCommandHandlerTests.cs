using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using System.Diagnostics;
using WorldAudit.Application.Configuration;
using WorldAudit.Application.Services;
using WorldAudit.Domain;
using WorldAudit.Integration;
using WorldAudit.Mod;
using WorldAudit.Presentation.Chat;
using WorldAudit.Presentation.Commands;
using WorldAudit.Tests.TestSupport;

namespace WorldAudit.Tests;

public sealed class WorldAuditInspectCommandHandlerTests : IAsyncLifetime
{
    private string _databasePath = null!;
    private WorldAuditRuntime _runtime = null!;

    public async Task InitializeAsync()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"worldaudit-inspect-handler-{Guid.NewGuid():N}.db");
        _runtime = await WorldAuditRuntime.CreateAsync(new WorldAuditOptions
        {
            DatabasePath = _databasePath,
            WriterBatchSize = 2,
            WriterMaxFlushDelay = TimeSpan.FromMilliseconds(25),
            DefaultPageSize = 8
        }).WaitAsync(TimeSpan.FromSeconds(5));
    }

    public async Task DisposeAsync()
    {
        await _runtime.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task SendInspectHistoryAsync_SendsMessagesOnMainThreadWithoutNeedingFullFlush()
    {
        var position = new BlockPos(10, 65, 10);
        var worldPosition = new BlockPosition(position.X, position.Y, position.Z);
        await _runtime.BlockCapture.CaptureAsync(BlockChangeCaptureContext.PlayerPlaced(
            "main",
            worldPosition,
            new DateTimeOffset(2026, 3, 16, 11, 58, 0, TimeSpan.Zero),
            "Dayton",
            "player-1",
            "game:stonebrick"));
        await _runtime.ContainerCapture.CaptureAsync(new ContainerTransactionCaptureContext(
            WorldId: "main",
            Position: worldPosition,
            OccurredAt: new DateTimeOffset(2026, 3, 16, 11, 59, 0, TimeSpan.Zero),
            ActorName: "Dayton",
            ActorExternalId: "player-1",
            Cause: AuditCause.Player,
            InventoryType: "crate",
            ContainerLabel: "game:crate",
            BeforeSnapshot: null,
            AfterSnapshot: null,
            Lines:
            [
                new ContainerTransactionLineCapture("game:torch", 0, 2, 0, 2)
            ]));

        await _runtime.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await _runtime.AuditWriter.QueueBlockEventAsync(new BlockAuditEvent(
            Id: null,
            WorldId: "main",
            Position: worldPosition,
            OccurredAt: new DateTimeOffset(2026, 3, 16, 12, 0, 0, TimeSpan.Zero),
            Actor: new AuditActor("Dayton", "player-1"),
            Cause: AuditCause.Player,
            Action: BlockAuditAction.Place,
            OldBlockCode: "game:air",
            NewBlockCode: "game:temporalgear"));
        await _runtime.AuditWriter.QueueContainerTransactionAsync(new ContainerAuditTransaction(
            Id: null,
            WorldId: "main",
            Position: worldPosition,
            OccurredAt: new DateTimeOffset(2026, 3, 16, 12, 0, 1, TimeSpan.Zero),
            Actor: new AuditActor("Dayton", "player-1"),
            Cause: AuditCause.Player,
            InventoryType: "crate",
            ContainerLabel: "game:crate",
            BeforeSnapshot: null,
            AfterSnapshot: null,
            Lines:
            [
                new ContainerAuditLine(null, "game:gear-rusty", 0, 1, 0, 1)
            ]));

        var mainThreadCodes = new List<string>();
        var sentMessages = new List<string>();
        var inspectIndicatorMessages = new List<string>();
        var api = CreateServerApi(mainThreadCodes, inspectIndicatorMessages);
        var context = new WorldAuditCommandContext(
            api,
            () => _runtime,
            () => new WorldAuditModConfig(),
            new LookupCommandParser(),
            new PurgeCommandParser(),
            new InspectorStateService(),
            () => new ChatAuditFormatter(),
            () => "main",
            () => (true, string.Empty));
        var handler = new WorldAuditInspectCommandHandler(context);
        var player = CreateServerPlayer("player-1", "Dayton", sentMessages);
        var started = Stopwatch.StartNew();

        await handler.SendInspectHistoryAsync(player, position).WaitAsync(TimeSpan.FromSeconds(5));
        started.Stop();

        Assert.Contains(mainThreadCodes, code => code == "worldaudit-inspect-history");
        Assert.Contains(inspectIndicatorMessages, message => message == "[WorldAudit] Inspection mode enabled");
        Assert.Contains(sentMessages, message => message.Contains("Dayton", StringComparison.Ordinal));
        Assert.Contains(sentMessages, message => message.Contains("Crate changed", StringComparison.OrdinalIgnoreCase));
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(1));
    }

    private static ICoreServerAPI CreateServerApi(List<string> mainThreadCodes, List<string> inspectIndicatorMessages)
    {
        var logger = ProxyFactory.Create<ILogger>(proxy => { });
        var world = ProxyFactory.Create<IWorldAccessor>(proxy =>
        {
            proxy.SetValue(nameof(IWorldAccessor.DefaultSpawnPosition), new EntityPos(0, 0, 0));
        });
        var events = ProxyFactory.Create<IServerEventAPI>(proxy =>
        {
            proxy.SetHandler("EnqueueMainThreadTask", args =>
            {
                mainThreadCodes.Add((string)args![1]!);
                ((Action)args[0]!).Invoke();
                return null;
            });
        });

        return ProxyFactory.Create<ICoreServerAPI>(proxy =>
        {
            proxy.SetValue(nameof(ICoreServerAPI.Logger), logger);
            proxy.SetValue(nameof(ICoreServerAPI.World), world);
            proxy.SetValue(nameof(ICoreServerAPI.Event), events);
            proxy.SetHandler("SendIngameError", args =>
            {
                inspectIndicatorMessages.Add((string)args![2]!);
                return null;
            });
        });
    }

    private static IServerPlayer CreateServerPlayer(string playerUid, string playerName, List<string> sentMessages)
    {
        return ProxyFactory.Create<IServerPlayer>(proxy =>
        {
            proxy.SetValue(nameof(IServerPlayer.PlayerUID), playerUid);
            proxy.SetValue(nameof(IServerPlayer.PlayerName), playerName);
            proxy.SetHandler("SendMessage", args =>
            {
                sentMessages.Add((string)args![1]!);
                return null;
            });
        });
    }
}
