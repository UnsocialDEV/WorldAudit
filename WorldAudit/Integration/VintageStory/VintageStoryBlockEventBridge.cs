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

    private readonly ICoreServerAPI _api;
    private readonly Action<IServerPlayer> _inspectIndicatorHandler;
    private readonly System.Func<bool> _blockAuditEnabled;
    private readonly System.Func<string> _worldIdAccessor;
    private readonly System.Func<string, bool> _isInspectEnabled;
    private readonly BlockMutationScopeManager _scopeManager;
    private readonly VintageStoryInteractionAttributionTracker _interactionTracker;
    private readonly VintageStoryBlockEntitySnapshotCodec _snapshotCodec;
    private readonly VintageStoryBlockMutationObserver _mutationObserver;
    private readonly VintageStoryInspectRequestDispatcher _inspectDispatcher;

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
        VintageStoryInteractionAttributionTracker interactionTracker,
        VintageStoryBlockEntitySnapshotCodec snapshotCodec)
    {
        _api = api;
        _scopeManager = scopeManager;
        _inspectIndicatorHandler = inspectIndicatorHandler;
        _blockAuditEnabled = blockAuditEnabled;
        _worldIdAccessor = worldIdAccessor;
        _isInspectEnabled = isInspectEnabled;
        _interactionTracker = interactionTracker;
        _snapshotCodec = snapshotCodec;
        _inspectDispatcher = new VintageStoryInspectRequestDispatcher(
            inspectHandler,
            (operation, exception) => _api?.Logger?.Error("[WorldAudit] {0} failed: {1}", operation, exception));
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
        WorldAuditBlockMutationBehaviorRuntime.Initialize(this);
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

    internal void OnObservedBlockPlaced(Block block, IWorldAccessor world, BlockPos pos)
    {
        _mutationObserver.OnBlockPlaced(block, world, pos);
    }

    internal void OnObservedBlockRemoved(Block block, IWorldAccessor world, BlockPos pos)
    {
        _mutationObserver.OnBlockRemoved(block, world, pos);
    }

    internal bool OnObservedBlockInteractStart(IPlayer byPlayer, BlockSelection blockSel)
    {
        try
        {
            if (byPlayer?.PlayerUID is null || blockSel?.Position is null)
            {
                return false;
            }

            if (ShouldConsumeContainerInspect(byPlayer, blockSel))
            {
                return true;
            }

            if (!_blockAuditEnabled())
            {
                return false;
            }

            var pos = blockSel.Position.Copy();
            var oldSnapshot = _snapshotCodec.Capture(pos);
            RecordInteraction(byPlayer.PlayerName, byPlayer.PlayerUID, pos, oldSnapshot);
            SeedPlayerScope(byPlayer.PlayerName, byPlayer.PlayerUID, pos, oldSnapshot);
            return false;
        }
        catch (Exception exception)
        {
            _api.Logger.Error("[WorldAudit] Block interaction scope seeding failed: {0}", exception);
            return false;
        }
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
                if (!IsContainerTarget(blockSel.Position))
                {
                    _inspectDispatcher.TryDispatch(byPlayer, blockSel.Position.Copy());
                }
            }

            if (_blockAuditEnabled())
            {
                RecordInteraction(byPlayer.PlayerName, byPlayer.PlayerUID, blockSel.Position.Copy(), null);
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

        _inspectDispatcher.RemovePlayer(byPlayer.PlayerUID);
    }

    private void SeedPlayerScope(IServerPlayer byPlayer, BlockPos position, byte[]? oldBlockEntitySnapshot)
    {
        SeedPlayerScope(byPlayer.PlayerName, byPlayer.PlayerUID, position, oldBlockEntitySnapshot);
    }

    private void SeedPlayerScope(string actorName, string? actorExternalId, BlockPos position, byte[]? oldBlockEntitySnapshot)
    {
        var worldPosition = new BlockPosition(position.X, position.Y, position.Z);
        _scopeManager.SeedPositionScope(
            _worldIdAccessor(),
            worldPosition,
            BlockMutationScope.Player(
                actorName,
                actorExternalId,
                oldBlockEntitySnapshot,
                DateTimeOffset.UtcNow.AddSeconds(1)));
    }

    private void RecordInteraction(string actorName, string? actorExternalId, BlockPos position, byte[]? oldBlockEntitySnapshot)
    {
        _interactionTracker.Record(_worldIdAccessor(), position, actorName, actorExternalId, oldBlockEntitySnapshot);
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
        _inspectDispatcher.TryDispatch(player, position);
    }

    private bool ShouldConsumeContainerInspect(IPlayer byPlayer, BlockSelection blockSel)
    {
        if (byPlayer is not IServerPlayer serverPlayer ||
            !_isInspectEnabled(serverPlayer.PlayerUID) ||
            !IsContainerTarget(blockSel.Position))
        {
            return false;
        }

        TriggerInspectIfNeeded(serverPlayer, blockSel.Position.Copy());
        SafeShowInspectIndicator(serverPlayer);
        return true;
    }

    private bool IsContainerTarget(BlockPos position)
    {
        return _api.World.BlockAccessor.GetBlockEntity(position) is IBlockEntityContainer;
    }
}
