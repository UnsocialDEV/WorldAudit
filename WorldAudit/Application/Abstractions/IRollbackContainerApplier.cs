using WorldAudit.Domain;

namespace WorldAudit.Application.Abstractions;

public interface IRollbackContainerApplier
{
    Task<RollbackContainerApplyResult> ApplyAsync(RollbackContainerPlanEntry entry, CancellationToken cancellationToken = default);
}
