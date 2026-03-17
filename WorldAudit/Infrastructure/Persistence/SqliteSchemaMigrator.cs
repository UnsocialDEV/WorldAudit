using Microsoft.Data.Sqlite;

namespace WorldAudit.Infrastructure.Persistence;

public sealed class SqliteSchemaMigrator
{
    private readonly SqliteConnectionFactory _connectionFactory;

    public SqliteSchemaMigrator(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(
            connection,
            transaction,
            """
            CREATE TABLE IF NOT EXISTS schema_migrations (
                version INTEGER NOT NULL PRIMARY KEY,
                applied_at_ms INTEGER NOT NULL
            );
            """,
            cancellationToken).ConfigureAwait(false);

        var alreadyApplied = await MigrationExistsAsync(connection, transaction, 1, cancellationToken).ConfigureAwait(false);
        if (!alreadyApplied)
        {
            await ExecuteAsync(
                connection,
                transaction,
                """
                CREATE TABLE worlds (
                    id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    code TEXT NOT NULL COLLATE NOCASE UNIQUE
                );

                CREATE TABLE actors (
                    id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    name TEXT NOT NULL COLLATE NOCASE UNIQUE,
                    external_id TEXT NULL,
                    is_synthetic INTEGER NOT NULL DEFAULT 0
                );

                CREATE TABLE causes (
                    id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    code TEXT NOT NULL COLLATE NOCASE UNIQUE
                );

                CREATE TABLE actions (
                    id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    code TEXT NOT NULL COLLATE NOCASE UNIQUE
                );

                CREATE TABLE blocks (
                    id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    code TEXT NOT NULL COLLATE NOCASE UNIQUE
                );

                CREATE TABLE block_events (
                    id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    world_id INTEGER NOT NULL,
                    x INTEGER NOT NULL,
                    y INTEGER NOT NULL,
                    z INTEGER NOT NULL,
                    chunk_x INTEGER NOT NULL,
                    chunk_y INTEGER NOT NULL,
                    chunk_z INTEGER NOT NULL,
                    occurred_at_ms INTEGER NOT NULL,
                    actor_id INTEGER NOT NULL,
                    cause_id INTEGER NOT NULL,
                    action_id INTEGER NOT NULL,
                    old_block_id INTEGER NULL,
                    new_block_id INTEGER NULL,
                    old_block_entity_blob BLOB NULL,
                    new_block_entity_blob BLOB NULL,
                    flags INTEGER NOT NULL DEFAULT 0,
                    source_job_id INTEGER NULL,
                    source_event_id INTEGER NULL,
                    FOREIGN KEY (world_id) REFERENCES worlds(id),
                    FOREIGN KEY (actor_id) REFERENCES actors(id),
                    FOREIGN KEY (cause_id) REFERENCES causes(id),
                    FOREIGN KEY (action_id) REFERENCES actions(id),
                    FOREIGN KEY (old_block_id) REFERENCES blocks(id),
                    FOREIGN KEY (new_block_id) REFERENCES blocks(id)
                );

                CREATE INDEX ix_block_events_exact
                ON block_events (world_id, x, y, z, occurred_at_ms DESC);

                CREATE INDEX ix_block_events_radius
                ON block_events (world_id, chunk_x, chunk_z, occurred_at_ms DESC, x, y, z);

                CREATE INDEX ix_block_events_actor_time
                ON block_events (actor_id, occurred_at_ms DESC);

                CREATE INDEX ix_block_events_cause_time
                ON block_events (cause_id, occurred_at_ms DESC);
                """,
                cancellationToken).ConfigureAwait(false);

            await ExecuteAsync(
                connection,
                transaction,
                $"INSERT INTO schema_migrations(version, applied_at_ms) VALUES (1, {DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()});",
                cancellationToken).ConfigureAwait(false);
        }

        alreadyApplied = await MigrationExistsAsync(connection, transaction, 2, cancellationToken).ConfigureAwait(false);
        if (!alreadyApplied)
        {
            await ExecuteAsync(
                connection,
                transaction,
                """
                CREATE TABLE inventory_types (
                    id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    code TEXT NOT NULL COLLATE NOCASE UNIQUE
                );

                CREATE TABLE items (
                    id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    code TEXT NOT NULL COLLATE NOCASE UNIQUE
                );

                CREATE TABLE container_transactions (
                    id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    world_id INTEGER NOT NULL,
                    x INTEGER NOT NULL,
                    y INTEGER NOT NULL,
                    z INTEGER NOT NULL,
                    chunk_x INTEGER NOT NULL,
                    chunk_y INTEGER NOT NULL,
                    chunk_z INTEGER NOT NULL,
                    occurred_at_ms INTEGER NOT NULL,
                    actor_id INTEGER NOT NULL,
                    cause_id INTEGER NOT NULL,
                    inventory_type_id INTEGER NOT NULL,
                    container_label TEXT NULL,
                    before_snapshot_blob BLOB NULL,
                    after_snapshot_blob BLOB NULL,
                    flags INTEGER NOT NULL DEFAULT 0,
                    source_job_id INTEGER NULL,
                    FOREIGN KEY (world_id) REFERENCES worlds(id),
                    FOREIGN KEY (actor_id) REFERENCES actors(id),
                    FOREIGN KEY (cause_id) REFERENCES causes(id),
                    FOREIGN KEY (inventory_type_id) REFERENCES inventory_types(id)
                );

                CREATE TABLE container_transaction_lines (
                    id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    transaction_id INTEGER NOT NULL,
                    item_id INTEGER NOT NULL,
                    slot_id INTEGER NULL,
                    quantity_delta INTEGER NOT NULL,
                    before_quantity INTEGER NOT NULL,
                    after_quantity INTEGER NOT NULL,
                    stack_before_blob BLOB NULL,
                    stack_after_blob BLOB NULL,
                    FOREIGN KEY (transaction_id) REFERENCES container_transactions(id) ON DELETE CASCADE,
                    FOREIGN KEY (item_id) REFERENCES items(id)
                );

                CREATE INDEX ix_container_transactions_exact
                ON container_transactions (world_id, x, y, z, occurred_at_ms DESC);

                CREATE INDEX ix_container_transactions_radius
                ON container_transactions (world_id, chunk_x, chunk_z, occurred_at_ms DESC, x, y, z);

                CREATE INDEX ix_container_transactions_actor_time
                ON container_transactions (actor_id, occurred_at_ms DESC);

                CREATE INDEX ix_container_lines_item
                ON container_transaction_lines (item_id, transaction_id);
                """,
                cancellationToken).ConfigureAwait(false);

            await ExecuteAsync(
                connection,
                transaction,
                $"INSERT INTO schema_migrations(version, applied_at_ms) VALUES (2, {DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()});",
                cancellationToken).ConfigureAwait(false);
        }

        alreadyApplied = await MigrationExistsAsync(connection, transaction, 3, cancellationToken).ConfigureAwait(false);
        if (!alreadyApplied)
        {
            await ExecuteAsync(
                connection,
                transaction,
                """
                CREATE TABLE rollback_jobs (
                    id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    world_id INTEGER NOT NULL,
                    requested_by_actor_id INTEGER NOT NULL,
                    operation_code TEXT NOT NULL COLLATE NOCASE DEFAULT 'rollback',
                    filter_summary TEXT NOT NULL,
                    state_code TEXT NOT NULL COLLATE NOCASE,
                    created_at_ms INTEGER NOT NULL,
                    queued_at_ms INTEGER NULL,
                    started_at_ms INTEGER NULL,
                    completed_at_ms INTEGER NULL,
                    planned_block_count INTEGER NOT NULL DEFAULT 0,
                    planned_chunk_count INTEGER NOT NULL DEFAULT 0,
                    applied_block_count INTEGER NOT NULL DEFAULT 0,
                    conflict_block_count INTEGER NOT NULL DEFAULT 0,
                    failed_block_count INTEGER NOT NULL DEFAULT 0,
                    oldest_target_occurred_at_ms INTEGER NULL,
                    last_error TEXT NULL,
                    FOREIGN KEY (world_id) REFERENCES worlds(id),
                    FOREIGN KEY (requested_by_actor_id) REFERENCES actors(id)
                );

                CREATE TABLE rollback_job_entries (
                    id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    job_id INTEGER NOT NULL,
                    target_type_code TEXT NOT NULL COLLATE NOCASE,
                    target_event_id INTEGER NOT NULL,
                    execution_order INTEGER NOT NULL,
                    result_code TEXT NOT NULL COLLATE NOCASE DEFAULT 'planned',
                    applied_at_ms INTEGER NULL,
                    error_text TEXT NULL,
                    FOREIGN KEY (job_id) REFERENCES rollback_jobs(id) ON DELETE CASCADE
                );

                CREATE INDEX ix_rollback_jobs_state
                ON rollback_jobs (state_code, created_at_ms DESC);

                CREATE INDEX ix_rollback_job_entries_job_order
                ON rollback_job_entries (job_id, execution_order);

                CREATE INDEX ix_rollback_job_entries_job_result
                ON rollback_job_entries (job_id, result_code, execution_order);
                """,
                cancellationToken).ConfigureAwait(false);

            await ExecuteAsync(
                connection,
                transaction,
                $"INSERT INTO schema_migrations(version, applied_at_ms) VALUES (3, {DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()});",
                cancellationToken).ConfigureAwait(false);
        }

        alreadyApplied = await MigrationExistsAsync(connection, transaction, 4, cancellationToken).ConfigureAwait(false);
        if (!alreadyApplied)
        {
            var hasConflictCountColumn = await ColumnExistsAsync(connection, transaction, "rollback_jobs", "conflict_block_count", cancellationToken).ConfigureAwait(false);
            if (!hasConflictCountColumn)
            {
                await ExecuteAsync(
                    connection,
                    transaction,
                    """
                    ALTER TABLE rollback_jobs ADD COLUMN conflict_block_count INTEGER NOT NULL DEFAULT 0;
                    """,
                    cancellationToken).ConfigureAwait(false);
            }

            await ExecuteAsync(
                connection,
                transaction,
                $"INSERT INTO schema_migrations(version, applied_at_ms) VALUES (4, {DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()});",
                cancellationToken).ConfigureAwait(false);
        }

        alreadyApplied = await MigrationExistsAsync(connection, transaction, 5, cancellationToken).ConfigureAwait(false);
        if (!alreadyApplied)
        {
            var hasOperationCodeColumn = await ColumnExistsAsync(connection, transaction, "rollback_jobs", "operation_code", cancellationToken).ConfigureAwait(false);
            if (!hasOperationCodeColumn)
            {
                await ExecuteAsync(
                    connection,
                    transaction,
                    """
                    ALTER TABLE rollback_jobs ADD COLUMN operation_code TEXT NOT NULL COLLATE NOCASE DEFAULT 'rollback';
                    """,
                    cancellationToken).ConfigureAwait(false);
            }

            await ExecuteAsync(
                connection,
                transaction,
                $"INSERT INTO schema_migrations(version, applied_at_ms) VALUES (5, {DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()});",
                cancellationToken).ConfigureAwait(false);
        }

        alreadyApplied = await MigrationExistsAsync(connection, transaction, 6, cancellationToken).ConfigureAwait(false);
        if (!alreadyApplied)
        {
            await ExecuteAsync(
                connection,
                transaction,
                """
                DROP INDEX IF EXISTS ix_block_events_actor_time;
                DROP INDEX IF EXISTS ix_block_events_cause_time;
                DROP INDEX IF EXISTS ix_container_transactions_actor_time;

                CREATE INDEX IF NOT EXISTS ix_block_events_actor_lookup
                ON block_events (world_id, actor_id, chunk_x, chunk_z, occurred_at_ms DESC);

                CREATE INDEX IF NOT EXISTS ix_block_events_cause_lookup
                ON block_events (world_id, cause_id, chunk_x, chunk_z, occurred_at_ms DESC);

                CREATE INDEX IF NOT EXISTS ix_block_events_action_lookup
                ON block_events (world_id, action_id, chunk_x, chunk_z, occurred_at_ms DESC);

                CREATE INDEX IF NOT EXISTS ix_container_transactions_actor_lookup
                ON container_transactions (world_id, actor_id, chunk_x, chunk_z, occurred_at_ms DESC);

                CREATE INDEX IF NOT EXISTS ix_container_transactions_cause_lookup
                ON container_transactions (world_id, cause_id, chunk_x, chunk_z, occurred_at_ms DESC);
                """,
                cancellationToken).ConfigureAwait(false);

            await ExecuteAsync(
                connection,
                transaction,
                $"INSERT INTO schema_migrations(version, applied_at_ms) VALUES (6, {DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()});",
                cancellationToken).ConfigureAwait(false);
        }

        alreadyApplied = await MigrationExistsAsync(connection, transaction, 7, cancellationToken).ConfigureAwait(false);
        if (!alreadyApplied)
        {
            if (!await ColumnExistsAsync(connection, transaction, "rollback_jobs", "planned_container_count", cancellationToken).ConfigureAwait(false))
            {
                await ExecuteAsync(connection, transaction, "ALTER TABLE rollback_jobs ADD COLUMN planned_container_count INTEGER NOT NULL DEFAULT 0;", cancellationToken).ConfigureAwait(false);
            }

            if (!await ColumnExistsAsync(connection, transaction, "rollback_jobs", "applied_container_count", cancellationToken).ConfigureAwait(false))
            {
                await ExecuteAsync(connection, transaction, "ALTER TABLE rollback_jobs ADD COLUMN applied_container_count INTEGER NOT NULL DEFAULT 0;", cancellationToken).ConfigureAwait(false);
            }

            if (!await ColumnExistsAsync(connection, transaction, "rollback_jobs", "conflict_container_count", cancellationToken).ConfigureAwait(false))
            {
                await ExecuteAsync(connection, transaction, "ALTER TABLE rollback_jobs ADD COLUMN conflict_container_count INTEGER NOT NULL DEFAULT 0;", cancellationToken).ConfigureAwait(false);
            }

            if (!await ColumnExistsAsync(connection, transaction, "rollback_jobs", "failed_container_count", cancellationToken).ConfigureAwait(false))
            {
                await ExecuteAsync(connection, transaction, "ALTER TABLE rollback_jobs ADD COLUMN failed_container_count INTEGER NOT NULL DEFAULT 0;", cancellationToken).ConfigureAwait(false);
            }

            await ExecuteAsync(
                connection,
                transaction,
                $"INSERT INTO schema_migrations(version, applied_at_ms) VALUES (7, {DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()});",
                cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> MigrationExistsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int version,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM schema_migrations WHERE version = $version);";
        command.Parameters.AddWithValue("$version", version);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is long value && value == 1;
    }

    private static async Task<bool> ColumnExistsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tableName,
        string columnName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA table_info({tableName});";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (string.Equals(reader.GetString(reader.GetOrdinal("name")), columnName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
