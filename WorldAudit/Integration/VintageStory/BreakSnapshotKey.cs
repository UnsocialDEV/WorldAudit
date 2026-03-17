namespace WorldAudit.Integration.VintageStory;

internal readonly record struct BreakSnapshotKey(string PlayerUid, int X, int Y, int Z);
internal sealed record BreakSnapshot(string BlockCode, byte[]? BlockEntitySnapshot);
