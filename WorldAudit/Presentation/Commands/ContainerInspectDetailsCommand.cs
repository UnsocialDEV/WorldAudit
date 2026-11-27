using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace WorldAudit.Presentation.Commands;

internal sealed class ContainerInspectDetailsCommand(WorldAuditCommandContext context)
{
    internal TextCommandResult Handle(TextCommandCallingArgs args)
    {
        const int pageSize = 3;
        var tokens = WorldAuditCommandText.GetQueryTokens(args).ToArray();
        var page = 1;
        if (args.Caller?.Player is not IServerPlayer player || tokens.Length is < 1 or > 2 ||
            !long.TryParse(tokens[0], out var id) || id <= 0 ||
            (tokens.Length == 2 && (!int.TryParse(tokens[1], out page) || page <= 0)))
            return TextCommandResult.Error("Use /wa i details <id> [page] with positive numbers.");

        var world = context.GetWorldId();
        return context.Background.Run(player, "inspect storage details",
            () => context.Runtime.ContainerInspectDetails.ReadAsync(world, id, page, pageSize), rows =>
            {
                var group = context.AuditGroup(player);
                if (rows.Count == 0)
                {
                    player.SendAuditLine(group, "[WA] No transactions found on this page.", EnumChatType.CommandSuccess);
                    return;
                }
                var location = WorldAuditCommandText.FormatDisplayPosition(context.Api, rows[0].Position);
                player.SendAuditLine(group, $"[WA] Storage details at {location} · Page {page}", EnumChatType.CommandSuccess);
                foreach (var row in rows.Take(pageSize))
                {
                    var lines = WorldAuditCommandText.DescribeContainerLines(row.Lines);
                    player.SendAuditLine(group,
                        $"#{row.Id} · {row.OccurredAt.UtcDateTime:yyyy-MM-dd HH:mm:ss.fff} UTC · {row.Actor.Name} · {row.Cause} · {lines}",
                        EnumChatType.CommandSuccess);
                }
                if (rows.Count > pageSize)
                    player.SendAuditLine(group, $"Next page: /wa i details {id} {page + 1L}", EnumChatType.CommandSuccess);
            });
    }
}
