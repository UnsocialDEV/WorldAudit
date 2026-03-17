using Microsoft.Data.Sqlite;
using WorldAudit.Domain;

namespace WorldAudit.Infrastructure.Persistence;

internal static class SqliteAuditValueCodec
{
    public static string ToCode(AuditCause cause) => cause.ToString().ToLowerInvariant();

    public static string ToCode(BlockAuditAction action) => action.ToString().ToLowerInvariant();

    public static string ToCode(RollbackJobOperation operation) => operation.ToString().ToLowerInvariant();

    public static string ToCode(RollbackJobState state) => state.ToString().ToLowerInvariant();

    public static string ToCode(RollbackJobEntryResult result) => result.ToString().ToLowerInvariant();

    public static string ToCode(RollbackJobEntryTargetType targetType) => targetType.ToString().ToLowerInvariant();

    public static AuditCause ParseCause(string code)
    {
        return Enum.TryParse<AuditCause>(code, true, out var cause)
            ? cause
            : AuditCause.Unknown;
    }

    public static BlockAuditAction ParseAction(string code)
    {
        return Enum.TryParse<BlockAuditAction>(code, true, out var action)
            ? action
            : BlockAuditAction.Replace;
    }

    public static RollbackJobState ParseRollbackJobState(string code)
    {
        return Enum.TryParse<RollbackJobState>(code, true, out var state)
            ? state
            : RollbackJobState.Failed;
    }

    public static RollbackJobOperation ParseRollbackJobOperation(string code)
    {
        return Enum.TryParse<RollbackJobOperation>(code, true, out var operation)
            ? operation
            : RollbackJobOperation.Rollback;
    }

    public static RollbackJobEntryResult ParseRollbackJobEntryResult(string code)
    {
        return Enum.TryParse<RollbackJobEntryResult>(code, true, out var result)
            ? result
            : RollbackJobEntryResult.Failed;
    }

    public static RollbackJobEntryTargetType ParseRollbackJobEntryTargetType(string code)
    {
        return Enum.TryParse<RollbackJobEntryTargetType>(code, true, out var targetType)
            ? targetType
            : RollbackJobEntryTargetType.Block;
    }

    public static byte[]? ReadBlob(SqliteDataReader reader, string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);
        return reader.IsDBNull(ordinal) ? null : (byte[])reader.GetValue(ordinal);
    }

    public static DateTimeOffset? ReadNullableUnixMilliseconds(SqliteDataReader reader, string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);
        return reader.IsDBNull(ordinal)
            ? null
            : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(ordinal));
    }
}
