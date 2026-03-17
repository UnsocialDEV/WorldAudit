using Microsoft.Data.Sqlite;
using WorldAudit.Domain;

namespace WorldAudit.Infrastructure.Persistence;

internal sealed class SqliteAuditLookupResolver
{
    private readonly SqliteAuditRepositoryState _state;

    public SqliteAuditLookupResolver(SqliteAuditRepositoryState state)
    {
        _state = state;
    }

    public async Task<long> ResolveActorIdAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AuditActor actor,
        CancellationToken cancellationToken)
    {
        var actorName = AuditActor.NormalizeName(actor.Name);
        if (_state.ActorIds.TryGetValue(actorName, out var cachedId))
        {
            return cachedId;
        }

        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            """
            INSERT OR IGNORE INTO actors(name, external_id, is_synthetic)
            VALUES ($name, $externalId, $isSynthetic);
            """;
        insert.Parameters.AddWithValue("$name", actorName);
        insert.Parameters.AddWithValue("$externalId", actor.ExternalId is null ? DBNull.Value : actor.ExternalId);
        insert.Parameters.AddWithValue("$isSynthetic", actor.IsSynthetic ? 1 : 0);
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await using var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText = "SELECT id FROM actors WHERE name = $name COLLATE NOCASE;";
        select.Parameters.AddWithValue("$name", actorName);
        var result = await select.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        var id = result is long actorId
            ? actorId
            : throw new InvalidOperationException($"Failed to resolve actor '{actorName}'.");

        _state.ActorIds.TryAdd(actorName, id);
        return id;
    }

    public Task<long> ResolveWorldIdAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string worldCode,
        CancellationToken cancellationToken)
        => ResolveCodeLookupAsync(connection, transaction, _state.WorldIds, "worlds", worldCode, cancellationToken);

    public Task<long?> TryResolveWorldIdAsync(SqliteConnection connection, string worldCode, CancellationToken cancellationToken)
        => TryResolveCodeLookupAsync(connection, _state.WorldIds, "worlds", worldCode, cancellationToken);

    public Task<long> ResolveCauseIdAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AuditCause cause,
        CancellationToken cancellationToken)
        => ResolveCodeLookupAsync(connection, transaction, _state.CauseIds, "causes", SqliteAuditValueCodec.ToCode(cause), cancellationToken);

    public Task<long?> TryResolveCauseIdAsync(SqliteConnection connection, AuditCause cause, CancellationToken cancellationToken)
        => TryResolveCodeLookupAsync(connection, _state.CauseIds, "causes", SqliteAuditValueCodec.ToCode(cause), cancellationToken);

    public Task<long> ResolveActionIdAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        BlockAuditAction action,
        CancellationToken cancellationToken)
        => ResolveCodeLookupAsync(connection, transaction, _state.ActionIds, "actions", SqliteAuditValueCodec.ToCode(action), cancellationToken);

    public Task<long?> TryResolveActionIdAsync(SqliteConnection connection, BlockAuditAction action, CancellationToken cancellationToken)
        => TryResolveCodeLookupAsync(connection, _state.ActionIds, "actions", SqliteAuditValueCodec.ToCode(action), cancellationToken);

    public Task<long?> ResolveOptionalBlockIdAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string? blockCode,
        CancellationToken cancellationToken)
        => ResolveOptionalCodeLookupAsync(connection, transaction, _state.BlockIds, "blocks", blockCode, cancellationToken);

    public Task<long> ResolveInventoryTypeIdAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string inventoryType,
        CancellationToken cancellationToken)
        => ResolveCodeLookupAsync(connection, transaction, _state.InventoryTypeIds, "inventory_types", inventoryType, cancellationToken);

    public Task<long> ResolveItemIdAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string itemCode,
        CancellationToken cancellationToken)
        => ResolveCodeLookupAsync(connection, transaction, _state.ItemIds, "items", itemCode, cancellationToken);

    public async Task<long?> TryResolveOptionalActorIdAsync(
        SqliteConnection connection,
        string? actorName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(actorName))
        {
            return null;
        }

        if (_state.ActorIds.TryGetValue(actorName, out var cachedId))
        {
            return cachedId;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM actors WHERE name = $name COLLATE NOCASE;";
        command.Parameters.AddWithValue("$name", actorName);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (result is not long actorId)
        {
            return null;
        }

        _state.ActorIds.TryAdd(actorName, actorId);
        return actorId;
    }

    public async Task<ResolvedLookupFilters?> ResolveLookupFiltersAsync(
        SqliteConnection connection,
        long? actorId,
        AuditCause? cause,
        BlockAuditAction? action,
        CancellationToken cancellationToken)
    {
        var causeId = cause.HasValue
            ? await TryResolveCauseIdAsync(connection, cause.Value, cancellationToken).ConfigureAwait(false)
            : null;
        var actionId = action.HasValue
            ? await TryResolveActionIdAsync(connection, action.Value, cancellationToken).ConfigureAwait(false)
            : null;

        if ((cause.HasValue && !causeId.HasValue) || (action.HasValue && !actionId.HasValue))
        {
            return null;
        }

        return new ResolvedLookupFilters(
            SqliteAuditSql.BuildFilterMask(actorId.HasValue, causeId.HasValue, actionId.HasValue),
            actorId,
            causeId,
            actionId);
    }

    private static async Task<long> ResolveCodeLookupAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        System.Collections.Concurrent.ConcurrentDictionary<string, long> cache,
        string tableName,
        string code,
        CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(code, out var cachedId))
        {
            return cachedId;
        }

        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = $"INSERT OR IGNORE INTO {tableName}(code) VALUES ($code);";
        insert.Parameters.AddWithValue("$code", code);
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await using var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText = $"SELECT id FROM {tableName} WHERE code = $code COLLATE NOCASE;";
        select.Parameters.AddWithValue("$code", code);
        var result = await select.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        var id = result is long lookupId
            ? lookupId
            : throw new InvalidOperationException($"Failed to resolve lookup '{tableName}:{code}'.");

        cache.TryAdd(code, id);
        return id;
    }

    private static Task<long?> ResolveOptionalCodeLookupAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        System.Collections.Concurrent.ConcurrentDictionary<string, long> cache,
        string tableName,
        string? code,
        CancellationToken cancellationToken)
    {
        return string.IsNullOrWhiteSpace(code)
            ? Task.FromResult<long?>(null)
            : ResolveOptionalCodeLookupCoreAsync(connection, transaction, cache, tableName, code, cancellationToken);
    }

    private static async Task<long?> ResolveOptionalCodeLookupCoreAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        System.Collections.Concurrent.ConcurrentDictionary<string, long> cache,
        string tableName,
        string code,
        CancellationToken cancellationToken)
    {
        var lookupId = await ResolveCodeLookupAsync(connection, transaction, cache, tableName, code, cancellationToken).ConfigureAwait(false);
        return lookupId;
    }

    private static async Task<long?> TryResolveCodeLookupAsync(
        SqliteConnection connection,
        System.Collections.Concurrent.ConcurrentDictionary<string, long> cache,
        string tableName,
        string code,
        CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(code, out var cachedId))
        {
            return cachedId;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT id FROM {tableName} WHERE code = $code COLLATE NOCASE;";
        command.Parameters.AddWithValue("$code", code);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        if (result is not long id)
        {
            return null;
        }

        cache.TryAdd(code, id);
        return id;
    }
}

internal sealed record ResolvedLookupFilters(
    int FilterMask,
    long? ActorId,
    long? CauseId,
    long? ActionId);
