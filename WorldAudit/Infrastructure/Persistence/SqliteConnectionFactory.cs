using Microsoft.Data.Sqlite;
using SQLitePCL;
using WorldAudit.Application.Configuration;

namespace WorldAudit.Infrastructure.Persistence;

public sealed class SqliteConnectionFactory
{
    private static readonly Lazy<bool> ProviderInitialization = new(InitializeProvider);
    private readonly string _connectionString;
    private readonly WorldAuditOptions _options;

    public SqliteConnectionFactory(WorldAuditOptions options)
    {
        _ = ProviderInitialization.Value;
        _options = options;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = options.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
    }

    public string DatabasePath => _options.DatabasePath;

    public string WalPath => $"{_options.DatabasePath}-wal";

    public Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
        => OpenConnectionAsync(cancellationToken, commandTimeoutSeconds: null);

    internal async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken, int? commandTimeoutSeconds)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_options.DatabasePath) ?? AppContext.BaseDirectory);

        var connection = new SqliteConnection(_connectionString);
        if (commandTimeoutSeconds.HasValue)
        {
            connection.DefaultTimeout = commandTimeoutSeconds.Value;
        }

        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await ConfigureConnectionAsync(connection, cancellationToken, commandTimeoutSeconds).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task ConfigureConnectionAsync(SqliteConnection connection, CancellationToken cancellationToken, int? commandTimeoutSeconds)
    {
        var busyTimeout = commandTimeoutSeconds.HasValue
            ? Math.Min(_options.SqliteBusyTimeoutMilliseconds, commandTimeoutSeconds.Value * 1000)
            : _options.SqliteBusyTimeoutMilliseconds;
        using var command = connection.CreateCommand();
        command.CommandText =
            $$"""
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            PRAGMA foreign_keys=ON;
            PRAGMA temp_store=MEMORY;
            PRAGMA busy_timeout={{busyTimeout}};
            """;

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static bool InitializeProvider()
    {
        SqliteNativeLibraryBootstrapper.EnsureLoaded(Path.GetDirectoryName(typeof(SqliteConnectionFactory).Assembly.Location) ?? AppContext.BaseDirectory);
        Batteries_V2.Init();
        return true;
    }
}
