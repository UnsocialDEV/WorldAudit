using Vintagestory.API.Datastructures;
using Vintagestory.GameContent;
using WorldAudit.Integration.VintageStory;

namespace WorldAudit.Tests;

public sealed class RebuildSnapshotTests
{
    [Theory]
    [InlineData(typeof(BlockEntityGroundStorage))]
    [InlineData(typeof(BlockEntityBomb))]
    public void PreparingARebuildCopiesOnlyTheEntitySpecificHazardFields(Type type)
    {
        var original = new TreeAttribute();
        original.SetInt("lit", 1);
        original.SetFloat("remainingSeconds", 2.5f);
        original.SetString("ignitedByPlayerUid", "Alice");
        original.SetBool("burning", true);
        original.SetBool("isBurning", true);
        original.SetString("fixture", "preserved");
        var recorded = original.ToBytes();
        var rebuilt = new TreeAttribute();
        rebuilt.FromBytes(RebuildSnapshot.Prepare(type, recorded));
        Assert.Equal(original.ToBytes(), recorded);
        Assert.Equal("preserved", rebuilt.GetString("fixture"));
        var isBomb = type == typeof(BlockEntityBomb);
        Assert.Equal(isBomb, rebuilt.GetBool("burning"));
        Assert.Equal(isBomb, rebuilt.GetBool("isBurning"));
        Assert.Equal(isBomb ? 0 : 1, rebuilt.GetInt("lit"));
        Assert.Equal(isBomb ? 0f : 2.5f, rebuilt.GetFloat("remainingSeconds"));
        Assert.Equal(isBomb ? "" : "Alice", rebuilt.GetString("ignitedByPlayerUid"));
    }
}
