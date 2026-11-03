using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;
using WorldAudit.Application.Abstractions;
using WorldAudit.Application.Configuration;
using WorldAudit.Application.Services;
using WorldAudit.Domain;
using WorldAudit.Integration;
using WorldAudit.Integration.VintageStory;
using WorldAudit.Tests.TestSupport;

namespace WorldAudit.Tests;

[CollectionDefinition("Game hooks", DisableParallelization = true)]
public sealed class GameHooksCollection;

[Collection("Game hooks")]
public sealed class VintageStoryGameContractTests
{
    [Fact]
    public void AllRequiredHooksInstallOnActual1227AssembliesAndDetach()
    {
        // Force the real gameplay assemblies to load before resolving patch targets.
        Assert.NotNull(typeof(BlockEntityChisel).Assembly);
        Assert.NotNull(typeof(Vintagestory.Server.ServerPlayer).Assembly);
        var fixture = new WorldFixture();
        var api = ProxyFactory.Create<ICoreServerAPI>(p =>
        {
            p.SetValue("World", fixture.World);
            p.SetValue("Logger", ProxyFactory.Create<ILogger>(_ => { }));
        });
        var hooks = new VintageStoryGameHooks(api, fixture.Monitor);
        try
        {
            hooks.Register();
            // A failed reload can dispose a replacement before it was registered.
            new VintageStoryGameHooks(api, fixture.Monitor).Dispose();
            Assert.True(VintageStoryGameHooks.Active);
            foreach (var (type, name) in new[]
            {
                ("Vintagestory.Common.BlockAccessorBase", "SetBlockInternal"),
                ("Vintagestory.Common.BlockAccessorRelaxed", "ExchangeBlock"),
                ("Vintagestory.GameContent.BlockEntityChisel", "OnReceivedClientPacket"),
                ("Vintagestory.GameContent.BEBehaviorDoor", "ToggleDoorWing"),
                ("Vintagestory.GameContent.BlockBehaviorDoor", "getDoorAt"),
                ("Vintagestory.Server.ServerSystemInventory", "HandleHandInteraction"),
                ("Vintagestory.Server.ServerSystemInventory", "callOnUsing"),
                ("Vintagestory.Common.BlockAccessorRelaxedBulkUpdate", "Commit"),
                ("Vintagestory.Common.BlockAccessorBulkMinimalUpdate", "FastCommit"),
                ("Vintagestory.Server.ServerMain", "CreateExplosion")
            })
                Assert.Contains("worldaudit.1.22.7", Harmony.GetPatchInfo(AccessTools.Method(AccessTools.TypeByName(type), name)).Owners);
        }
        finally { hooks.Dispose(); }
        Assert.False(VintageStoryGameHooks.Active);
    }

    [Fact]
    public void MultiblockOperationRetainsPlayerAndCompletedEntityInitialization()
    {
        var f = new WorldFixture();
        var lower = new BlockPos(10, 65, 10);
        var upper = lower.UpCopy();
        using (f.Monitor.Begin(BlockMutationScope.Player("Alice", "alice")))
        {
            f.Monitor.Touch(lower);
            f.Set(lower, 1, "game:door");
            // Actual Block.OnBlockPlaced spawns its BE before behaviors; door setup continues afterward.
            var block = new Block { Code = new AssetLocation("game:door"), EntityClass = "test", BlockBehaviors = [] };
            block.OnBlockPlaced(f.World, lower);
            f.Entity!.Value = 42;
            using (f.Monitor.Begin())
            {
                f.Monitor.Touch(upper);
                f.Set(upper, 1, "game:multiblock-monolithic-0-p1-0");
            }
            Assert.Empty(f.Sink.Events);
        }
        Assert.Equal(2, f.Sink.Events.Count);
        Assert.All(f.Sink.Events, e => Assert.Equal("alice", e.AmbientScope?.ActorExternalId));
        var tree = new TreeAttribute();
        tree.FromBytes(f.Sink.Events.Single(e => e.Position.Y == 65).NewBlockEntitySnapshot);
        Assert.Equal(42, tree.GetInt("value"));
        Assert.Null(f.Scopes.GetAmbientScope());
    }

    [Fact]
    public void ExchangeAndEntityOnlyEditsAreCapturedWithoutBlockBehaviorCallbacks()
    {
        var f = new WorldFixture();
        var pos = new BlockPos(12, 65, 10);
        f.Set(pos, 1, "game:chiseledblock");
        f.Entity = new StateEntity { Pos = pos, Value = 1 };
        using (f.Monitor.Begin(BlockMutationScope.Player("Alice", "alice")))
        {
            f.Monitor.Touch(pos);
            f.Entity.Value = 2;
        }
        var edit = Assert.Single(f.Sink.Events);
        Assert.Equal(edit.OldBlockCode, edit.NewBlockCode);
        Assert.NotEqual(edit.OldBlockEntitySnapshot, edit.NewBlockEntitySnapshot);
        f.Sink.Events.Clear();
        using (f.Monitor.Begin(BlockMutationScope.Player("Alice", "alice")))
        {
            f.Monitor.Touch(pos);
            f.Set(pos, 1, "game:trapdoor-open");
        }
        Assert.Equal("game:trapdoor-open", Assert.Single(f.Sink.Events).NewBlockCode);
    }

    [Fact]
    public void FluidRemovalRecordsOldFluidAndItsLayerWithoutTouchingSolid()
    {
        var f = new WorldFixture();
        var pos = new BlockPos(10, 65, 10, 2);
        f.Set(pos, 1, "game:fence");
        f.Set(pos, 2, "game:water-still-7");
        using (f.Monitor.Begin(BlockMutationScope.Synthetic(AuditCause.Liquid)))
        {
            f.Monitor.Touch(pos);
            f.Set(pos, 2, "game:air");
        }
        var mutation = Assert.Single(f.Sink.Events);
        Assert.Equal(BlockAuditAction.Break, mutation.Action);
        Assert.Equal("game:water-still-7", mutation.OldBlockCode);
        Assert.Equal(2, mutation.Layer);
        Assert.Equal(2, mutation.Position.Dimension);
        Assert.Equal(pos, new BlockPos(mutation.Position.X, mutation.Position.Y, mutation.Position.Z));
    }

    [Fact]
    public void DimensionScopesAndGuardsDoNotOverlap()
    {
        var a = new BlockPos(1, 65, 1, 0);
        var b = new BlockPos(1, 65, 1, 1);
        var tracker = new VintageStoryInteractionAttributionTracker();
        tracker.Record("save", a, "Alice", "alice");
        Assert.False(tracker.TryResolve("save", b, out _));
        var guard = new WorldMutationGuard();
        using var lease = guard.Begin("save", new BlockPosition(a.X, a.InternalY, a.Z));
        Assert.False(guard.IsActive("save", new BlockPosition(b.X, b.InternalY, b.Z)));
    }

    [Theory]
    [InlineData(AuditCause.Explosion)]
    [InlineData(AuditCause.Fire)]
    [InlineData(AuditCause.Gravity)]
    [InlineData(AuditCause.Liquid)]
    [InlineData(AuditCause.Decay)]
    [InlineData(AuditCause.System)]
    [InlineData(AuditCause.Worldgen)]
    public void ExplicitCauseRetainsProvenPlayerIdentity(AuditCause cause)
    {
        var classifier = new BlockMutationClassifierChain(new WorldAuditOptions());
        var result = classifier.Classify(new BlockMutationObservation("save", new(1, 65, 1), DateTimeOffset.UtcNow,
            BlockAuditAction.Break, "game:rock-sandstone", "game:air", AmbientScope: new BlockMutationScope("Alice", "alice", cause)));
        Assert.Equal(cause, result.Cause);
        Assert.Equal("alice", result.ActorExternalId);
        Assert.False(result.IsSynthetic);
    }

    private sealed class WorldFixture
    {
        internal readonly BlockMutationScopeManager Scopes = new();
        internal readonly Sink Sink = new();
        internal readonly IWorldAccessor World;
        internal readonly VintageStoryMutationMonitor Monitor;
        internal StateEntity? Entity;
        private readonly Dictionary<(int, int, int, int), Block> _blocks = new();
        internal WorldFixture()
        {
            var accessor = ProxyFactory.Create<IBlockAccessor>(p =>
            {
                p.SetHandler("GetBlock", args =>
                {
                    var pos = (BlockPos)args![0]!;
                    var layer = args.Length > 1 && args[1] is int l ? l : 1;
                    return _blocks.GetValueOrDefault((pos.X, pos.InternalY, pos.Z, layer)) ?? new Block { Code = new AssetLocation("game:air") };
                });
                p.SetHandler("GetBlockEntity", args => Entity?.Pos.Equals(args![0]) == true ? Entity : null);
                p.SetHandler("SpawnBlockEntity", args => { Entity = new StateEntity { Pos = ((BlockPos)args![1]!).Copy() }; return null; });
            });
            World = ProxyFactory.Create<IWorldAccessor>(p => p.SetValue("BlockAccessor", accessor));
            Monitor = new VintageStoryMutationMonitor(World, Sink, Scopes, () => "save", () => true, e => throw e);
        }
        internal void Set(BlockPos pos, int layer, string code) => _blocks[(pos.X, pos.InternalY, pos.Z, layer)] = new Block { Code = new AssetLocation(code) };
    }
    private sealed class StateEntity : BlockEntity
    {
        internal int Value;
        public override void ToTreeAttributes(ITreeAttribute tree) => tree.SetInt("value", Value);
    }
    private sealed class Sink : IBlockMutationSink
    {
        internal readonly List<BlockMutationObservation> Events = [];
        public void Observe(BlockMutationObservation observation) => Events.Add(observation);
        public ValueTask ObserveAsync(BlockMutationObservation observation, CancellationToken cancellationToken = default) { Observe(observation); return ValueTask.CompletedTask; }
    }
}
