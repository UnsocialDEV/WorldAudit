using WorldAudit.Integration.VintageStory;

namespace WorldAudit.Mod;

internal sealed record WorldAuditRuntimeReloadState(
    WorldAuditRuntime Runtime,
    WorldAuditModConfig Config,
    VintageStoryBlockEventBridge EventBridge,
    VintageStoryContainerEventBridge ContainerEventBridge,
    long RollbackTickListenerId);
