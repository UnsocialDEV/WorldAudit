using Vintagestory.API.Common;
using Vintagestory.API.Server;
using WorldAudit.Domain;

namespace WorldAudit.Presentation.Commands;

internal sealed class WorldAuditRollbackCommandHandler
{
    private readonly WorldAuditCommandContext _context;

    public WorldAuditRollbackCommandHandler(WorldAuditCommandContext context)
    {
        _context = context;
    }

    public TextCommandResult HandleRollback(TextCommandCallingArgs args) => HandlePreview(args, RollbackJobOperation.Rollback);

    public TextCommandResult HandleRestore(TextCommandCallingArgs args) => HandlePreview(args, RollbackJobOperation.Restore);

    public TextCommandResult HandleUndo(TextCommandCallingArgs args)
    {
        return _context.ExecuteSafely(args, "undo preview", () =>
        {
            if (args.Caller?.Player is not IServerPlayer player)
            {
                return TextCommandResult.Error("This command requires a player caller.", "not_player");
            }

            if (!WorldAuditCommandText.TryParseJobId(args, out var sourceJobId))
            {
                return TextCommandResult.Error("[WA] Provide a numeric job id, for example /wa undo 12.", "bad_job");
            }

            var planner = _context.Runtime.RollbackPlanner;
            var actor = new AuditActor(player.PlayerName, player.PlayerUID);
            return _context.Background.Run(player, "undo preview", () => planner.PreviewUndoAsync(sourceJobId, actor),
                job => WorldAuditCommandText.SendPreviewReady(player, job, _context.AuditGroup(player)));
        });
    }

    public TextCommandResult HandleApply(TextCommandCallingArgs args)
    {
        return _context.ExecuteSafely(args, "apply rollback job", () =>
        {
            if (args.Caller?.Player is not IServerPlayer player)
            {
                return TextCommandResult.Error("This command requires a player caller.", "not_player");
            }

            if (!WorldAuditCommandText.TryParseJobId(args, out var jobId))
            {
                return TextCommandResult.Error("[WA] Provide a numeric job id, for example /wa apply 12.", "bad_job");
            }

            var job = _context.Runtime.RollbackCoordinator.QueueApplyAsync(jobId).GetAwaiter().GetResult();
            if (job is null || job.WorldId != _context.GetWorldId())
            {
                return TextCommandResult.Error($"[WA] Job #{jobId} was not found.", "job_missing");
            }

            player.SendAuditLine(_context.AuditGroup(player), $"[WA] Job #{job.Id} is {job.State.ToString().ToLowerInvariant()}.", EnumChatType.CommandSuccess);
            var message = job.State switch
            {
                RollbackJobState.Queued or RollbackJobState.Running => $"[WA] {WorldAuditCommandText.ToTitleCase(job.Operation)} job #{job.Id} queued.",
                RollbackJobState.Completed or RollbackJobState.CompletedWithConflicts => $"[WA] {WorldAuditCommandText.ToTitleCase(job.Operation)} job #{job.Id} has already finished.",
                RollbackJobState.Failed => $"[WA] {WorldAuditCommandText.ToTitleCase(job.Operation)} job #{job.Id} failed.",
                _ => $"[WA] {WorldAuditCommandText.ToTitleCase(job.Operation)} job #{job.Id} is {job.State.ToString().ToLowerInvariant()}."
            };

            return TextCommandResult.Success(message, job.Id);
        });
    }

    public TextCommandResult HandleJobs(TextCommandCallingArgs args)
    {
        return _context.ExecuteSafely(args, "list rollback jobs", () =>
        {
            if (args.Caller?.Player is not IServerPlayer player)
            {
                return TextCommandResult.Error("This command requires a player caller.", "not_player");
            }

            var jobs = _context.Runtime.Repository.GetRollbackJobsAsync().GetAwaiter().GetResult();
            if (jobs.Count == 0)
            {
                return TextCommandResult.Success("[WA] No WorldAudit jobs found.", null);
            }

            player.SendAuditLine(_context.AuditGroup(player), "[WA] Recent WorldAudit jobs", EnumChatType.CommandSuccess);
            foreach (var job in jobs)
            {
                player.SendAuditLine(_context.AuditGroup(player), "", EnumChatType.CommandSuccess);
                player.SendAuditLine(
                    _context.AuditGroup(player),
                    $"[WA] Job #{job.Id} | {WorldAuditCommandText.DescribeOperation(job.Operation)} | {job.State.ToString().ToLowerInvariant()}\n[WA] Blocks: {job.AppliedBlockCount}/{job.PlannedBlockCount}     Containers: {job.AppliedContainerCount}/{job.PlannedContainerCount}     Conflicts: {job.ConflictBlockCount + job.ConflictContainerCount} \n[WA] Requested by: {job.RequestedBy.Name}",
                    EnumChatType.CommandSuccess);
            }

            return TextCommandResult.Success($"[WA] Listed {jobs.Count} job(s).", jobs.Count);
        });
    }

    public TextCommandResult HandleCancel(TextCommandCallingArgs args)
    {
        return _context.ExecuteSafely(args, "cancel rollback job", () =>
        {
            if (!WorldAuditCommandText.TryParseJobId(args, out var jobId))
                return TextCommandResult.Error("[WA] Provide a numeric job id, for example /wa cancel 12.", "bad_job");
            try
            {
                var job = _context.Runtime.RollbackCoordinator.CancelAsync(jobId).GetAwaiter().GetResult();
                return job is null
                    ? TextCommandResult.Error($"[WA] Job #{jobId} was not found.", "job_missing")
                    : TextCommandResult.Success($"[WA] Job #{jobId} cancelled.", jobId);
            }
            catch (InvalidOperationException exception)
            {
                return TextCommandResult.Error($"[WA] {exception.Message}", "bad_cancel");
            }
        });
    }

    public TextCommandResult HandleJob(TextCommandCallingArgs args)
    {
        return _context.ExecuteSafely(args, "show rollback job", () =>
        {
            if (args.Caller?.Player is not IServerPlayer player)
            {
                return TextCommandResult.Error("This command requires a player caller.", "not_player");
            }

            var tokens = WorldAuditCommandText.GetQueryTokens(args).ToArray();
            if (tokens.Length > 1) return new RollbackJobPageCommand(_context).Handle(player, tokens);
            if (!WorldAuditCommandText.TryParseJobId(args, out var jobId))
            {
                return TextCommandResult.Error("[WA] Provide a numeric job id, for example /wa job 12.", "bad_job");
            }

            var job = _context.Runtime.Repository.GetRollbackJobAsync(jobId).GetAwaiter().GetResult();
            if (job is null || job.WorldId != _context.GetWorldId())
            {
                return TextCommandResult.Error($"[WA] Job #{jobId} was not found.", "job_missing");
            }

            player.SendAuditLine(_context.AuditGroup(player), $"[WA] Job #{job.Id} | {WorldAuditCommandText.DescribeOperation(job.Operation)} | {job.State.ToString().ToLowerInvariant()} \n[WA] Requested by: {job.RequestedBy.Name}", EnumChatType.CommandSuccess);
            player.SendAuditLine(
                _context.AuditGroup(player),
                $"[WA] Blocks: {job.AppliedBlockCount}/{job.PlannedBlockCount}\n[WA] Containers: {job.AppliedContainerCount}/{job.PlannedContainerCount}\n[WA] Conflicts: {job.ConflictBlockCount + job.ConflictContainerCount}\n[WA] Failed: {job.FailedBlockCount + job.FailedContainerCount}\n[WA] Chunks: {job.PlannedChunkCount}",
                EnumChatType.CommandSuccess);
            player.SendAuditLine(_context.AuditGroup(player), $"[WA] Filters: {job.FilterSummary}", EnumChatType.CommandSuccess);

            player.SendAuditLine(_context.AuditGroup(player), WorldAuditCommandText.JobSelection(job), EnumChatType.CommandSuccess);
            var conflicts = _context.Runtime.Repository
                .GetRollbackJobEntryDetailsAsync(job.Id, RollbackJobEntryResult.Conflict, 3)
                .GetAwaiter()
                .GetResult();
            foreach (var conflict in conflicts)
            {
                var detail = string.IsNullOrWhiteSpace(conflict.ErrorText) ? "Conflict" : conflict.ErrorText;
                player.SendAuditLine(
                    _context.AuditGroup(player),
                    $"[WA] {conflict.TargetType} conflict at {WorldAuditCommandText.FormatDisplayPosition(_context.Api, conflict.Position)} | {WorldAuditCommandText.FormatRelativeTime(conflict.SourceOccurredAt, DateTimeOffset.UtcNow)} | {detail}",
                    EnumChatType.CommandError);
            }

            var failures = _context.Runtime.Repository
                .GetRollbackJobEntryDetailsAsync(job.Id, RollbackJobEntryResult.Failed, 3)
                .GetAwaiter()
                .GetResult();
            foreach (var failure in failures)
            {
                var detail = string.IsNullOrWhiteSpace(failure.ErrorText) ? "Failed" : failure.ErrorText;
                player.SendAuditLine(
                    _context.AuditGroup(player),
                    $"[WA] {failure.TargetType} failed at {WorldAuditCommandText.FormatDisplayPosition(_context.Api, failure.Position)} | {WorldAuditCommandText.FormatRelativeTime(failure.SourceOccurredAt, DateTimeOffset.UtcNow)} | {detail}",
                    EnumChatType.CommandError);
            }

            if (!string.IsNullOrWhiteSpace(job.LastError))
            {
                player.SendAuditLine(_context.AuditGroup(player), $"[WA] Last error: {job.LastError}", EnumChatType.CommandError);
            }

            return TextCommandResult.Success($"[WA] Job #{job.Id} displayed.", job.Id);
        });
    }

    private TextCommandResult HandlePreview(TextCommandCallingArgs args, RollbackJobOperation operation)
    {
        return _context.ExecuteSafely(args, $"{WorldAuditCommandText.DescribeOperation(operation)} preview", () =>
        {
            if (args.Caller?.Player is not IServerPlayer player)
            {
                return TextCommandResult.Error("This command requires a player caller.", "not_player");
            }

            try
            {
                var tokens = WorldAuditCommandText.GetQueryTokens(args).ToArray();
                var filters = _context.LookupCommandParser.Parse(tokens, DateTimeOffset.UtcNow);

                var center = WorldAuditCommandText.GetPlayerBlockPosition(player);
                var period = filters.TimeRange is { } range
                    ? $"{range.FromInclusive:yyyy-MM-dd HH:mm:ss} to {range.ToInclusive:yyyy-MM-dd HH:mm:ss} UTC"
                    : "all retained history";
                var summary = $"{string.Join(' ', tokens)} | center: {WorldAuditCommandText.FormatDisplayPosition(_context.Api, center)} | radius: {filters.Radius ?? 5} | {period}";
                var worldId = _context.GetWorldId();
                var actor = new AuditActor(player.PlayerName, player.PlayerUID);
                var planner = _context.Runtime.RollbackPlanner;
                return _context.Background.Run(player, "rollback or restore preview", () =>
                    operation == RollbackJobOperation.Rollback
                        ? planner.PreviewRollbackAsync(worldId, center, filters, actor, summary)
                        : planner.PreviewRestoreAsync(worldId, center, filters, actor, summary),
                    job => WorldAuditCommandText.SendPreviewReady(player, job, _context.AuditGroup(player)));
            }
            catch (FormatException exception)
            {
                return TextCommandResult.Error($"[WA] {exception.Message}", "bad_rollback");
            }
        });
    }
}
