using System.Reflection;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using WorldAudit.Domain;
using WorldAudit.Integration;
using WorldAudit.Integration.VintageStory;
using WorldAudit.Tests.TestSupport;

namespace WorldAudit.Tests;

public sealed class VintageStoryContainerEventBridgeTests
{
    [Fact]
    public void SlotModified_UsesExecutingPlayerDespiteAnotherPlayersClickAndOpenSession()
    {
        using var fixture = new ContainerTestFixture();
        var alice = ContainerTestFixture.Player("alice", "Alice");
        var bob = ContainerTestFixture.Player("bob", "Bob");
        fixture.Bridge.TrackAt(fixture.Position);
        fixture.Inventory.Open(alice);
        fixture.Inventory.Open(bob);
        DidUse(fixture.Bridge, bob, fixture.Position);

        using (fixture.Scopes.BeginAmbientScope(BlockMutationScope.Player("Alice", "alice")))
            fixture.Change(0, fixture.Stack("game:torch", 1));
        fixture.Inventory.Close(alice);
        fixture.Inventory.Close(bob);

        var context = Assert.Single(fixture.Capture.Contexts);
        Assert.Equal("Alice", context.ActorName);
        Assert.Equal("alice", context.ActorExternalId);
        Assert.Equal(AuditCause.Player, context.Cause);
        Assert.Equal(1, Assert.Single(context.Lines).QuantityDelta);
    }

    [Fact]
    public void SlotModified_WithoutExecutingPlayer_DoesNotBlameRecentClickOrSoleOpener()
    {
        using var fixture = new ContainerTestFixture();
        var bob = ContainerTestFixture.Player("bob", "Bob");
        DidUse(fixture.Bridge, bob, fixture.Position);
        fixture.Inventory.Open(bob);

        fixture.Change(0, fixture.Stack("game:hammer", 1));
        fixture.Inventory.Close(bob);

        var context = Assert.Single(fixture.Capture.Contexts);
        Assert.Equal("#unknown", context.ActorName);
        Assert.Null(context.ActorExternalId);
        Assert.Equal(AuditCause.Unknown, context.Cause);
    }

    [Fact]
    public void TrackAt_BeforeFirstDirectUse_CapturesInitialAdditionBeforeDidUseBlock()
    {
        using var fixture = new ContainerTestFixture("groundstorage");
        fixture.Bridge.TrackAt(fixture.Position);
        using (fixture.Scopes.BeginAmbientScope(BlockMutationScope.Player("Alice", "alice")))
            fixture.Change(0, fixture.Stack("game:torch", 1));
        DidUse(fixture.Bridge, ContainerTestFixture.Player("alice", "Alice"), fixture.Position);

        var context = Assert.Single(fixture.Capture.Contexts);
        Assert.Equal("groundstorage", context.InventoryType);
        Assert.Equal("game:groundstorage", context.ContainerLabel);
        var line = Assert.Single(context.Lines);
        Assert.Equal(0, line.BeforeQuantity);
        Assert.Equal(1, line.AfterQuantity);
        Assert.Null(line.StackBeforeSnapshot);
        Assert.NotNull(line.StackAfterSnapshot);
    }

    [Fact]
    public void TrackAt_LastItemRemoval_CapturesEvenWhenBlockEntityHasDisappeared()
    {
        using var fixture = new ContainerTestFixture("groundstorage");
        fixture.Inventory[0].Itemstack = fixture.Stack("game:torch", 1);
        fixture.Bridge.TrackAt(fixture.Position);
        fixture.Container = null;
        using (fixture.Scopes.BeginAmbientScope(BlockMutationScope.Player("Alice", "alice")))
            fixture.Change(0, null);
        DidUse(fixture.Bridge, ContainerTestFixture.Player("alice", "Alice"), fixture.Position);

        var context = Assert.Single(fixture.Capture.Contexts);
        Assert.Equal("alice", context.ActorExternalId);
        Assert.Equal(new BlockPosition(5, 65, 5), context.Position);
        var line = Assert.Single(context.Lines);
        Assert.Equal(-1, line.QuantityDelta);
        Assert.Equal("game:torch", line.ItemCode);
        Assert.NotNull(line.StackBeforeSnapshot);
        Assert.Null(line.StackAfterSnapshot);
    }

    [Fact]
    public void TrackInventory_ReplacementWithSameId_DetachesOldAndCapturesNewInstance()
    {
        using var fixture = new ContainerTestFixture();
        var original = fixture.Inventory;
        original[0].Itemstack = fixture.Stack("game:stone", 9);
        fixture.Bridge.TrackInventory(original);
        var replacement = fixture.NewInventory();
        replacement[0].Itemstack = fixture.Stack("game:torch", 2);
        Assert.Equal(original.InventoryID, replacement.InventoryID);
        fixture.Bridge.TrackInventory(replacement);
        // A late unload notification for the old instance must not detach the replacement.
        fixture.Bridge.UntrackInventory(original);

        original[0].Itemstack = fixture.Stack("game:stone", 8);
        original.DidModifyItemSlot(original[0]);
        replacement[0].Itemstack = fixture.Stack("game:torch", 3);
        replacement.DidModifyItemSlot(replacement[0]);

        Assert.Same(replacement, Assert.Single(fixture.Bridge.TrackedInventories));
        var line = Assert.Single(Assert.Single(fixture.Capture.Contexts).Lines);
        Assert.Equal("game:torch", line.ItemCode);
        Assert.Equal(2, line.BeforeQuantity);
        Assert.Equal(3, line.AfterQuantity);
    }

    [Fact]
    public void Unregister_DetachesSlotOpenAndCloseHandlers()
    {
        using var fixture = new ContainerTestFixture();
        var player = ContainerTestFixture.Player("alice", "Alice");
        fixture.Bridge.TrackInventory(fixture.Inventory);
        fixture.Inventory.Open(player);
        fixture.Bridge.Unregister();

        fixture.Change(0, fixture.Stack("game:torch", 1));
        fixture.Inventory.Close(player);
        fixture.Inventory.Open(player);
        fixture.Change(0, fixture.Stack("game:torch", 2));

        Assert.Empty(fixture.Capture.Contexts);
        Assert.Empty(fixture.Bridge.TrackedInventories);
    }

    [Fact]
    public void SlotModified_DifferentItemsWithEqualCounts_RecordsRemovalAndAddition()
    {
        using var fixture = new ContainerTestFixture();
        fixture.Inventory[0].Itemstack = fixture.Stack("game:diamond", 10);
        fixture.Bridge.TrackInventory(fixture.Inventory);

        fixture.Change(0, fixture.Stack("game:dirt", 10));

        var context = Assert.Single(fixture.Capture.Contexts);
        Assert.Collection(context.Lines,
            removal =>
            {
                Assert.Equal("game:diamond", removal.ItemCode);
                Assert.Equal(0, removal.SlotId);
                Assert.Equal(-10, removal.QuantityDelta);
                Assert.Equal(10, removal.BeforeQuantity);
                Assert.Equal(0, removal.AfterQuantity);
                Assert.NotNull(removal.StackBeforeSnapshot);
                Assert.Null(removal.StackAfterSnapshot);
            },
            addition =>
            {
                Assert.Equal("game:dirt", addition.ItemCode);
                Assert.Equal(0, addition.SlotId);
                Assert.Equal(10, addition.QuantityDelta);
                Assert.Equal(0, addition.BeforeQuantity);
                Assert.Equal(10, addition.AfterQuantity);
                Assert.Null(addition.StackBeforeSnapshot);
                Assert.NotNull(addition.StackAfterSnapshot);
            });
    }

    [Fact]
    public void DidUseBlock_InspectMode_DoesNotStartContainerTracking()
    {
        using var fixture = new ContainerTestFixture(inspecting: true);
        DidUse(fixture.Bridge, ContainerTestFixture.Player("alice", "Alice"), fixture.Position);
        fixture.Change(0, fixture.Stack("game:torch", 1));

        Assert.Empty(fixture.Capture.Contexts);
        Assert.Empty(fixture.Bridge.TrackedInventories);
    }

    private static void DidUse(VintageStoryContainerEventBridge bridge, IServerPlayer player, BlockPos position)
        => typeof(VintageStoryContainerEventBridge)
            .GetMethod("OnDidUseBlock", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(bridge, [player, new BlockSelection { Position = position }]);
}

internal sealed class ContainerTestFixture : IDisposable
{
    private readonly Dictionary<string, Block> _blocks = new();
    private readonly Dictionary<int, Item> _items = new();
    private readonly string _containerType;
    private readonly List<string> _errors = [];
    internal BlockPos Position { get; } = new(5, 65, 5);
    internal InventoryGeneric Inventory { get; }
    internal TestContainer? Container { get; set; }
    internal ICoreServerAPI Api { get; }
    internal IServerWorldAccessor World { get; }
    internal double GameHours { get; set; } = 100;
    internal BlockMutationScopeManager Scopes { get; } = new();
    internal CaptureSink Capture { get; } = new();
    internal VintageStoryContainerEventBridge Bridge { get; }

    internal ContainerTestFixture(string containerType = "chest", bool inspecting = false,
        IContainerTransactionCapture? capture = null)
    {
        _containerType = containerType;
        var logger = ProxyFactory.Create<ILogger>(p => p.SetHandler("Error", args =>
        {
            _errors.Add(string.Join(" ", args ?? []));
            return null;
        }));
        var accessor = ProxyFactory.Create<IBlockAccessor>(p =>
        {
            p.SetHandler("GetBlockEntity", _ => Container);
            p.SetHandler("GetBlock", _ => Block("game:" + _containerType));
            p.SetHandler("GetChunkAtBlockPos", _ => ProxyFactory.Create<IWorldChunk>(_ => { }));
        });
        World = ProxyFactory.Create<IServerWorldAccessor>(p =>
        {
            p.SetValue(nameof(IWorldAccessor.BlockAccessor), accessor);
            p.SetValue(nameof(IWorldAccessor.Logger), logger);
            p.SetValue(nameof(IWorldAccessor.Side), EnumAppSide.Server);
            p.SetValue(nameof(IWorldAccessor.Rand), new Random(1));
            p.SetValue(nameof(IWorldAccessor.Calendar), ProxyFactory.Create<IGameCalendar>(calendar =>
                calendar.SetHandler("get_TotalHours", _ => GameHours)));
            p.SetHandler("GetBlock", args => args![0] is int id
                ? _blocks.Values.SingleOrDefault(block => block.Id == id)
                : _blocks.GetValueOrDefault(args[0]!.ToString()!));
            p.SetHandler("GetItem", args => args![0] is int id
                ? _items.GetValueOrDefault(id)
                : _items.Values.SingleOrDefault(item => item.Code.ToString() == args[0]!.ToString()));
        });
        Api = ProxyFactory.Create<ICoreServerAPI>(p =>
        {
            p.SetValue(nameof(ICoreAPI.World), World);
            p.SetValue(nameof(ICoreAPI.Logger), logger);
            p.SetValue(nameof(ICoreServerAPI.Event), ProxyFactory.Create<IServerEventAPI>(_ => { }));
            p.SetValue(nameof(ICoreAPI.ClassRegistry), ProxyFactory.Create<IClassRegistryAPI>(registry =>
                registry.SetHandler("CreateInvNetworkUtil", _ => ProxyFactory.Create<IInventoryNetworkUtil>(_ => { }))));
        });
        Inventory = NewInventory();
        Container = new TestContainer(Inventory, containerType);
        Bridge = new VintageStoryContainerEventBridge(Api, capture ?? Capture, () => true,
            () => "main", _ => inspecting, new VintageStoryInteractionAttributionTracker(), Scopes);
    }

    internal InventoryGeneric NewInventory()
        => new(4, "inventory-1", _containerType, Api, static (_, inv) => new ItemSlot(inv))
        {
            Pos = Position.Copy(), AuditLogAccess = false
        };

    internal ItemStack Stack(string code, int quantity) => new(Block(code), quantity);

    internal void Register(Item item) => _items[item.Id] = item;

    private Block Block(string code)
    {
        if (!_blocks.TryGetValue(code, out var block))
            _blocks[code] = block = new Block { BlockId = _blocks.Count + 1, Code = new AssetLocation(code) };
        return block;
    }

    internal void Change(int slotId, ItemStack? stack)
    {
        Inventory[slotId].Itemstack = stack;
        Inventory.DidModifyItemSlot(Inventory[slotId]);
    }

    internal static IServerPlayer Player(string uid, string name)
        => ProxyFactory.Create<IServerPlayer>(p =>
        {
            p.SetValue(nameof(IPlayer.PlayerUID), uid);
            p.SetValue(nameof(IPlayer.PlayerName), name);
        });

    public void Dispose()
    {
        Bridge.Unregister();
        Assert.Empty(_errors);
    }

    internal sealed class CaptureSink : IContainerTransactionCapture
    {
        internal List<ContainerTransactionCaptureContext> Contexts { get; } = [];
        public void Capture(ContainerTransactionCaptureContext context) => Contexts.Add(context);
    }

    internal sealed class TestContainer(IInventory inventory, string className) : BlockEntity, IBlockEntityContainer
    {
        public IInventory Inventory => inventory;
        public string InventoryClassName => className;
        public void DropContents(Vec3d atPos) { }
        public void CheckInventoryClearedMidTick() { }
    }
}
