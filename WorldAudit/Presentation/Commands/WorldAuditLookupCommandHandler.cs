using Vintagestory.API.Common;
using Vintagestory.API.Server;
using WorldAudit.Domain;

namespace WorldAudit.Presentation.Commands;

internal sealed class WorldAuditLookupCommandHandler
{
    private readonly WorldAuditCommandContext _context;

    public WorldAuditLookupCommandHandler(WorldAuditCommandContext context)
    {
        _context = context;
    }

    public TextCommandResult HandleLookup(TextCommandCallingArgs args) => ExecuteLookup(args, useNearDefault: false);

    public TextCommandResult HandleNear(TextCommandCallingArgs args) => ExecuteLookup(args, useNearDefault: true);

    private TextCommandResult ExecuteLookup(TextCommandCallingArgs args, bool useNearDefault)
    {
        return _context.ExecuteSafely("lookup", () =>
        {
            if (args.Caller?.Player is not IServerPlayer player)
            {
                return TextCommandResult.Error("This command requires a player caller.", "not_player");
            }

            try
            {
                var filters = _context.LookupCommandParser.Parse(WorldAuditCommandText.GetQueryTokens(args), DateTimeOffset.UtcNow);
                if (useNearDefault && filters.Radius is null)
                {
                    filters = filters with { Radius = _context.Config.DefaultNearRadius };
                }

                var center = WorldAuditCommandText.GetPlayerBlockPosition(player);
                var worldId = _context.GetWorldId();
                var blockResults = filters.IncludeBlocks
                    ? _context.Runtime.BlockQueries.LookupAsync(worldId, center, filters).GetAwaiter().GetResult()
                    : Array.Empty<BlockAuditEvent>();
                var containerResults = filters.IncludeContainers
                    ? _context.Runtime.ContainerQueries.LookupAsync(worldId, center, filters).GetAwaiter().GetResult()
                    : Array.Empty<ContainerAuditTransaction>();

                SendHistorySections(player, $"[WA] Lookup at {WorldAuditCommandText.FormatDisplayPosition(_context.Api, center)}", blockResults, containerResults);

                var total = blockResults.Count + containerResults.Count;
                return TextCommandResult.Success($"[WA] Found {total} audit record(s).", total);
            }
            catch (FormatException exception)
            {
                return TextCommandResult.Error($"[WA] {exception.Message}", "bad_lookup");
            }
        });
    }

    private void SendHistorySections(
        IServerPlayer player,
        string header,
        IReadOnlyList<BlockAuditEvent> blockResults,
        IReadOnlyList<ContainerAuditTransaction> containerResults)
    {
        player.SendMessage(0, header, EnumChatType.CommandSuccess, null);

        var sentSection = false;
        if (blockResults.Count > 0)
        {
            sentSection = true;
            if (containerResults.Count > 0)
            {
                player.SendMessage(0, "[WA] Block history", EnumChatType.CommandSuccess, null);
            }

            foreach (var line in _context.Formatter.FormatBlockResults(blockResults, DateTimeOffset.UtcNow))
            {
                player.SendMessage(0, line, EnumChatType.CommandSuccess, null);
            }
        }

        if (containerResults.Count > 0)
        {
            sentSection = true;
            if (blockResults.Count > 0)
            {
                player.SendMessage(0, "[WA] Container history", EnumChatType.CommandSuccess, null);
            }

            foreach (var line in _context.Formatter.FormatContainerResults(containerResults, DateTimeOffset.UtcNow))
            {
                player.SendMessage(0, line, EnumChatType.CommandSuccess, null);
            }
        }

        if (!sentSection)
        {
            player.SendMessage(0, "[WA] No matching audit history found.", EnumChatType.CommandSuccess, null);
        }
    }
}
