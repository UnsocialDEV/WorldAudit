using System.Globalization;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using WorldAudit.Application;
using WorldAudit.Domain;
using WorldAudit.Presentation.Chat;

namespace WorldAudit.Presentation.Commands;

internal static class WorldAuditCommandText
{
    public const string HelpText =
        """
        [WA] /wa inspect - toggle inspect mode
        [WA] /wa lookup [t:<time>] [r:<radius>] [u:<user|#cause>] [a:<action>] [i:<codes>] [e:<codes>] [-b|-c]
        [WA] /wa near [t:<time>] [u:<user|#cause>] [a:<action>] [i:<codes>] [e:<codes>] [-b|-c]
        [WA] /wa rollback [t:<time>] [r:<radius>] [u:<user|#cause>] [a:<action>] [i:<codes>] [e:<codes>] [-b|-c]
        [WA] /wa restore [t:<time>] [r:<radius>] [u:<user|#cause>] [a:<action>] [i:<codes>] [e:<codes>] [-b|-c]
        [WA] /wa undo <jobId>
        [WA] /wa apply <jobId> | /wa jobs | /wa job <jobId>
        [WA] /wa status | /wa reload | /wa consumer <pause|resume>
        [WA] /wa purge t:<age> | /wa purge confirm t:<age>
        """;

    public static BlockPosition GetPlayerBlockPosition(IServerPlayer player)
    {
        var pos = player.Entity?.Pos?.AsBlockPos;
        if (pos is null)
        {
            return new BlockPosition(0, 0, 0);
        }

        return new BlockPosition(pos.X, pos.Y, pos.Z);
    }

    public static string FormatDisplayPosition(ICoreServerAPI api, BlockPos position)
    {
        var localPosition = position.Copy().ToLocalPosition(api);
        return $"{localPosition.X}, {localPosition.Y}, {localPosition.Z}";
    }

    public static string FormatDisplayPosition(ICoreServerAPI api, BlockPosition position)
    {
        var localPosition = new BlockPos(position.X, position.Y, position.Z).ToLocalPosition(api);
        return $"{localPosition.X}, {localPosition.Y}, {localPosition.Z}";
    }

    public static IEnumerable<string> GetQueryTokens(TextCommandCallingArgs args)
    {
        if (args.ArgCount == 0)
        {
            return [];
        }

        var raw = args[0] as string ?? args.LastArg as string;
        return string.IsNullOrWhiteSpace(raw)
            ? []
            : raw.Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    }

    public static bool TryParseJobId(TextCommandCallingArgs args, out long jobId)
    {
        var raw = args.ArgCount == 0
            ? null
            : args[0] as string ?? args.LastArg as string;
        return long.TryParse(raw, out jobId);
    }

    public static string DescribeOperation(RollbackJobOperation operation)
    {
        return operation == RollbackJobOperation.Rollback ? "rollback" : "restore";
    }

    public static string ToTitleCase(RollbackJobOperation operation)
    {
        return operation == RollbackJobOperation.Rollback ? "Rollback" : "Restore";
    }

    public static string FormatRelativeTime(DateTimeOffset? occurredAt, DateTimeOffset now)
    {
        if (occurredAt is null)
        {
            return "n/a";
        }

        var delta = now - occurredAt.Value;
        if (delta < TimeSpan.FromMinutes(1))
        {
            return "just now";
        }

        if (delta < TimeSpan.FromHours(1))
        {
            return $"{Math.Max(1, (int)delta.TotalMinutes)}m ago";
        }

        if (delta < TimeSpan.FromDays(1))
        {
            return $"{Math.Max(1, (int)delta.TotalHours)}h ago";
        }

        return $"{Math.Max(1, (int)delta.TotalDays)}d ago";
    }

    public static IReadOnlyList<InspectChatRow> BuildInspectRows(
        IReadOnlyList<BlockAuditEvent> blockResults,
        IReadOnlyList<ContainerAuditTransaction> containerResults)
    {
        var rows = new List<InspectChatRow>(blockResults.Count + containerResults.Count);
        var now = DateTimeOffset.UtcNow;

        rows.AddRange(blockResults.Select(auditEvent => new InspectChatRow(
            auditEvent.OccurredAt,
            0,
            "Block",
            FormatRelativeTime(auditEvent.OccurredAt, now),
            auditEvent.Actor.Name,
            ToDisplayText(auditEvent.Cause),
            DescribeInspectBlockAction(auditEvent))));

        rows.AddRange(containerResults.Select(transaction => new InspectChatRow(
            transaction.OccurredAt,
            0,
            FriendlyName(transaction.ContainerLabel ?? transaction.InventoryType),
            FormatRelativeTime(transaction.OccurredAt, now),
            transaction.Actor.Name,
            ToDisplayText(transaction.Cause),
            DescribeInspectContainerAction(transaction))));

        return rows
            .OrderByDescending(row => row.OccurredAt)
            .Select((row, index) => row with { EventNumber = index + 1 })
            .ToList();
    }

    public static void SendPreviewReady(IServerPlayer player, RollbackJob job)
    {
        player.SendMessage(0, $"[WA] Preview {DescribeOperation(job.Operation)} job #{job.Id} ready.", EnumChatType.CommandSuccess, null);
        player.SendMessage(
            0,
            $"[WA] Blocks: {job.PlannedBlockCount} | Containers: {job.PlannedContainerCount} | Chunks: {job.PlannedChunkCount} | Oldest edit: {FormatRelativeTime(job.OldestTargetOccurredAt, DateTimeOffset.UtcNow)}",
            EnumChatType.CommandSuccess,
            null);
        player.SendMessage(0, $"[WA] Use /wa job {job.Id} to inspect or /wa apply {job.Id} to apply.", EnumChatType.CommandSuccess, null);
    }

    public static IEnumerable<string> FormatStatus(AuditStatusSnapshot status)
    {
        yield return "[WA] Status";
        yield return $"[WA] DB: {status.DatabasePath}";
        yield return $"[WA] SQLite: {status.SqliteVersion} | WAL: {FormatBytes(status.WalSizeBytes)}";
        yield return $"[WA] Consumer: {(status.IsConsumerPaused ? "paused" : "running")} | Queue: {status.WriterSnapshot.PendingEvents} pending | Persisted: {status.WriterSnapshot.PersistedEvents}";
        yield return $"[WA] Flush: {status.WriterSnapshot.FlushCount} total | last {status.WriterSnapshot.LastFlushBatchSize} in {status.WriterSnapshot.LastFlushDuration.TotalMilliseconds:0.0} ms | largest batch {status.WriterSnapshot.LargestFlushBatchSize}";
        yield return $"[WA] Slow paths: queries {status.QueryPerformance.SlowQueryCount} | flushes {status.WriterSnapshot.SlowFlushCount} | WAL threshold {(status.IsWalThresholdExceeded ? "exceeded" : "ok")}";
        yield return $"[WA] Policy: checkpoint every {status.CheckpointPolicy.CheckpointInterval.TotalMinutes:0}m or {FormatBytes(status.CheckpointPolicy.WalSizeThresholdBytes)} WAL | optimize every {status.CheckpointPolicy.OptimizeInterval.TotalHours:0}h";
        yield return status.LastCheckpoint is null
            ? "[WA] Last checkpoint: none"
            : $"[WA] Last checkpoint: {status.LastCheckpoint.Mode.ToString().ToLowerInvariant()} | {status.LastCheckpoint.Reason} | {status.LastCheckpoint.Duration.TotalMilliseconds:0.0} ms";
        yield return status.LastOptimize is null
            ? "[WA] Last optimize: none"
            : $"[WA] Last optimize: {status.LastOptimize.Reason} | {status.LastOptimize.Duration.TotalMilliseconds:0.0} ms";
        yield return $"[WA] Last query: {(status.QueryPerformance.LastOperation ?? "none")} | {status.QueryPerformance.LastDuration.TotalMilliseconds:0.0} ms | slowest {(status.QueryPerformance.SlowestOperation ?? "none")} {status.QueryPerformance.SlowestDuration.TotalMilliseconds:0.0} ms";
        yield return $"[WA] Active jobs: {status.ActiveRollbackJobCount} | Retention: {(status.RetentionDays > 0 ? $"{status.RetentionDays}d" : "disabled")}";
        yield return status.LastMaintenanceRun is null
            ? "[WA] Last maintenance: none"
            : $"[WA] Last maintenance: {(status.LastMaintenanceRun.Succeeded ? "ok" : "failed")} | {status.LastMaintenanceRun.Summary}";
        yield return "[WA] Status displayed.";
    }

    public static IEnumerable<string> FormatPurgePreview(PurgePreview preview, TimeSpan age)
    {
        yield return $"[WA] Purge preview for records older than {FormatAge(age)}.";
        yield return $"[WA] Cutoff: {preview.Cutoff:O}";
        yield return $"[WA] Block events: {preview.BlockEventCount} | Container transactions: {preview.ContainerTransactionCount} | Rollback jobs: {preview.RollbackJobCount}";
        yield return $"[WA] Total candidates: {preview.TotalCount} | Confirm with /wa purge confirm t:{FormatAgeToken(age)}";
    }

    public static IEnumerable<string> FormatPurgeResult(PurgeResult result)
    {
        yield return $"[WA] Purge complete for records older than {result.Cutoff:O}.";
        yield return $"[WA] Deleted block events: {result.DeletedBlockEventCount} | container transactions: {result.DeletedContainerTransactionCount} | rollback jobs: {result.DeletedRollbackJobCount}";
        yield return $"[WA] SQLite maintenance: checkpoint {(result.CheckpointRan ? "ok" : "skipped")} | optimize {(result.OptimizeRan ? "ok" : "skipped")}";
        yield return $"[WA] Purged {result.TotalDeletedCount} record(s).";
    }

    private static string DescribeInspectBlockAction(BlockAuditEvent auditEvent)
    {
        return auditEvent.Action switch
        {
            BlockAuditAction.Place => $"Placed {FriendlyName(auditEvent.NewBlockCode)}",
            BlockAuditAction.Break => $"Broke {FriendlyName(auditEvent.OldBlockCode)}",
            BlockAuditAction.Restore => $"Restored {FriendlyName(auditEvent.NewBlockCode)}",
            _ => $"Replaced {FriendlyName(auditEvent.OldBlockCode)} with {FriendlyName(auditEvent.NewBlockCode)}"
        };
    }

    private static string DescribeInspectContainerAction(ContainerAuditTransaction transaction)
    {
        var label = FriendlyName(transaction.ContainerLabel ?? transaction.InventoryType);
        return $"{label} changed: {DescribeContainerLines(transaction.Lines)}";
    }

    private static string DescribeContainerLines(IReadOnlyList<ContainerAuditLine> lines)
    {
        if (lines.Count == 0)
        {
            return "no item delta";
        }

        return string.Join("; ", lines.Select(line =>
        {
            var quantity = line.QuantityDelta > 0 ? $"+{line.QuantityDelta}" : line.QuantityDelta.ToString(CultureInfo.InvariantCulture);
            var slot = line.SlotId.HasValue ? $" (slot {line.SlotId.Value})" : string.Empty;
            return $"{quantity} {FriendlyName(line.ItemCode)}{slot}";
        }));
    }

    private static string FriendlyName(string? code)
    {
        if (string.IsNullOrWhiteSpace(code) || code.EndsWith(":air", StringComparison.OrdinalIgnoreCase))
        {
            return "Air";
        }

        var value = code.Split(':', 2).Last().Replace('-', ' ').Replace('_', ' ');
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(value);
    }

    private static string ToDisplayText(Enum value)
    {
        var raw = value.ToString().Replace('_', ' ');
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(raw.ToLowerInvariant());
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        if (bytes < 1024 * 1024)
        {
            return $"{bytes / 1024d:0.0} KiB";
        }

        return $"{bytes / (1024d * 1024d):0.0} MiB";
    }

    private static string FormatAge(TimeSpan age)
    {
        if (age.TotalDays >= 1 && age.TotalDays % 1 == 0)
        {
            return $"{age.TotalDays:0} day(s)";
        }

        if (age.TotalHours >= 1 && age.TotalHours % 1 == 0)
        {
            return $"{age.TotalHours:0} hour(s)";
        }

        if (age.TotalMinutes >= 1 && age.TotalMinutes % 1 == 0)
        {
            return $"{age.TotalMinutes:0} minute(s)";
        }

        return $"{age.TotalSeconds:0} second(s)";
    }

    private static string FormatAgeToken(TimeSpan age)
    {
        if (age.TotalDays >= 7 && age.TotalDays % 7 == 0)
        {
            return $"{age.TotalDays / 7:0}w";
        }

        if (age.TotalDays >= 1 && age.TotalDays % 1 == 0)
        {
            return $"{age.TotalDays:0}d";
        }

        if (age.TotalHours >= 1 && age.TotalHours % 1 == 0)
        {
            return $"{age.TotalHours:0}h";
        }

        if (age.TotalMinutes >= 1 && age.TotalMinutes % 1 == 0)
        {
            return $"{age.TotalMinutes:0}m";
        }

        return $"{age.TotalSeconds:0}s";
    }
}
