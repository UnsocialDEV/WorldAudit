using Vintagestory.API.Common;
using Vintagestory.API.Server;
using WorldAudit.Application.Services;
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
        return _context.ExecuteSafely(args, "lookup", () =>
        {
            if (args.Caller?.Player is not IServerPlayer player)
            {
                return TextCommandResult.Error("This command requires a player caller.", "not_player");
            }

            try
            {
                var filters = _context.LookupCommandParser.Parse(WorldAuditCommandText.GetQueryTokens(args), DateTimeOffset.UtcNow);
                filters = filters with { PageSize = filters.PageSize ?? Math.Clamp(_context.Config.DefaultPageSize, 1, LookupCommandParser.MaximumPageSize) };
                if (useNearDefault && filters.Radius is null)
                {
                    filters = filters with { Radius = _context.Config.DefaultNearRadius };
                }

                var center = WorldAuditCommandText.GetPlayerBlockPosition(player);
                var worldId = _context.GetWorldId();
                var runtime = _context.Runtime;
                var header = $"[WA] Lookup at {WorldAuditCommandText.FormatDisplayPosition(_context.Api, center)}";
                return _context.Background.Run(player, "lookup", async () =>
                {
                    var blocks = filters.IncludeBlocks
                        ? await runtime.BlockQueries.LookupAsync(worldId, center, filters).ConfigureAwait(false)
                        : Array.Empty<BlockAuditEvent>();
                    var containers = filters.IncludeContainers
                        ? await runtime.ContainerQueries.LookupAsync(worldId, center, filters).ConfigureAwait(false)
                        : Array.Empty<ContainerAuditTransaction>();
                    return (blocks, containers);
                }, results => SendHistorySections(player, header, results.blocks, results.containers));
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
        player.SendAuditLine(_context.AuditGroup(player), header, EnumChatType.CommandSuccess);

        var sentSection = false;
        if (blockResults.Count > 0)
        {
            sentSection = true;
            if (containerResults.Count > 0)
            {
                player.SendAuditLine(_context.AuditGroup(player), "[WA] Block history", EnumChatType.CommandSuccess);
            }

            foreach (var line in _context.Formatter.FormatBlockResults(blockResults, DateTimeOffset.UtcNow))
            {
                player.SendAuditLine(_context.AuditGroup(player), line, EnumChatType.CommandSuccess);
            }
        }

        if (containerResults.Count > 0)
        {
            sentSection = true;
            if (blockResults.Count > 0)
            {
                player.SendAuditLine(_context.AuditGroup(player), "[WA] Container history", EnumChatType.CommandSuccess);
            }

            foreach (var line in _context.Formatter.FormatContainerResults(containerResults, DateTimeOffset.UtcNow))
            {
                player.SendAuditLine(_context.AuditGroup(player), line, EnumChatType.CommandSuccess);
            }
        }

        if (!sentSection)
        {
            player.SendAuditLine(_context.AuditGroup(player), "[WA] No matching audit history found.", EnumChatType.CommandSuccess);
        }
    }
}
