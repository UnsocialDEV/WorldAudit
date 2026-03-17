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

            var hasCodeFilters = HasCodeFilters(query.IncludeCodes, query.ExcludeCodes);
            await using var command = connection.CreateCommand();
            command.CommandText = SqliteAuditSql.BuildBlockHistorySql(includeLimit: !hasCodeFilters);
            command.Parameters.AddWithValue("$worldId", worldId.Value);
            command.Parameters.AddWithValue("$x", query.Position.X);
            command.Parameters.AddWithValue("$y", query.Position.Y);
            command.Parameters.AddWithValue("$z", query.Position.Z);
            if (!hasCodeFilters)
            {
                command.Parameters.AddWithValue("$limit", query.Limit);
            }

            SqliteAuditSql.AddTimeRangeParameters(command, query.TimeRange);

            var results = await _reader.ReadBlockEventsAsync(command, cancellationToken).ConfigureAwait(false);
            return LimitBlockResults(FilterBlockResults(results, query.IncludeCodes, query.ExcludeCodes), query.Limit);
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

            var (min, max, minChunk, maxChunk) = BuildSearchBounds(query.Center, query.Radius);
            await using var command = connection.CreateCommand();
            command.CommandText = SqliteAuditSql.BlockLookupSqlByMask[filters.FilterMask];
            command.Parameters.AddWithValue("$worldId", worldId.Value);
            SqliteAuditSql.AddLookupParameters(command, min, max, minChunk, maxChunk, query.TimeRange, query.Limit, filters);

            var events = await _reader.ReadBlockEventsAsync(command, cancellationToken).ConfigureAwait(false);
            return FilterBlockResults(events, query.Center, query.Radius, query.IncludeCodes, query.ExcludeCodes, query.Limit);
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

            var (min, max, minChunk, maxChunk) = BuildSearchBounds(query.Center, query.Radius);
            await using var command = connection.CreateCommand();
            command.CommandText = SqliteAuditSql.RollbackBlockLookupSqlByMask[filters.FilterMask];
            command.Parameters.AddWithValue("$worldId", worldId.Value);
            SqliteAuditSql.AddLookupParameters(command, min, max, minChunk, maxChunk, query.TimeRange, limit: null, filters);

            var events = await _reader.ReadBlockEventsAsync(command, cancellationToken).ConfigureAwait(false);
            return FilterBlockResults(events, query.Center, query.Radius, query.IncludeCodes, query.ExcludeCodes, query.Limit);
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

            var hasCodeFilters = HasCodeFilters(query.IncludeCodes, query.ExcludeCodes);
            await using var command = connection.CreateCommand();
            command.CommandText = SqliteAuditSql.BuildContainerHistorySql(includeLimit: !hasCodeFilters);
            command.Parameters.AddWithValue("$worldId", worldId.Value);
            command.Parameters.AddWithValue("$x", query.Position.X);
            command.Parameters.AddWithValue("$y", query.Position.Y);
            command.Parameters.AddWithValue("$z", query.Position.Z);
            if (!hasCodeFilters)
            {
                command.Parameters.AddWithValue("$limit", query.Limit);
            }

            SqliteAuditSql.AddTimeRangeParameters(command, query.TimeRange);

            var transactions = await _reader.ReadContainerTransactionsAsync(connection, command, cancellationToken).ConfigureAwait(false);
            return LimitContainerResults(FilterContainerResults(transactions, query.IncludeCodes, query.ExcludeCodes), query.Limit);
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
                SqliteAuditSql.BuildFilterMask(actorId.HasValue, causeId.HasValue, hasAction: false),
                actorId,
                causeId,
                ActionId: null);

            var (min, max, minChunk, maxChunk) = BuildSearchBounds(query.Center, query.Radius);
            await using var command = connection.CreateCommand();
            command.CommandText = SqliteAuditSql.ContainerLookupSqlByMask[filters.FilterMask];
            command.Parameters.AddWithValue("$worldId", worldId.Value);
            SqliteAuditSql.AddLookupParameters(command, min, max, minChunk, maxChunk, query.TimeRange, limit: null, filters);

            var transactions = await _reader.ReadContainerTransactionsAsync(connection, command, cancellationToken).ConfigureAwait(false);
            return FilterContainerResults(transactions, query.Center, query.Radius, query.IncludeCodes, query.ExcludeCodes, query.Limit);
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
                SqliteAuditSql.BuildFilterMask(actorId.HasValue, causeId.HasValue, hasAction: false),
                actorId,
                causeId,
                ActionId: null);

            var (min, max, minChunk, maxChunk) = BuildSearchBounds(query.Center, query.Radius);
            await using var command = connection.CreateCommand();
            command.CommandText = SqliteAuditSql.ContainerLookupSqlByMask[filters.FilterMask];
            command.Parameters.AddWithValue("$worldId", worldId.Value);
            SqliteAuditSql.AddLookupParameters(command, min, max, minChunk, maxChunk, query.TimeRange, limit: null, filters);

            var transactions = await _reader.ReadContainerTransactionsAsync(connection, command, cancellationToken).ConfigureAwait(false);
            return FilterContainerResults(transactions, query.Center, query.Radius, query.IncludeCodes, query.ExcludeCodes, query.Limit);
        }
        finally
        {
            _state.Diagnostics?.RecordQuery(nameof(SelectRollbackContainerTransactionsAsync), started.Elapsed);
        }
    }

    private (BlockPosition Min, BlockPosition Max, ChunkPosition MinChunk, ChunkPosition MaxChunk) BuildSearchBounds(BlockPosition center, int radius)
    {
        var min = new BlockPosition(center.X - radius, center.Y - radius, center.Z - radius);
        var max = new BlockPosition(center.X + radius, center.Y + radius, center.Z + radius);
        return (min, max, min.ToChunkPosition(_state.Options.ChunkSize), max.ToChunkPosition(_state.Options.ChunkSize));
    }

    private static bool HasCodeFilters(IReadOnlyList<string>? includeCodes, IReadOnlyList<string>? excludeCodes)
    {
        return includeCodes is { Count: > 0 } || excludeCodes is { Count: > 0 };
    }

    private static IReadOnlyList<BlockAuditEvent> FilterBlockResults(
        IReadOnlyList<BlockAuditEvent> events,
        BlockPosition center,
        int radius,
        IReadOnlyList<string>? includeCodes,
        IReadOnlyList<string>? excludeCodes,
        int limit)
    {
        var filtered = new List<BlockAuditEvent>(Math.Min(events.Count, limit));
        var maxDistanceSquared = (long)radius * radius;
        for (var index = 0; index < events.Count && filtered.Count < limit; index++)
        {
            var entry = events[index];
            if (entry.Position.DistanceSquaredTo(center) <= maxDistanceSquared
                && MatchesBlockCodeFilters(entry, includeCodes, excludeCodes))
            {
                filtered.Add(entry);
            }
        }

        return filtered;
    }

    private static IReadOnlyList<ContainerAuditTransaction> FilterContainerResults(
        IReadOnlyList<ContainerAuditTransaction> transactions,
        BlockPosition center,
        int radius,
        IReadOnlyList<string>? includeCodes,
        IReadOnlyList<string>? excludeCodes,
        int limit)
    {
        var filtered = new List<ContainerAuditTransaction>(Math.Min(transactions.Count, limit));
        var maxDistanceSquared = (long)radius * radius;
        for (var index = 0; index < transactions.Count && filtered.Count < limit; index++)
        {
            var entry = transactions[index];
            if (entry.Position.DistanceSquaredTo(center) <= maxDistanceSquared
                && MatchesContainerCodeFilters(entry, includeCodes, excludeCodes))
            {
                filtered.Add(entry);
            }
        }

        return filtered;
    }

    private static IReadOnlyList<BlockAuditEvent> FilterBlockResults(
        IReadOnlyList<BlockAuditEvent> events,
        IReadOnlyList<string>? includeCodes,
        IReadOnlyList<string>? excludeCodes)
    {
        if (!HasCodeFilters(includeCodes, excludeCodes))
        {
            return events;
        }

        var filtered = new List<BlockAuditEvent>(events.Count);
        for (var index = 0; index < events.Count; index++)
        {
            var entry = events[index];
            if (MatchesBlockCodeFilters(entry, includeCodes, excludeCodes))
            {
                filtered.Add(entry);
            }
        }

        return filtered;
    }

    private static IReadOnlyList<ContainerAuditTransaction> FilterContainerResults(
        IReadOnlyList<ContainerAuditTransaction> transactions,
        IReadOnlyList<string>? includeCodes,
        IReadOnlyList<string>? excludeCodes)
    {
        if (!HasCodeFilters(includeCodes, excludeCodes))
        {
            return transactions;
        }

        var filtered = new List<ContainerAuditTransaction>(transactions.Count);
        for (var index = 0; index < transactions.Count; index++)
        {
            var entry = transactions[index];
            if (MatchesContainerCodeFilters(entry, includeCodes, excludeCodes))
            {
                filtered.Add(entry);
            }
        }

        return filtered;
    }

    private static IReadOnlyList<BlockAuditEvent> LimitBlockResults(IReadOnlyList<BlockAuditEvent> events, int limit)
    {
        if (events.Count <= limit)
        {
            return events;
        }

        var limited = new BlockAuditEvent[limit];
        for (var index = 0; index < limit; index++)
        {
            limited[index] = events[index];
        }

        return limited;
    }

    private static IReadOnlyList<ContainerAuditTransaction> LimitContainerResults(IReadOnlyList<ContainerAuditTransaction> transactions, int limit)
    {
        if (transactions.Count <= limit)
        {
            return transactions;
        }

        var limited = new ContainerAuditTransaction[limit];
        for (var index = 0; index < limit; index++)
        {
            limited[index] = transactions[index];
        }

        return limited;
    }

    private static bool MatchesBlockCodeFilters(
        BlockAuditEvent entry,
        IReadOnlyList<string>? includeCodes,
        IReadOnlyList<string>? excludeCodes)
    {
        if (includeCodes is { Count: > 0 }
            && !ContainsCode(includeCodes, entry.OldBlockCode)
            && !ContainsCode(includeCodes, entry.NewBlockCode))
        {
            return false;
        }

        if (excludeCodes is { Count: > 0 }
            && (ContainsCode(excludeCodes, entry.OldBlockCode) || ContainsCode(excludeCodes, entry.NewBlockCode)))
        {
            return false;
        }

        return true;
    }

    private static bool MatchesContainerCodeFilters(
        ContainerAuditTransaction entry,
        IReadOnlyList<string>? includeCodes,
        IReadOnlyList<string>? excludeCodes)
    {
        var includeMatched = includeCodes is not { Count: > 0 };
        for (var index = 0; index < entry.Lines.Count; index++)
        {
            var itemCode = entry.Lines[index].ItemCode;
            if (!includeMatched && ContainsCode(includeCodes, itemCode))
            {
                includeMatched = true;
            }

            if (excludeCodes is { Count: > 0 } && ContainsCode(excludeCodes, itemCode))
            {
                return false;
            }
        }

        return includeMatched;
    }

    private static bool ContainsCode(IReadOnlyList<string>? codes, string? value)
    {
        if (codes is null || string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        for (var index = 0; index < codes.Count; index++)
        {
            if (string.Equals(codes[index], value, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
