using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using WorldAudit.Application.Abstractions;
using WorldAudit.Domain;
using WorldAudit.Integration;

namespace WorldAudit.Integration.VintageStory;

public sealed class VintageStoryBlockEventBridge
{
    private const string InspectDeniedClaimant = "worldauditinspectsilent";
    private static readonly TimeSpan InspectAttemptCooldown = TimeSpan.FromMilliseconds(250);

    private readonly ICoreServerAPI _api;
    private readonly System.Func<IServerPlayer, BlockPos, Task> _inspectHandler;
    private readonly Action<IServerPlayer> _inspectIndicatorHandler;
    private readonly System.Func<bool> _blockAuditEnabled;
    private readonly System.Func<string> _worldIdAccessor;
    private readonly System.Func<string, bool> _isInspectEnabled;
    private readonly BlockMutationScopeManager _scopeManager;
    private readonly VintageStoryBlockEntitySnapshotCodec _snapshotCodec;
    private readonly VintageStoryBlockMutationObserver _mutationObserver;
    private readonly Dictionary<string, InspectAttempt> _recentInspectAttempts = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _inspectAttemptSync = new();

    public VintageStoryBlockEventBridge(
        ICoreServerAPI api,
        IBlockMutationSink capture,
        BlockMutationScopeManager scopeManager,
        System.Func<IServerPlayer, BlockPos, Task> inspectHandler,
        Action<IServerPlayer> inspectIndicatorHandler,
        System.Func<bool> blockAuditEnabled,
        System.Func<bool> fireCauseProviderEnabled,
        System.Func<string> worldIdAccessor,
        System.Func<string, bool> isInspectEnabled,
        VintageStoryBlockEntitySnapshotCodec snapshotCodec)
    {
        _api = api;
        _scopeManager = scopeManager;
        _inspectHandler = inspectHandler;
        _inspectIndicatorHandler = inspectIndicatorHandler;
        _blockAuditEnabled = blockAuditEnabled;
        _worldIdAccessor = worldIdAccessor;
        _isInspectEnabled = isInspectEnabled;
        _snapshotCodec = snapshotCodec;
        _mutationObserver = new VintageStoryBlockMutationObserver(
            capture,
            scopeManager,
            blockAuditEnabled,
            fireCauseProviderEnabled,
            worldIdAccessor,
            () => api.World,
            snapshotCodec,
            logFailure: (operation, exception) => api.Logger.Error("[WorldAudit] {0} failed: {1}", operation, exception));
    }

    public void Register()
    {
        WorldAuditBlockMutationBehaviorRuntime.Initialize(_mutationObserver);
        _api.Event.CanPlaceOrBreakBlock += OnCanPlaceOrBreakBlock;
        _api.Event.BreakBlock += OnBreakBlock;
        _api.Event.DidUseBlock += OnDidUseBlock;
        _api.Event.PlayerDisconnect += OnPlayerDisconnect;
    }

    public void Unregister()
    {
        WorldAuditBlockMutationBehaviorRuntime.Reset();
        _api.Event.CanPlaceOrBreakBlock -= OnCanPlaceOrBreakBlock;
        _api.Event.BreakBlock -= OnBreakBlock;
        _api.Event.DidUseBlock -= OnDidUseBlock;
        _api.Event.PlayerDisconnect -= OnPlayerDisconnect;
    }

    public void FlushPendingMutations()
    {
        _mutationObserver.FlushPendingRemovals();
    }

    private bool OnCanPlaceOrBreakBlock(IServerPlayer byPlayer, BlockSelection blockSel, out string claimant)
    {
        claimant = string.Empty;
        try
        {
            if (byPlayer?.PlayerUID is null || blockSel?.Position is null)
            {
                return true;
            }

            if (_isInspectEnabled(byPlayer.PlayerUID))
            {
                TriggerInspectIfNeeded(byPlayer, blockSel.Position.Copy());
                claimant = InspectDeniedClaimant;
                SafeShowInspectIndicator(byPlayer);
                return false;
            }

            if (!_blockAuditEnabled())
            {
                return true;
            }

            SeedPlayerScope(byPlayer, blockSel.Position.Copy(), null);
            return true;
        }
        catch (Exception exception)
        {
            _api.Logger.Error("[WorldAudit] Block place/break interception failed: {0}", exception);
            claimant = string.Empty;
            return true;
        }
    }

    private void OnBreakBlock(IServerPlayer byPlayer, BlockSelection blockSel, ref float dropQuantityMultiplier, ref EnumHandling handling)
    {
        try
        {
            if (byPlayer?.PlayerUID is null || blockSel?.Position is null)
            {
                return;
            }

            var pos = blockSel.Position.Copy();
            SeedPlayerScope(byPlayer, pos, _snapshotCodec.Capture(pos));
        }
        catch (Exception exception)
        {
            _api.Logger.Error("[WorldAudit] Break-block scope seeding failed: {0}", exception);
        }
    }

    private void OnDidUseBlock(IServerPlayer byPlayer, BlockSelection blockSel)
    {
        try
        {
            if (byPlayer?.PlayerUID is null || blockSel?.Position is null)
            {
                return;
            }

            if (_isInspectEnabled(byPlayer.PlayerUID))
            {
                _inspectHandler(byPlayer, blockSel.Position.Copy()).GetAwaiter().GetResult();
            }
        }
        catch (Exception exception)
        {
            _api.Logger.Error("[WorldAudit] Inspect block lookup failed: {0}", exception);
        }
    }

    private void OnPlayerDisconnect(IServerPlayer byPlayer)
    {
        if (byPlayer?.PlayerUID is null)
        {
            return;
        }

        lock (_inspectAttemptSync)
        {
            _recentInspectAttempts.Remove(byPlayer.PlayerUID);
        }
    }

    private void SeedPlayerScope(IServerPlayer byPlayer, BlockPos position, byte[]? oldBlockEntitySnapshot)
    {
        var worldPosition = new BlockPosition(position.X, position.Y, position.Z);
        _scopeManager.SeedPositionScope(
            _worldIdAccessor(),
            worldPosition,
            BlockMutationScope.Player(
                byPlayer.PlayerName,
                byPlayer.PlayerUID,
                oldBlockEntitySnapshot,
                DateTimeOffset.UtcNow.AddSeconds(1)));
    }

    private void SafeShowInspectIndicator(IServerPlayer player)
    {
        try
        {
            _inspectIndicatorHandler(player);
        }
        catch (Exception exception)
        {
            _api.Logger.Error("[WorldAudit] Inspect mode indicator failed: {0}", exception);
        }
    }

    private void TriggerInspectIfNeeded(IServerPlayer player, BlockPos position)
    {
        if (!ShouldTriggerInspect(player.PlayerUID, position))
        {
            return;
        }

        _inspectHandler(player, position).GetAwaiter().GetResult();
    }

    private bool ShouldTriggerInspect(string playerUid, BlockPos position)
    {
        var now = DateTimeOffset.UtcNow;
        var nextAttempt = new InspectAttempt(new BlockPosition(position.X, position.Y, position.Z), now);

        lock (_inspectAttemptSync)
        {
            if (_recentInspectAttempts.TryGetValue(playerUid, out var previous)
                && previous.Position == nextAttempt.Position
                && now - previous.OccurredAt < InspectAttemptCooldown)
            {
                return false;
            }

            _recentInspectAttempts[playerUid] = nextAttempt;
            return true;
        }
    }

    private sealed record InspectAttempt(BlockPosition Position, DateTimeOffset OccurredAt);
}
