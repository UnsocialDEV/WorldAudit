using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using WorldAudit.Application.Abstractions;
using WorldAudit.Application.Services;
using WorldAudit.Domain;
using WorldAudit.Integration;
using WorldAudit.Integration.VintageStory;
using WorldAudit.Tests.TestSupport;

namespace WorldAudit.Tests;

public sealed class VintageStoryRollbackContainerApplierTests
{
    [Fact]
    public async Task ApplyAsync_AfterRealFoodAging_AppliesQuantityChangeAndRetainsCurrentAge()
    {
        using var fixture = new ContainerTestFixture();
        var food = new Item
        {
            Code = new AssetLocation("game:food"),
            TransitionableProps = [new TransitionableProperties
            {
                Type = EnumTransitionType.Perish,
                FreshHours = NatFloat.createUniform(100, 0),
                TransitionHours = NatFloat.createUniform(10, 0)
            }]
        };
        fixture.Register(food);
        fixture.Inventory[0].Itemstack = new ItemStack(food, 5);
        food.UpdateAndGetTransitionStates(fixture.World, fixture.Inventory[0]);
        var target = Snapshot(fixture.Inventory);
        fixture.Inventory[0].Itemstack!.StackSize = 3;
        var expected = Snapshot(fixture.Inventory);
        var slotEvents = 0;
        fixture.Inventory.SlotModified += _ => slotEvents++;

        fixture.GameHours += 1;
        food.UpdateAndGetTransitionStates(fixture.World, fixture.Inventory[0]);
        Assert.False(expected.AsSpan().SequenceEqual(Snapshot(fixture.Inventory)));
        Assert.Equal(0, slotEvents); // Vanilla aging changes state without a slot notification.
        var agedResult = fixture.Inventory[0].Itemstack!.Clone();
        agedResult.StackSize = 5;

        var result = await new VintageStoryRollbackContainerApplier(fixture.Api)
            .ApplyAsync(Entry(fixture, expected, target));

        Assert.Equal(RollbackContainerApplyStatus.Applied, result.Status);
        Assert.True(result.ChangedWorld);
        Assert.Equal(agedResult.ToBytes(), fixture.Inventory[0].Itemstack!.ToBytes());
        Assert.Equal(1, slotEvents);
        Assert.Equal(Snapshot(fixture.Inventory), result.ActualSnapshot);
    }

    [Theory]
    [InlineData("quality")]
    [InlineData("owner")]
    public async Task ApplyAsync_ChangedCustomAttribute_ConflictsWithoutMutatingAnySlot(string attribute)
    {
        using var fixture = new ContainerTestFixture();
        fixture.Inventory[0].Itemstack = fixture.Stack("game:torch", 5);
        fixture.Inventory[0].Itemstack!.Attributes.SetString(attribute, "before");
        var target = Snapshot(fixture.Inventory);
        fixture.Inventory[0].Itemstack!.StackSize = 3;
        var expected = Snapshot(fixture.Inventory);
        fixture.Inventory[0].Itemstack!.Attributes.SetString(attribute, "changed-by-another-action");
        var actualBefore = Snapshot(fixture.Inventory);
        var notifications = 0;
        fixture.Inventory.SlotModified += _ => notifications++;

        var result = await new VintageStoryRollbackContainerApplier(fixture.Api)
            .ApplyAsync(Entry(fixture, expected, target));

        Assert.Equal(RollbackContainerApplyStatus.Conflict, result.Status);
        Assert.False(result.ChangedWorld);
        Assert.Equal(actualBefore, Snapshot(fixture.Inventory));
        Assert.Equal(0, notifications);
    }

    [Fact]
    public async Task ApplyAsync_UnrelatedSlotChanged_PreservesItAndOnlyNotifiesTouchedSlot()
    {
        using var fixture = new ContainerTestFixture();
        fixture.Inventory[0].Itemstack = fixture.Stack("game:torch", 5);
        fixture.Inventory[1].Itemstack = fixture.Stack("game:stone", 1);
        var target = Snapshot(fixture.Inventory);
        fixture.Inventory[0].Itemstack!.StackSize = 3;
        var expected = Snapshot(fixture.Inventory);
        fixture.Inventory[1].Itemstack = fixture.Stack("game:diamond", 7);
        fixture.Inventory[1].Itemstack!.Attributes.SetString("owner", "Bob");
        var unrelated = fixture.Inventory[1].Itemstack!.ToBytes();
        var notifications = new List<int>();
        fixture.Inventory.SlotModified += notifications.Add;

        var result = await new VintageStoryRollbackContainerApplier(fixture.Api)
            .ApplyAsync(Entry(fixture, expected, target));

        Assert.Equal(RollbackContainerApplyStatus.Applied, result.Status);
        Assert.Equal(5, fixture.Inventory[0].StackSize);
        Assert.Equal(unrelated, fixture.Inventory[1].Itemstack!.ToBytes());
        Assert.Equal([0], notifications);
        Assert.Equal(Snapshot(fixture.Inventory), result.ActualSnapshot);
    }

    [Fact]
    public async Task ApplyAsync_TouchedSlotQuantityChanged_ConflictsAndPreservesCurrentContents()
    {
        using var fixture = new ContainerTestFixture();
        fixture.Inventory[0].Itemstack = fixture.Stack("game:torch", 5);
        var target = Snapshot(fixture.Inventory);
        fixture.Inventory[0].Itemstack!.StackSize = 3;
        var expected = Snapshot(fixture.Inventory);
        fixture.Inventory[0].Itemstack!.StackSize = 4;

        var result = await new VintageStoryRollbackContainerApplier(fixture.Api)
            .ApplyAsync(Entry(fixture, expected, target));

        Assert.Equal(RollbackContainerApplyStatus.Conflict, result.Status);
        Assert.False(result.ChangedWorld);
        Assert.Equal(4, fixture.Inventory[0].StackSize);
    }

    [Fact]
    public async Task ApplyAsync_UnderMutationGuard_SuppressesOrdinaryCaptureAndUpdatesSessionBaseline()
    {
        var transactions = new List<ContainerAuditTransaction>();
        var writer = ProxyFactory.Create<IAuditWriter>(p => p.SetHandler("QueueContainerTransaction", args =>
        {
            transactions.Add((ContainerAuditTransaction)args![0]!);
            return null;
        }));
        var guard = new WorldMutationGuard();
        using var fixture = new ContainerTestFixture(capture: new ContainerTransactionCaptureService(writer, guard));
        fixture.Inventory[0].Itemstack = fixture.Stack("game:torch", 5);
        var target = Snapshot(fixture.Inventory);
        fixture.Inventory[0].Itemstack!.StackSize = 3;
        var expected = Snapshot(fixture.Inventory);
        fixture.Bridge.TrackInventory(fixture.Inventory);
        var alice = ContainerTestFixture.Player("alice", "Alice");
        fixture.Inventory.Open(alice);
        var entry = Entry(fixture, expected, target);

        using (guard.Begin(entry.WorldId, entry.Position))
        {
            var result = await new VintageStoryRollbackContainerApplier(fixture.Api).ApplyAsync(entry);
            Assert.Equal(RollbackContainerApplyStatus.Applied, result.Status);
        }
        fixture.Inventory.Close(alice);
        Assert.Empty(transactions);

        using (fixture.Scopes.BeginAmbientScope(BlockMutationScope.Player("Alice", "alice")))
            fixture.Change(0, fixture.Stack("game:torch", 6));

        var transaction = Assert.Single(transactions);
        var line = Assert.Single(transaction.Lines);
        Assert.Equal(5, line.BeforeQuantity);
        Assert.Equal(6, line.AfterQuantity);
        Assert.Equal(1, line.QuantityDelta);
        Assert.Equal("alice", transaction.Actor.ExternalId);
    }

    private static RollbackContainerPlanEntry Entry(ContainerTestFixture fixture, byte[] expected, byte[] target)
    {
        var position = new BlockPosition(fixture.Position.X, fixture.Position.InternalY, fixture.Position.Z);
        return new RollbackContainerPlanEntry(1, 1, 1, "main", RollbackJobOperation.Rollback,
            position, position.ToChunkPosition(32), DateTimeOffset.UtcNow, "chest", "game:chest",
            expected, target, 0);
    }

    private static byte[] Snapshot(InventoryBase inventory)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(inventory.Count);
        for (var slotId = 0; slotId < inventory.Count; slotId++)
        {
            var stack = inventory[slotId].Itemstack;
            var bytes = stack?.ToBytes();
            writer.Write(slotId);
            writer.Write(stack?.Collectible.Code.ToString() ?? "");
            writer.Write(stack?.StackSize ?? 0);
            writer.Write(bytes?.Length ?? 0);
            if (bytes is not null) writer.Write(bytes);
        }
        return stream.ToArray();
    }
}
