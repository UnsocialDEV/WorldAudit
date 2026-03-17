using WorldAudit.Integration.VintageStory;

namespace WorldAudit.Tests;

public sealed class VintageStoryBlockEntitySnapshotCodecTests
{
    [Fact]
    public void TryGetOrNull_ReturnsBytes_WhenOperationSucceeds()
    {
        var snapshot = VintageStoryBlockEntitySnapshotCodec.TryGetOrNull(
            static () => new byte[] { 1, 2, 3 });

        Assert.Equal([1, 2, 3], snapshot);
    }

    [Fact]
    public void TryGetOrNull_ReturnsNull_WhenOperationThrows()
    {
        var snapshot = VintageStoryBlockEntitySnapshotCodec.TryGetOrNull<byte[]>(
            static () => throw new InvalidOperationException("unsupported attribute"));

        Assert.Null(snapshot);
    }
}
