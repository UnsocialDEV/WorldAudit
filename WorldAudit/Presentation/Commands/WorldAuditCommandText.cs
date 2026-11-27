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
        [WA] WorldAudit commands
        /wa inspect — Toggle inspection, then interact with a location to read its history.
        /wa i details <id> [page] — Read individual transactions from a storage entry.
        /wa lookup [t:<time>] [r:<radius>] [u:<user|#cause>] [a:<action>] [i:<codes>] [e:<codes>] [-b|-c] — Search audit history.
        /wa near [t:<time>] [u:<user|#cause>] [a:<action>] [i:<codes>] [e:<codes>] [-b|-c] — Search nearby history.
        /wa rollback [t:<time>] [r:<radius>] [u:<user|#cause>] [a:<action>] [i:<codes>] [e:<codes>] [-b|-c] — Preview reversal of matching changes.
        Rollback skips earlier jobs by default. Use u:#rollback or u:#restore to select their changes.
        /wa restore [t:<time>] [r:<radius>] [u:<user|#cause>] [a:<action>] [i:<codes>] [e:<codes>] [-b|-c] — Preview replay of matching changes.
        /wa undo <jobId> — Preview reversal of a completed job.
        /wa apply <jobId> — Apply or resume a previewed job.
        /wa cancel <jobId> — Discard remaining job work.
        /wa jobs — List recent jobs.
        /wa job <jobId> [blocks|storage] [page] — Review a job and its frozen selection.
        /wa job <jobId> storage details <id> [page] — Review selected transactions.
        /wa status — Show storage and writer health.
        /wa reload — Reload configuration.
        /wa consumer <pause|resume> — Control audit writes.
        /wa purge t:<age> — Preview removal of old audit history.
        /wa purge confirm t:<age> — Delete audit history older than the specified age.
        """;

    public static string EscapeChatText(string text) => text.Replace("<", "&lt;").Replace(">", "&gt;");

    public static BlockPosition GetPlayerBlockPosition(IServerPlayer player)
    {
        var pos = player.Entity?.Pos?.AsBlockPos;
        if (pos is null)
        {
            return new BlockPosition(0, 0, 0);
        }

        return new BlockPosition(pos.X, pos.InternalY, pos.Z);
    }

    public static string FormatDisplayPosition(ICoreServerAPI api, BlockPos position)
    {
        var localPosition = position.Copy().ToLocalPosition(api);
        return $"{localPosition.X}, {localPosition.Y}, {localPosition.Z}";
    }

    public static string FormatDisplayPosition(ICoreServerAPI api, BlockPosition position)
    {
        return FormatDisplayPosition(api, new BlockPos(position.X, position.LocalY, position.Z, position.Dimension));
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
        IReadOnlyList<ContainerInspectEntry> containerResults)
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

        rows.AddRange(containerResults.Select(entry => ContainerInspectSummary.Format(entry, now)));

        return rows
            .OrderByDescending(row => row.OccurredAt)
            .Select((row, index) => row with { EventNumber = index + 1 })
            .ToList();
    }

    internal static string JobSelection(RollbackJob job) =>
        $"[WA] Full selection: {job.PlannedBlockCount} block edits · {job.PlannedVisitCount} storage visits (including standalone changes) · {job.PlannedContainerCount} storage transactions\n" +
        $"Browse /wa job {job.Id} blocks or /wa job {job.Id} storage. Apply covers the entire selection.";

    public static void SendPreviewReady(IServerPlayer player, RollbackJob job, int groupId)
    {
        player.SendAuditLine(groupId, $"[WA] Preview {DescribeOperation(job.Operation)} job #{job.Id} ready.", EnumChatType.CommandSuccess);
        player.SendAuditLine(groupId, $"[WA] Selection: {job.FilterSummary}", EnumChatType.CommandSuccess);
        player.SendAuditLine(
            groupId,
            $"[WA] Chunks: {job.PlannedChunkCount}\n[WA] Oldest edit: {FormatRelativeTime(job.OldestTargetOccurredAt, DateTimeOffset.UtcNow)}",
            EnumChatType.CommandSuccess);
        player.SendAuditLine(groupId, JobSelection(job), EnumChatType.CommandSuccess);
        player.SendAuditLine(groupId, $"[WA] Use /wa job {job.Id} to inspect, /wa apply {job.Id} to apply, or /wa cancel {job.Id} to discard.", EnumChatType.CommandSuccess);
    }

    public static IEnumerable<string> FormatStatus(AuditStatusSnapshot status)
    {
        yield return "[WA] Status";
        yield return $"[WA] DB: {status.DatabasePath}";
        yield return status.StorageError is null
            ? $"[WA] SQLite: {status.SqliteVersion}\n[WA] WAL: {FormatBytes(status.WalSizeBytes)}"
            : $"[WA] Storage unavailable: {status.StorageError}";
        yield return $"[WA] Consumer: {(status.WriterSnapshot.LastError is not null ? "faulted" : status.IsConsumerPaused ? "paused" : "running")}\n[WA] Queue: {status.WriterSnapshot.PendingEvents} pending\n[WA] Persisted: {status.WriterSnapshot.PersistedEvents}";
        if (status.WriterSnapshot.RejectedEvents > 0)
            yield return $"[WA] Audit history incomplete: {status.WriterSnapshot.RejectedEvents} record(s) lost since startup.";
        if (status.WriterSnapshot.LastError is not null)
        {
            yield return $"[WA] Writer error: {status.WriterSnapshot.LastError}";
        }
        yield return $"[WA] Flush: {status.WriterSnapshot.FlushCount} total | last {status.WriterSnapshot.LastFlushBatchSize} in {status.WriterSnapshot.LastFlushDuration.TotalMilliseconds:0.0} ms\n[WA] largest batch {status.WriterSnapshot.LargestFlushBatchSize}";
        var walThreshold = status.StorageError is not null ? "unavailable" : status.IsWalThresholdExceeded ? "exceeded" : "ok";
        yield return $"[WA] Slow paths: queries {status.QueryPerformance.SlowQueryCount}\n[WA] flushes {status.WriterSnapshot.SlowFlushCount}\n[WA] WAL threshold {walThreshold}";
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
        yield return $"[WA] Block events: {preview.BlockEventCount}\n[WA] Container transactions: {preview.ContainerTransactionCount}\n[WA] Rollback jobs: {preview.RollbackJobCount}";
        yield return $"[WA] Total candidates: {preview.TotalCount}\n[WA] Confirm with /wa purge confirm t:{FormatAgeToken(age)}";
    }

    public static IEnumerable<string> FormatPurgeResult(PurgeResult result)
    {
        yield return $"[WA] Purge complete for records older than {result.Cutoff:O}.";
        yield return $"[WA] Deleted block events: {result.DeletedBlockEventCount}\n[WA] container transactions: {result.DeletedContainerTransactionCount}\n[WA] rollback jobs: {result.DeletedRollbackJobCount}";
        yield return $"[WA] SQLite maintenance: checkpoint {(result.CheckpointRan ? "ok" : "skipped")} | optimize {(result.OptimizeRan ? "ok" : "skipped")}";
        yield return $"[WA] Purged {result.TotalDeletedCount} record(s).";
    }

    internal static string DescribeInspectBlockAction(BlockAuditEvent auditEvent)
    {
        return auditEvent.Action switch
        {
            BlockAuditAction.Place => $"Placed {FriendlyName(auditEvent.NewBlockCode)}",
            BlockAuditAction.Break => $"Broke {FriendlyName(auditEvent.OldBlockCode)}",
            BlockAuditAction.Restore => $"Restored {FriendlyName(auditEvent.NewBlockCode)}",
            _ => $"Replaced {FriendlyName(auditEvent.OldBlockCode)} with {FriendlyName(auditEvent.NewBlockCode)}"
        };
    }

    internal static string DescribeContainerLines(IReadOnlyList<ContainerAuditLine> lines)
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

    internal static string FriendlyName(string? code)
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
