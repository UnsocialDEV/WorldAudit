using WorldAudit.Domain;

namespace WorldAudit.Application.Abstractions;

public interface IRollbackBlockApplier
{
    Task<RollbackBlockApplyResult> ApplyAsync(RollbackBlockPlanEntry entry, CancellationToken cancellationToken = default);
}
