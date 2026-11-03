using Vintagestory.API.Common;
using Vintagestory.API.Server;
using WorldAudit.Domain;
using WorldAudit.Integration.VintageStory;
using WorldAudit.Tests.TestSupport;

namespace WorldAudit.Tests;

public sealed class VintageStoryRollbackBlockApplierTests
{
    [Fact]
    public async Task SolidReplay_DoesNotRunMultiblockPlacementOrRemovalCallbacks()
    {
        var harness = new Harness("game:door-solid-oak");
        var result = await harness.Applier.ApplyAsync(Entry("game:door-solid-oak", "game:air"));
        Assert.Equal(RollbackBlockApplyStatus.Applied, result.Status);
        Assert.Equal(0, harness.GameplayWrites);
        Assert.Equal(1, harness.Writes);
    }
    [Fact]
    public async Task Apply_DoesNotDisplaceUnrelatedFluid()
    {
        var harness = new Harness("game:air") { Fluid = new Block { BlockId = 9, Code = new AssetLocation("game:water-still-7") } };
        harness.Target = new LiquidDisplacingBlock { BlockId = 4, Code = new AssetLocation("game:stone") };
        var result = await harness.Applier.ApplyAsync(Entry("game:air", "game:stone"));
        Assert.Equal(RollbackBlockApplyStatus.Conflict, result.Status);
        Assert.Equal(0, harness.Writes);
    }

    private sealed class LiquidDisplacingBlock : Block
    {
        public override bool DisplacesLiquids(IBlockAccessor accessor, Vintagestory.API.MathTools.BlockPos pos) => true;
    }
    [Fact]
    public async Task Apply_ConflictsWhenAnotherPlayerChangedTheBlock()
    {
        var harness = new Harness("game:granite");
        var result = await harness.Applier.ApplyAsync(Entry("game:stone", "game:air"));
        Assert.Equal(RollbackBlockApplyStatus.Conflict, result.Status);
        Assert.Equal("game:granite", harness.Current.Code.ToString());
        Assert.Equal(0, harness.Writes);
    }

    [Fact]
    public async Task Apply_RejectsMissingTargetBlockEntitySnapshot()
    {
        var harness = new Harness("game:air");
        harness.Target = new Block { BlockId = 4, Code = new AssetLocation("game:chest"), EntityClass = "Chest" };
        var result = await harness.Applier.ApplyAsync(Entry("game:air", "game:chest"));
        Assert.Equal(RollbackBlockApplyStatus.Conflict, result.Status);
        Assert.Equal(0, harness.Writes);
    }

    [Fact]
    public async Task Apply_RejectsUnrecordedCurrentBlockEntity()
    {
        var harness = new Harness("game:chest");
        harness.Current.EntityClass = "Chest";
        var result = await harness.Applier.ApplyAsync(Entry("game:chest", "game:air"));
        Assert.Equal(RollbackBlockApplyStatus.Conflict, result.Status);
        Assert.Equal(0, harness.Writes);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Apply_UsesRecordedLayerAndReportsActualMutation(int layer)
    {
        var harness = new Harness("game:stone") { CurrentIsFluid = layer == 2 };
        var result = await harness.Applier.ApplyAsync(Entry("game:stone", "game:air") with { Layer = layer });
        Assert.Equal(RollbackBlockApplyStatus.Applied, result.Status);
        Assert.True(result.ChangedWorld);
        Assert.Equal(layer, harness.ReadLayer);
        Assert.Equal(layer, harness.WriteLayer);
        Assert.Equal("game:stone", result.PreviousBlockCode);
        Assert.Equal("game:air", result.ActualBlockCode);
    }

    [Fact]
    public async Task Apply_RejectsLegacyUnknownLayer()
    {
        var harness = new Harness("game:stone");
        var result = await harness.Applier.ApplyAsync(Entry("game:stone", "game:air") with { Layer = 0 });
        Assert.Equal(RollbackBlockApplyStatus.Conflict, result.Status);
        Assert.Equal(0, harness.Writes);
    }

    [Fact]
    public async Task Apply_RestoresPreviousBlockWhenTargetSnapshotCannotBeRead()
    {
        var harness = new Harness("game:air");
        harness.Target = new Block { BlockId = 4, Code = new AssetLocation("game:chest"), EntityClass = "Chest" };
        var result = await harness.Applier.ApplyAsync(Entry("game:air", "game:chest") with { TargetBlockEntitySnapshot = [1] });
        Assert.Equal(RollbackBlockApplyStatus.Conflict, result.Status);
        Assert.False(result.ChangedWorld);
        Assert.Equal("game:air", harness.Current.Code.ToString());
        Assert.Equal(2, harness.Writes);
    }

    private static RollbackBlockPlanEntry Entry(string expected, string target) => new(
        1, 1, 1, "main", RollbackJobOperation.Rollback, new(1, 64, 1), new(0, 2, 0),
        DateTimeOffset.UtcNow, target, null, 0, ExpectedBlockCode: expected);

    private sealed class Harness
    {
        public Block Current;
        public bool CurrentIsFluid;
        public Block Fluid = new() { Code = new AssetLocation("game:air") };
        public Block Target = new() { BlockId = 0, Code = new AssetLocation("game:air") };
        public int Writes;
        public int GameplayWrites;
        public int ReadLayer;
        public int WriteLayer;
        public VintageStoryRollbackBlockApplier Applier;
        public Harness(string currentCode)
        {
            Current = new Block { BlockId = 2, Code = new AssetLocation(currentCode) };
            var original = Current;
            var accessor = ProxyFactory.Create<IBlockAccessor>(p =>
            {
                p.SetHandler(nameof(IBlockAccessor.GetChunkAtBlockPos), _ => ProxyFactory.Create<IWorldChunk>(_ => { }));
                p.SetHandler(nameof(IBlockAccessor.GetBlock), args => { ReadLayer = (int)args![1]!; return ReadLayer == 2 && !CurrentIsFluid ? Fluid : Current; });
                p.SetHandler(nameof(IBlockAccessor.SetBlock), args => { Writes++; GameplayWrites++; WriteLayer = (int)args![2]!; Current = (int)args[0]! == Target.Id ? Target : original; return null; });
                p.SetHandler(nameof(IBlockAccessor.ExchangeBlock), args => { Writes++; WriteLayer = 1; Current = (int)args![0]! == Target.Id ? Target : original; return null; });
            });
            var world = ProxyFactory.Create<IServerWorldAccessor>(p =>
            {
                p.SetValue(nameof(IServerWorldAccessor.BlockAccessor), accessor);
                p.SetHandler(nameof(IServerWorldAccessor.GetBlock), args => ((AssetLocation)args![0]!).Equals(Target.Code) ? Target : original);
            });
            var api = ProxyFactory.Create<ICoreServerAPI>(p => p.SetValue(nameof(ICoreServerAPI.World), world));
            Applier = new VintageStoryRollbackBlockApplier(api);
        }
    }
}
