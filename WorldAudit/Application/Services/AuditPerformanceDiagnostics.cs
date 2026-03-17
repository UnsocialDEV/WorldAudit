using WorldAudit.Application.Configuration;

namespace WorldAudit.Application.Services;

public sealed class AuditPerformanceDiagnostics
{
    private readonly WorldAuditOptions _options;
    private readonly Action<string>? _logger;
    private readonly object _sync = new();
    private long _queryCount;
    private int _slowQueryCount;
    private string? _lastQueryOperation;
    private TimeSpan _lastQueryDuration;
    private string? _slowestQueryOperation;
    private TimeSpan _slowestQueryDuration;
    private AuditCheckpointStatus? _lastCheckpoint;
    private AuditOptimizeStatus? _lastOptimize;

    public AuditPerformanceDiagnostics(WorldAuditOptions options, Action<string>? logger = null)
    {
        _options = options;
        _logger = logger;
    }

    public void RecordQuery(string operation, TimeSpan duration)
    {
        lock (_sync)
        {
            _queryCount++;
            _lastQueryOperation = operation;
            _lastQueryDuration = duration;

            if (duration > _slowestQueryDuration)
            {
                _slowestQueryDuration = duration;
                _slowestQueryOperation = operation;
            }

            if (duration.TotalMilliseconds >= _options.SlowQueryThresholdMilliseconds)
            {
                _slowQueryCount++;
                Log($"Slow query '{operation}' took {duration.TotalMilliseconds:0.0} ms.");
            }
        }
    }

    public void RecordCheckpoint(string reason, SqliteCheckpointResult result, TimeSpan duration)
    {
        var status = new AuditCheckpointStatus(
            DateTimeOffset.UtcNow,
            result.Mode,
            reason,
            duration,
            result.WalSizeBytesBefore,
            result.WalSizeBytesAfter);

        lock (_sync)
        {
            _lastCheckpoint = status;
        }

        Log($"Checkpoint {result.Mode} ({reason}) took {duration.TotalMilliseconds:0.0} ms. WAL {result.WalSizeBytesBefore} -> {result.WalSizeBytesAfter} bytes.");
    }

    public void RecordOptimize(string reason, TimeSpan duration)
    {
        var status = new AuditOptimizeStatus(DateTimeOffset.UtcNow, duration, reason);

        lock (_sync)
        {
            _lastOptimize = status;
        }

        Log($"Optimize ({reason}) took {duration.TotalMilliseconds:0.0} ms.");
    }

    public AuditQueryPerformanceSnapshot SnapshotQueries()
    {
        lock (_sync)
        {
            return new AuditQueryPerformanceSnapshot(
                _queryCount,
                _slowQueryCount,
                _lastQueryOperation,
                _lastQueryDuration,
                _slowestQueryOperation,
                _slowestQueryDuration);
        }
    }

    public AuditCheckpointStatus? SnapshotCheckpoint()
    {
        lock (_sync)
        {
            return _lastCheckpoint;
        }
    }

    public AuditOptimizeStatus? SnapshotOptimize()
    {
        lock (_sync)
        {
            return _lastOptimize;
        }
    }

    public void LogVerbose(string message)
    {
        Log(message);
    }

    private void Log(string message)
    {
        if (!_options.VerboseDiagnostics || _logger is null)
        {
            return;
        }

        _logger($"[WorldAudit] {message}");
    }
}
