using System.Collections.Concurrent;
using WorldAudit.Domain;

namespace WorldAudit.Application.Services;

public sealed class InspectorStateService
{
    private readonly ConcurrentDictionary<string, byte> _enabledPlayers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, InspectCursor> _inspectCursors = new(StringComparer.OrdinalIgnoreCase);

    public bool Toggle(string playerUid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playerUid);

        if (_enabledPlayers.TryRemove(playerUid, out _))
        {
            _inspectCursors.TryRemove(playerUid, out _);
            return false;
        }

        _enabledPlayers[playerUid] = 1;
        _inspectCursors.TryRemove(playerUid, out _);
        return true;
    }

    public bool IsEnabled(string playerUid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playerUid);
        return _enabledPlayers.ContainsKey(playerUid);
    }

    public void Remove(string playerUid)
    {
        if (string.IsNullOrWhiteSpace(playerUid))
        {
            return;
        }

        _enabledPlayers.TryRemove(playerUid, out _);
        _inspectCursors.TryRemove(playerUid, out _);
    }

    public IReadOnlyCollection<string> GetEnabledPlayerUids()
    {
        return _enabledPlayers.Keys.ToArray();
    }

    public InspectPageState AdvancePage(string playerUid, string worldId, BlockPosition position, int totalEventCount, int pageSize)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playerUid);
        ArgumentException.ThrowIfNullOrWhiteSpace(worldId);

        var normalizedPageSize = Math.Max(1, pageSize);
        var totalPages = Math.Max(1, (int)Math.Ceiling(totalEventCount / (double)normalizedPageSize));
        var location = new InspectLocation(worldId, position);

        InspectPageState result = default;
        _inspectCursors.AddOrUpdate(
            playerUid,
            _ =>
            {
                result = CreatePageState(location, totalEventCount, normalizedPageSize, totalPages, nextPage: totalPages > 1 ? 2 : 1);
                return new InspectCursor(location, result.NextPage);
            },
            (_, existing) =>
            {
                var pageNumber = existing.Location == location
                    ? Math.Min(Math.Max(existing.NextPage, 1), totalPages)
                    : 1;
                var nextPage = totalPages > 1 && pageNumber < totalPages ? pageNumber + 1 : 1;
                result = CreatePageState(location, totalEventCount, normalizedPageSize, totalPages, nextPage, pageNumber);
                return new InspectCursor(location, nextPage);
            });

        return result;
    }

    private static InspectPageState CreatePageState(
        InspectLocation location,
        int totalEventCount,
        int pageSize,
        int totalPages,
        int nextPage,
        int pageNumber = 1)
    {
        return new InspectPageState(
            location.WorldId,
            location.Position,
            pageNumber,
            totalPages,
            totalEventCount,
            pageSize,
            (pageNumber - 1) * pageSize,
            nextPage);
    }

    private sealed record InspectCursor(InspectLocation Location, int NextPage);

    private sealed record InspectLocation(string WorldId, BlockPosition Position);
}

public readonly record struct InspectPageState(
    string WorldId,
    BlockPosition Position,
    int PageNumber,
    int TotalPages,
    int TotalEventCount,
    int PageSize,
    int Offset,
    int NextPage);
