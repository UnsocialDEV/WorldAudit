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
        return _context.ExecuteSafely("undo preview", () =>
        {
            if (args.Caller?.Player is not IServerPlayer player)
            {
                return TextCommandResult.Error("This command requires a player caller.", "not_player");
            }

            if (!WorldAuditCommandText.TryParseJobId(args, out var sourceJobId))
            {
                return TextCommandResult.Error("[WA] Provide a numeric job id, for example /wa undo 12.", "bad_job");
            }

            try
            {
                var job = _context.Runtime.RollbackPlanner
                    .PreviewUndoAsync(sourceJobId, new AuditActor(player.PlayerName, player.PlayerUID))
                    .GetAwaiter()
                    .GetResult();

                WorldAuditCommandText.SendPreviewReady(player, job);
                return TextCommandResult.Success(
                    $"[WA] {WorldAuditCommandText.ToTitleCase(job.Operation)} preview job #{job.Id} created from job #{sourceJobId}.",
                    job.Id);
            }
            catch (InvalidOperationException exception)
            {
                return TextCommandResult.Error($"[WA] {exception.Message}", "bad_undo");
            }
        });
    }

    public TextCommandResult HandleApply(TextCommandCallingArgs args)
    {
        return _context.ExecuteSafely("apply rollback job", () =>
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
            if (job is null)
            {
                return TextCommandResult.Error($"[WA] Job #{jobId} was not found.", "job_missing");
            }

            player.SendMessage(0, $"[WA] Job #{job.Id} is {job.State.ToString().ToLowerInvariant()}.", EnumChatType.CommandSuccess, null);
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
        return _context.ExecuteSafely("list rollback jobs", () =>
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

            player.SendMessage(0, "[WA] Recent WorldAudit jobs", EnumChatType.CommandSuccess, null);
            foreach (var job in jobs)
            {
                player.SendMessage(
                    0,
                    $"[WA] Job #{job.Id} | {WorldAuditCommandText.DescribeOperation(job.Operation)} | {job.State.ToString().ToLowerInvariant()} | blocks {job.AppliedBlockCount}/{job.PlannedBlockCount} | containers {job.AppliedContainerCount}/{job.PlannedContainerCount} | conflicts {job.ConflictBlockCount + job.ConflictContainerCount} | requested by {job.RequestedBy.Name}",
                    EnumChatType.CommandSuccess,
                    null);
            }

            return TextCommandResult.Success($"[WA] Listed {jobs.Count} job(s).", jobs.Count);
        });
    }

    public TextCommandResult HandleJob(TextCommandCallingArgs args)
    {
        return _context.ExecuteSafely("show rollback job", () =>
        {
            if (args.Caller?.Player is not IServerPlayer player)
            {
                return TextCommandResult.Error("This command requires a player caller.", "not_player");
            }

            if (!WorldAuditCommandText.TryParseJobId(args, out var jobId))
            {
                return TextCommandResult.Error("[WA] Provide a numeric job id, for example /wa job 12.", "bad_job");
            }

            var job = _context.Runtime.Repository.GetRollbackJobAsync(jobId).GetAwaiter().GetResult();
            if (job is null)
            {
                return TextCommandResult.Error($"[WA] Job #{jobId} was not found.", "job_missing");
            }

            player.SendMessage(0, $"[WA] Job #{job.Id} | {WorldAuditCommandText.DescribeOperation(job.Operation)} | {job.State.ToString().ToLowerInvariant()} | requested by {job.RequestedBy.Name}", EnumChatType.CommandSuccess, null);
            player.SendMessage(
                0,
                $"[WA] Blocks: {job.AppliedBlockCount}/{job.PlannedBlockCount} | Containers: {job.AppliedContainerCount}/{job.PlannedContainerCount} | Conflicts: {job.ConflictBlockCount + job.ConflictContainerCount} | Failed: {job.FailedBlockCount + job.FailedContainerCount} | Chunks: {job.PlannedChunkCount}",
                EnumChatType.CommandSuccess,
                null);
            player.SendMessage(0, $"[WA] Filters: {job.FilterSummary}", EnumChatType.CommandSuccess, null);

            var conflicts = _context.Runtime.Repository
                .GetRollbackJobEntryDetailsAsync(job.Id, RollbackJobEntryResult.Conflict, 3)
                .GetAwaiter()
                .GetResult();
            foreach (var conflict in conflicts)
            {
                var detail = string.IsNullOrWhiteSpace(conflict.ErrorText) ? "Conflict" : conflict.ErrorText;
                player.SendMessage(
                    0,
                    $"[WA] {conflict.TargetType} conflict at {WorldAuditCommandText.FormatDisplayPosition(_context.Api, conflict.Position)} | {WorldAuditCommandText.FormatRelativeTime(conflict.SourceOccurredAt, DateTimeOffset.UtcNow)} | {detail}",
                    EnumChatType.CommandError,
                    null);
            }

            var failures = _context.Runtime.Repository
                .GetRollbackJobEntryDetailsAsync(job.Id, RollbackJobEntryResult.Failed, 3)
                .GetAwaiter()
                .GetResult();
            foreach (var failure in failures)
            {
                var detail = string.IsNullOrWhiteSpace(failure.ErrorText) ? "Failed" : failure.ErrorText;
                player.SendMessage(
                    0,
                    $"[WA] {failure.TargetType} failed at {WorldAuditCommandText.FormatDisplayPosition(_context.Api, failure.Position)} | {WorldAuditCommandText.FormatRelativeTime(failure.SourceOccurredAt, DateTimeOffset.UtcNow)} | {detail}",
                    EnumChatType.CommandError,
                    null);
            }

            if (!string.IsNullOrWhiteSpace(job.LastError))
            {
                player.SendMessage(0, $"[WA] Last error: {job.LastError}", EnumChatType.CommandError, null);
            }

            return TextCommandResult.Success($"[WA] Job #{job.Id} displayed.", job.Id);
        });
    }

    private TextCommandResult HandlePreview(TextCommandCallingArgs args, RollbackJobOperation operation)
    {
        return _context.ExecuteSafely($"{WorldAuditCommandText.DescribeOperation(operation)} preview", () =>
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
                var previewTask = operation == RollbackJobOperation.Rollback
                    ? _context.Runtime.RollbackPlanner.PreviewRollbackAsync(
                        _context.GetWorldId(),
                        center,
                        filters,
                        new AuditActor(player.PlayerName, player.PlayerUID),
                        string.Join(' ', tokens))
                    : _context.Runtime.RollbackPlanner.PreviewRestoreAsync(
                        _context.GetWorldId(),
                        center,
                        filters,
                        new AuditActor(player.PlayerName, player.PlayerUID),
                        string.Join(' ', tokens));

                var createdJob = previewTask.GetAwaiter().GetResult();
                WorldAuditCommandText.SendPreviewReady(player, createdJob);
                return TextCommandResult.Success($"[WA] {WorldAuditCommandText.ToTitleCase(operation)} preview job #{createdJob.Id} created.", createdJob.Id);
            }
            catch (FormatException exception)
            {
                return TextCommandResult.Error($"[WA] {exception.Message}", "bad_rollback");
            }
        });
    }
}
