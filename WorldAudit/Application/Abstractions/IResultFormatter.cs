using WorldAudit.Domain;

namespace WorldAudit.Application.Abstractions;

public interface IResultFormatter
{
    IReadOnlyList<string> FormatBlockResults(IReadOnlyList<BlockAuditEvent> events, DateTimeOffset now);

    IReadOnlyList<string> FormatContainerResults(IReadOnlyList<ContainerAuditTransaction> transactions, DateTimeOffset now);
}
