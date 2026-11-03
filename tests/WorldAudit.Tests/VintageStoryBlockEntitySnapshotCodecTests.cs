using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;
using WorldAudit.Integration.VintageStory;
using WorldAudit.Tests.TestSupport;

namespace WorldAudit.Tests;

public sealed class VintageStoryBlockEntitySnapshotCodecTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Restore_Repairs1227MicroblockSnowArrayAsymmetry(bool hasSnow)
    {
        var entity = new SnowEntity();
        var snow = new BEBehaviorMicroblockSnowCover(entity);
        entity.Behaviors.Add(snow);
        var codec = CreateCodec(entity);
        var recorded = new TreeAttribute();
        if (hasSnow) recorded["snowcuboids"] = new IntArrayAttribute(new[] { 16773360 });
        snow.SnowCuboids.Add(99); snow.GroundSnowCuboids.Add(100);
        Assert.True(codec.TryRestore(new BlockPos(0), recorded.ToBytes(), out var error), error);
        Assert.Equal(hasSnow ? new uint[] { 16773360 } : [], snow.SnowCuboids);
        Assert.Empty(snow.GroundSnowCuboids);
        Assert.True(codec.AreEquivalent(recorded.ToBytes(), codec.Capture(new BlockPos(0))));
    }

    [Fact]
    public void SnapshotEquality_IgnoresAttributeOrderButPreservesValues()
    {
        var codec = CreateCodec(new SnapshotEntity());
        var left = new TreeAttribute(); left.SetInt("a", 1); left.SetInt("b", 2);
        var right = new TreeAttribute(); right.SetInt("b", 2); right.SetInt("a", 1);
        Assert.True(codec.AreEquivalent(left.ToBytes(), right.ToBytes()));
        right.SetInt("b", 3);
        Assert.False(codec.AreEquivalent(left.ToBytes(), right.ToBytes()));
    }

    private sealed class SnowEntity : BlockEntity
    {
        public override void ToTreeAttributes(ITreeAttribute tree) { foreach (var b in Behaviors) b.ToTreeAttributes(tree); }
        public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor world) { foreach (var b in Behaviors) b.FromTreeAttributes(tree, world); }
        public override void MarkDirty(bool redrawOnClient = false, IPlayer? skipPlayer = null) { }
    }
    [Fact]
    public void CaptureAndRestore_RoundTripNestedNativeAttributes()
    {
        var entity = new SnapshotEntity { Value = "kept", Quantity = 42 };
        var codec = CreateCodec(entity);
        var snapshot = codec.Capture(new BlockPos(1, 2, 3));
        Assert.NotNull(snapshot);
        entity.Value = "changed";
        entity.Quantity = 0;
        Assert.True(codec.TryRestore(new BlockPos(1, 2, 3), snapshot, out var error), error);
        Assert.Equal("kept", entity.Value);
        Assert.Equal(42, entity.Quantity);
        Assert.True(entity.Dirty);
    }

    [Fact]
    public void Capture_ReportsSerializationFailureInsteadOfMissingEntity()
    {
        var codec = CreateCodec(new SnapshotEntity { ThrowOnCapture = true });
        Assert.Throws<InvalidOperationException>(() => codec.Capture(new BlockPos(0)));
    }

    [Fact]
    public void Restore_RejectsCorruptSnapshotBeforeChangingEntity()
    {
        var entity = new SnapshotEntity { Value = "kept" };
        var codec = CreateCodec(entity);
        Assert.False(codec.TryRestore(new BlockPos(0), [1], out var error));
        Assert.NotNull(error);
        Assert.Equal("kept", entity.Value);
    }

    private static VintageStoryBlockEntitySnapshotCodec CreateCodec(BlockEntity entity)
    {
        var accessor = ProxyFactory.Create<IBlockAccessor>(p => p.SetHandler(nameof(IBlockAccessor.GetBlockEntity), _ => entity));
        var world = ProxyFactory.Create<IWorldAccessor>(p => p.SetValue(nameof(IWorldAccessor.BlockAccessor), accessor));
        return new VintageStoryBlockEntitySnapshotCodec(world);
    }

    private sealed class SnapshotEntity : BlockEntity
    {
        public string Value = "";
        public int Quantity;
        public bool ThrowOnCapture;
        public bool Dirty;
        public override void ToTreeAttributes(ITreeAttribute tree)
        {
            if (ThrowOnCapture) throw new InvalidOperationException("broken entity");
            tree.SetString("value", Value);
            var inventory = new TreeAttribute();
            inventory.SetInt("quantity", Quantity);
            tree["inventory"] = inventory;
        }
        public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldAccessForResolve)
        {
            Value = tree.GetString("value");
            Quantity = tree.GetTreeAttribute("inventory").GetInt("quantity");
        }
        public override void MarkDirty(bool redrawOnClient = false, IPlayer? skipPlayer = null) => Dirty = true;
    }
}
