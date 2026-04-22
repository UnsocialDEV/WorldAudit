namespace WorldAudit.Integration.VintageStory;

public sealed record VintageStoryInteractionResolution(
    string ActorName,
    string? ActorExternalId,
    byte[]? OldBlockEntitySnapshot,
    bool IsAmbiguous);
