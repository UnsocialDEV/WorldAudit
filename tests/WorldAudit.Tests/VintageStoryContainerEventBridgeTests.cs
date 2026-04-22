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
    public void SlotModified_AfterDidUseBlock_UsesRecentPlayerInteraction()
    {
        var inventory = CreateInventory("inventory-1", "inventory-generic", new BlockPos(5, 65, 5));
        var blockCode = new AssetLocation("game", "groundstorage");
        var container = new TestBlockEntityContainer(inventory, "groundstorage");
        var api = CreateServerApi(container, blockCode);
        var capture = new FakeContainerCapture();
        var bridge = new VintageStoryContainerEventBridge(
            api,
            capture,
            () => true,
            () => "main",
            _ => false,
            new VintageStoryInteractionAttributionTracker());
        var player = CreateServerPlayer("player-1", "Dayton");
        var selection = new BlockSelection { Position = new BlockPos(5, 65, 5) };

        InvokeDidUseBlock(bridge, player, selection);
        inventory[0].Itemstack = CreateStack("game:torch", 1);
        InvokeOnSlotModified(bridge, inventory, 0);

        var context = Assert.Single(capture.Contexts);
        Assert.Equal("Dayton", context.ActorName);
        Assert.Equal("player-1", context.ActorExternalId);
        Assert.Equal(AuditCause.Player, context.Cause);
        Assert.Equal("groundstorage", context.InventoryType);
        Assert.Equal("game:groundstorage", context.ContainerLabel);
        Assert.Single(context.Lines);
        Assert.Equal("game:torch", context.Lines[0].ItemCode);
        Assert.Equal(1, context.Lines[0].QuantityDelta);
    }

    [Fact]
    public void SlotModified_WithConflictingRecentInteractions_FallsBackToUnknown()
    {
        var inventory = CreateInventory("inventory-2", "inventory-generic", new BlockPos(9, 65, 9));
        var blockCode = new AssetLocation("game", "toolrack");
        var container = new TestBlockEntityContainer(inventory, "toolrack");
        var api = CreateServerApi(container, blockCode);
        var capture = new FakeContainerCapture();
        var clock = DateTimeOffset.UtcNow;
        var tracker = new VintageStoryInteractionAttributionTracker(() => clock);
        var bridge = new VintageStoryContainerEventBridge(api, capture, () => true, () => "main", _ => false, tracker);

        InvokeDidUseBlock(bridge, CreateServerPlayer("player-1", "First"), new BlockSelection { Position = new BlockPos(9, 65, 9) });
        clock = clock.AddMilliseconds(100);
        InvokeDidUseBlock(bridge, CreateServerPlayer("player-2", "Second"), new BlockSelection { Position = new BlockPos(9, 65, 9) });
        inventory[0].Itemstack = CreateStack("game:hammer", 1);
        InvokeOnSlotModified(bridge, inventory, 0);

        var context = Assert.Single(capture.Contexts);
        Assert.Equal("#unknown", context.ActorName);
        Assert.Null(context.ActorExternalId);
        Assert.Equal(AuditCause.Unknown, context.Cause);
    }

    [Fact]
    public void DidUseBlock_InspectMode_DoesNotStartContainerTracking()
    {
        var inventory = CreateInventory("inventory-3", "inventory-generic", new BlockPos(5, 65, 5));
        var blockCode = new AssetLocation("game", "chest");
        var container = new TestBlockEntityContainer(inventory, "chest");
        var api = CreateServerApi(container, blockCode);
        var capture = new FakeContainerCapture();
        var bridge = new VintageStoryContainerEventBridge(
            api,
            capture,
            () => true,
            () => "main",
            _ => true,
            new VintageStoryInteractionAttributionTracker());
        var player = CreateServerPlayer("player-1", "Dayton");
        var selection = new BlockSelection { Position = new BlockPos(5, 65, 5) };

        InvokeDidUseBlock(bridge, player, selection);
        inventory[0].Itemstack = CreateStack("game:torch", 1);
        InvokeOnSlotModified(bridge, inventory, 0);

        Assert.Empty(capture.Contexts);
    }

    private static void InvokeDidUseBlock(VintageStoryContainerEventBridge bridge, IServerPlayer player, BlockSelection selection)
    {
        var method = typeof(VintageStoryContainerEventBridge).GetMethod("OnDidUseBlock", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("OnDidUseBlock was not found.");
        method.Invoke(bridge, [player, selection]);
    }

    private static void InvokeOnSlotModified(VintageStoryContainerEventBridge bridge, InventoryBase inventory, int slotId)
    {
        var method = typeof(VintageStoryContainerEventBridge).GetMethod("OnSlotModified", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("OnSlotModified was not found.");
        method.Invoke(bridge, [inventory, slotId]);
    }

    private static InventoryGeneric CreateInventory(string inventoryId, string className, BlockPos position)
    {
        var api = ProxyFactory.Create<ICoreAPI>(proxy =>
        {
            proxy.SetValue(nameof(ICoreAPI.World), CreateWorldAccessor(null, new AssetLocation("game", "air")));
            proxy.SetValue(nameof(ICoreAPI.ClassRegistry), CreateClassRegistry());
        });
        var inventory = new InventoryGeneric(4, inventoryId, className, api, static (_, inv) => new ItemSlot(inv))
        {
            Pos = position
        };
        return inventory;
    }

    private static IClassRegistryAPI CreateClassRegistry()
    {
        return ProxyFactory.Create<IClassRegistryAPI>(proxy =>
        {
            proxy.SetHandler("CreateInvNetworkUtil", _ => ProxyFactory.Create<IInventoryNetworkUtil>(inner => { }));
        });
    }

    private static ICoreServerAPI CreateServerApi(BlockEntity container, AssetLocation blockCode)
    {
        var world = CreateWorldAccessor(container, blockCode);
        var logger = ProxyFactory.Create<ILogger>(proxy => { });

        return ProxyFactory.Create<ICoreServerAPI>(proxy =>
        {
            proxy.SetValue(nameof(ICoreServerAPI.World), world);
            proxy.SetValue(nameof(ICoreServerAPI.Logger), logger);
        });
    }

    private static IServerWorldAccessor CreateWorldAccessor(BlockEntity? container, AssetLocation blockCode)
    {
        var block = new Block
        {
            Code = blockCode
        };

        var blockAccessor = ProxyFactory.Create<IBlockAccessor>(proxy =>
        {
            proxy.SetHandler("GetBlockEntity", _ => container!);
            proxy.SetHandler("GetBlock", _ => block);
        });

        return ProxyFactory.Create<IServerWorldAccessor>(proxy =>
        {
            proxy.SetValue(nameof(IWorldAccessor.BlockAccessor), blockAccessor);
        });
    }

    private static IServerPlayer CreateServerPlayer(string playerUid, string playerName)
    {
        return ProxyFactory.Create<IServerPlayer>(proxy =>
        {
            proxy.SetValue(nameof(IServerPlayer.PlayerUID), playerUid);
            proxy.SetValue(nameof(IServerPlayer.PlayerName), playerName);
        });
    }

    private static ItemStack CreateStack(string code, int quantity)
    {
        var block = new Block
        {
            Code = new AssetLocation(code)
        };
        return new ItemStack(block, quantity);
    }

    private sealed class FakeContainerCapture : IContainerTransactionCapture
    {
        public List<ContainerTransactionCaptureContext> Contexts { get; } = [];

        public void Capture(ContainerTransactionCaptureContext context)
        {
            Contexts.Add(context);
        }

        public ValueTask CaptureAsync(ContainerTransactionCaptureContext context, CancellationToken cancellationToken = default)
        {
            Contexts.Add(context);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestBlockEntityContainer : BlockEntity, IBlockEntityContainer
    {
        public TestBlockEntityContainer(IInventory inventory, string inventoryClassName)
        {
            Inventory = inventory;
            InventoryClassName = inventoryClassName;
        }

        public IInventory Inventory { get; }

        public string InventoryClassName { get; }

        public void DropContents(Vec3d atPos)
        {
        }

        public void CheckInventoryClearedMidTick()
        {
        }
    }
}
