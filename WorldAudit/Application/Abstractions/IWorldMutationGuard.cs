using WorldAudit.Domain;

namespace WorldAudit.Application.Abstractions;

public interface IWorldMutationGuard
{
    IDisposable Begin(string worldId, BlockPosition position);

    bool IsActive(string worldId, BlockPosition position);
}
