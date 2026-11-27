using System.Diagnostics;
using WorldAudit.Domain;

namespace WorldAudit.Infrastructure.Persistence;

internal sealed class SqliteAuditQueryStore
{
    private readonly SqliteAuditRepositoryState _state;
    private readonly SqliteAuditLookupResolver _resolver;
    private readonly SqliteAuditRecordReader _reader;

    public SqliteAuditQueryStore(
        SqliteAuditRepositoryState state,
        SqliteAuditLookupResolver resolver,
        SqliteAuditRecordReader reader)
    {
        _state = state;
        _resolver = resolver;
        _reader = reader;
    }

    public async Task<IReadOnlyList<BlockAuditEvent>> GetBlockHistoryAsync(BlockHistoryQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var started = Stopwatch.StartNew();

        try
        {
            await using var connection = await _state.ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var worldId = await _resolver.TryResolveWorldIdAsync(connection, query.WorldId, cancellationToken).ConfigureAwait(false);
            if (!worldId.HasValue)
            {
                return Array.Empty<BlockAuditEvent>();
            }

            await using var command = connection.CreateCommand();
            SqliteHistoryPage.Configure(command, container: false, worldId.Value, query.Position, radius: 0,
                _state.Options.ChunkSize, query.TimeRange, query.Limit, filters: null, query.IncludeCodes, query.ExcludeCodes);
            return await _reader.ReadBlockEventsAsync(command, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _state.Diagnostics?.RecordQuery(nameof(GetBlockHistoryAsync), started.Elapsed);
        }
    }

    public async Task<IReadOnlyList<BlockAuditEvent>> LookupBlockEventsAsync(BlockLookupQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var started = Stopwatch.StartNew();

        try
        {
            await using var connection = await _state.ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var worldId = await _resolver.TryResolveWorldIdAsync(connection, query.WorldId, cancellationToken).ConfigureAwait(false);
            if (!worldId.HasValue)
            {
                return Array.Empty<BlockAuditEvent>();
            }

            var actorId = await _resolver.TryResolveOptionalActorIdAsync(connection, query.ActorName, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(query.ActorName) && !actorId.HasValue)
            {
                return Array.Empty<BlockAuditEvent>();
            }

            var filters = await _resolver.ResolveLookupFiltersAsync(connection, actorId, query.Cause, query.Action, cancellationToken).ConfigureAwait(false);
            if (filters is null)
            {
                return Array.Empty<BlockAuditEvent>();
            }

            await using var command = connection.CreateCommand();
            SqliteHistoryPage.Configure(command, container: false, worldId.Value, query.Center,
                query.Radius, _state.Options.ChunkSize, query.TimeRange, query.Limit, filters, query.IncludeCodes, query.ExcludeCodes);
            return await _reader.ReadBlockEventsAsync(command, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _state.Diagnostics?.RecordQuery(nameof(LookupBlockEventsAsync), started.Elapsed);
        }
    }

    public async Task<IReadOnlyList<BlockAuditEvent>> SelectRollbackBlockEventsAsync(RollbackBlockSelectionQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var started = Stopwatch.StartNew();

        try
        {
            await using var connection = await _state.ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var worldId = await _resolver.TryResolveWorldIdAsync(connection, query.WorldId, cancellationToken).ConfigureAwait(false);
            if (!worldId.HasValue)
            {
                return Array.Empty<BlockAuditEvent>();
            }

            var actorId = await _resolver.TryResolveOptionalActorIdAsync(connection, query.ActorName, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(query.ActorName) && !actorId.HasValue)
            {
                return Array.Empty<BlockAuditEvent>();
            }

            var filters = await _resolver.ResolveLookupFiltersAsync(connection, actorId, query.Cause, query.Action, cancellationToken).ConfigureAwait(false);
            if (filters is null)
            {
                return Array.Empty<BlockAuditEvent>();
            }

            await using var command = connection.CreateCommand();
            SqliteHistoryPage.Configure(command, container: false, worldId.Value, query.Center,
                query.Radius, _state.Options.ChunkSize, query.TimeRange, query.Limit, filters, query.IncludeCodes, query.ExcludeCodes,
                query.SkipFlameOnlyChanges, excludeJobHistory: query.ExcludeJobHistory && query.Cause is not (AuditCause.Rollback or AuditCause.Restore));
            return await _reader.ReadBlockEventsAsync(command, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _state.Diagnostics?.RecordQuery(nameof(SelectRollbackBlockEventsAsync), started.Elapsed);
        }
    }

    public async Task<IReadOnlyList<ContainerAuditTransaction>> GetContainerHistoryAsync(ContainerHistoryQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var started = Stopwatch.StartNew();

        try
        {
            await using var connection = await _state.ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var worldId = await _resolver.TryResolveWorldIdAsync(connection, query.WorldId, cancellationToken).ConfigureAwait(false);
            if (!worldId.HasValue)
            {
                return Array.Empty<ContainerAuditTransaction>();
            }

            await using var command = connection.CreateCommand();
            SqliteHistoryPage.Configure(command, container: true, worldId.Value, query.Position, radius: 0,
                _state.Options.ChunkSize, query.TimeRange, query.Limit, filters: null, query.IncludeCodes, query.ExcludeCodes);
            return await _reader.ReadContainerTransactionsAsync(connection, command, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _state.Diagnostics?.RecordQuery(nameof(GetContainerHistoryAsync), started.Elapsed);
        }
    }

    public async Task<IReadOnlyList<ContainerAuditTransaction>> LookupContainerTransactionsAsync(ContainerLookupQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var started = Stopwatch.StartNew();

        try
        {
            await using var connection = await _state.ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var worldId = await _resolver.TryResolveWorldIdAsync(connection, query.WorldId, cancellationToken).ConfigureAwait(false);
            if (!worldId.HasValue)
            {
                return Array.Empty<ContainerAuditTransaction>();
            }

            var actorId = await _resolver.TryResolveOptionalActorIdAsync(connection, query.ActorName, cancellationToken).ConfigureAwait(false);
            var causeId = query.Cause.HasValue
                ? await _resolver.TryResolveCauseIdAsync(connection, query.Cause.Value, cancellationToken).ConfigureAwait(false)
                : null;
            if ((!string.IsNullOrWhiteSpace(query.ActorName) && !actorId.HasValue)
                || (query.Cause.HasValue && !causeId.HasValue))
            {
                return Array.Empty<ContainerAuditTransaction>();
            }

            var filters = new ResolvedLookupFilters(

                actorId,
                causeId,
                ActionId: null);

            await using var command = connection.CreateCommand();
            SqliteHistoryPage.Configure(command, container: true, worldId.Value, query.Center,
                query.Radius, _state.Options.ChunkSize, query.TimeRange, query.Limit, filters, query.IncludeCodes, query.ExcludeCodes);
            return await _reader.ReadContainerTransactionsAsync(connection, command, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _state.Diagnostics?.RecordQuery(nameof(LookupContainerTransactionsAsync), started.Elapsed);
        }
    }

    public async Task<IReadOnlyList<ContainerAuditTransaction>> SelectRollbackContainerTransactionsAsync(
        ContainerRollbackSelectionQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var started = Stopwatch.StartNew();

        try
        {
            await using var connection = await _state.ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var worldId = await _resolver.TryResolveWorldIdAsync(connection, query.WorldId, cancellationToken).ConfigureAwait(false);
            if (!worldId.HasValue)
            {
                return Array.Empty<ContainerAuditTransaction>();
            }

            var actorId = await _resolver.TryResolveOptionalActorIdAsync(connection, query.ActorName, cancellationToken).ConfigureAwait(false);
            var causeId = query.Cause.HasValue
                ? await _resolver.TryResolveCauseIdAsync(connection, query.Cause.Value, cancellationToken).ConfigureAwait(false)
                : null;
            if ((!string.IsNullOrWhiteSpace(query.ActorName) && !actorId.HasValue)
                || (query.Cause.HasValue && !causeId.HasValue))
            {
                return Array.Empty<ContainerAuditTransaction>();
            }

            var filters = new ResolvedLookupFilters(

                actorId,
                causeId,
                ActionId: null);

            await using var command = connection.CreateCommand();
            SqliteHistoryPage.Configure(command, container: true, worldId.Value, query.Center,
                query.Radius, _state.Options.ChunkSize, query.TimeRange, query.Limit, filters, query.IncludeCodes, query.ExcludeCodes,
                excludeJobHistory: query.ExcludeJobHistory && query.Cause is not (AuditCause.Rollback or AuditCause.Restore));
            return await _reader.ReadContainerTransactionsAsync(connection, command, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _state.Diagnostics?.RecordQuery(nameof(SelectRollbackContainerTransactionsAsync), started.Elapsed);
        }
    }
}
