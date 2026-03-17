using System.Collections.Concurrent;
using WorldAudit.Application.Configuration;
using WorldAudit.Application.Services;

namespace WorldAudit.Infrastructure.Persistence;

internal sealed class SqliteAuditRepositoryState
{
    public SqliteAuditRepositoryState(
        SqliteConnectionFactory connectionFactory,
        WorldAuditOptions options,
        AuditPerformanceDiagnostics? diagnostics)
    {
        ConnectionFactory = connectionFactory;
        Options = options;
        Diagnostics = diagnostics;
    }

    public SqliteConnectionFactory ConnectionFactory { get; }

    public WorldAuditOptions Options { get; }

    public AuditPerformanceDiagnostics? Diagnostics { get; }

    public ConcurrentDictionary<string, long> WorldIds { get; } = new(StringComparer.OrdinalIgnoreCase);

    public ConcurrentDictionary<string, long> ActorIds { get; } = new(StringComparer.OrdinalIgnoreCase);

    public ConcurrentDictionary<string, long> BlockIds { get; } = new(StringComparer.OrdinalIgnoreCase);

    public ConcurrentDictionary<string, long> CauseIds { get; } = new(StringComparer.OrdinalIgnoreCase);

    public ConcurrentDictionary<string, long> ActionIds { get; } = new(StringComparer.OrdinalIgnoreCase);

    public ConcurrentDictionary<string, long> InventoryTypeIds { get; } = new(StringComparer.OrdinalIgnoreCase);

    public ConcurrentDictionary<string, long> ItemIds { get; } = new(StringComparer.OrdinalIgnoreCase);
}
