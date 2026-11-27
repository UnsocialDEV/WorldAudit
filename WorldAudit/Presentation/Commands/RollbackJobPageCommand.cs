using Vintagestory.API.Common;
using Vintagestory.API.Server;
using WorldAudit.Domain;
using WorldAudit.Presentation.Chat;

namespace WorldAudit.Presentation.Commands;

internal sealed class RollbackJobPageCommand(WorldAuditCommandContext context)
{
    internal TextCommandResult Handle(IServerPlayer player, string[] tokens)
    {
        var section = tokens[1].ToLowerInvariant();
        var details = section == "storage" && tokens.Length >= 3 && tokens[2].Equals("details", StringComparison.OrdinalIgnoreCase);
        var argsBeforePage = details ? 4 : 2;
        long anchor = 0;
        var page = 1;
        if (!long.TryParse(tokens[0], out var id) || id <= 0 || section is not ("blocks" or "storage") ||
            tokens.Length < argsBeforePage || tokens.Length > argsBeforePage + 1 ||
            (details && (!long.TryParse(tokens[3], out anchor) || anchor <= 0)) ||
            (tokens.Length > argsBeforePage && (!int.TryParse(tokens[^1], out page) || page <= 0)))
            return TextCommandResult.Error("Use /wa job <id> blocks|storage [page] or /wa job <id> storage details <id> [page].");

        var runtime = context.Runtime;
        var world = context.GetWorldId();
        var size = runtime.Options.DefaultPageSize;
        var label = details ? "Storage transactions" : section == "storage" ? "Storage visits and individual changes" : "Block edits";
        return context.Background.Run(player, "browse rollback job", async () =>
        {
            var job = await runtime.Repository.GetRollbackJobAsync(id).ConfigureAwait(false);
            var rows = new List<(BlockPosition Position, string Text)>();
            if (job is null || job.WorldId != world) return (found: false, count: 0, rows);
            int count;
            if (details)
            {
                var result = await runtime.JobVisitDetails.ReadAsync(id, world, anchor, page, size).ConfigureAwait(false);
                count = result.Count;
                foreach (var row in result.Rows)
                    rows.Add((row.Position, $"#{row.Id} · {row.Actor.Name} · {row.OccurredAt.UtcDateTime:yyyy-MM-dd HH:mm:ss.fff} UTC · {WorldAuditCommandText.DescribeContainerLines(row.Lines)}"));
            }
            else if (section == "storage")
            {
                count = job.PlannedVisitCount;
                var entries = await runtime.ContainerInspectHistory.ReadAsync(world, null, size, id, page).ConfigureAwait(false);
                foreach (var entry in entries)
                {
                    var row = ContainerInspectSummary.Format(entry, DateTimeOffset.UtcNow);
                    rows.Add((entry.Position, $"{row.Type} · {row.Who} · {row.When} · {row.What}\nDetails  /wa job {id} storage details {entry.DetailId}"));
                }
            }
            else
            {
                count = job.PlannedBlockCount;
                var events = await runtime.JobBlockPage.ReadAsync(id, world, page, size).ConfigureAwait(false);
                foreach (var row in events)
                    rows.Add((row.Position, $"{row.Actor.Name} · {row.OccurredAt.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC · {WorldAuditCommandText.DescribeInspectBlockAction(row)}"));
            }
            return (found: true, count, rows);
        }, result =>
        {
            var group = context.AuditGroup(player);
            void Send(string text) => player.SendAuditLine(group, text, EnumChatType.CommandSuccess);
            if (!result.found)
            {
                player.SendAuditLine(group, $"[WA] Job #{id} was not found.", EnumChatType.CommandError);
                return;
            }
            if (result.count == 0) { Send($"[WA] Job #{id} · {label}: no matching entries."); return; }
            var pages = (result.count - 1) / size + 1;
            if (page > pages) { Send($"[WA] {label}: choose a page from 1 to {pages}."); return; }
            Send($"[WA] Job #{id} · {label} · {result.count} total · Page {page} of {pages}");
            if (page < pages) Send($"Next page: /wa job {id} {section}{(details ? $" details {anchor}" : "")} {page + 1}");
            foreach (var row in result.rows.AsEnumerable().Reverse())
                Send($"{WorldAuditCommandText.FormatDisplayPosition(context.Api, row.Position)} · {row.Text}");
        });
    }
}
