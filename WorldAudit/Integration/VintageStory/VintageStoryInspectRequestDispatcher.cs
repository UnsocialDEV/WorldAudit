using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using WorldAudit.Domain;

namespace WorldAudit.Integration.VintageStory;

internal sealed class VintageStoryInspectRequestDispatcher
{
    private static readonly TimeSpan InspectAttemptCooldown = TimeSpan.FromMilliseconds(250);

    private readonly Func<IServerPlayer, BlockPos, Task> _inspectHandler;
    private readonly Action<string, Exception>? _logFailure;
    private readonly Dictionary<string, InspectDispatchState> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();

    public VintageStoryInspectRequestDispatcher(
        Func<IServerPlayer, BlockPos, Task> inspectHandler,
        Action<string, Exception>? logFailure = null)
    {
        _inspectHandler = inspectHandler;
        _logFailure = logFailure;
    }

    public bool TryDispatch(IServerPlayer player, BlockPos position)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(position);

        var key = new BlockPosition(position.X, position.Y, position.Z);
        var now = DateTimeOffset.UtcNow;

        lock (_sync)
        {
            if (_states.TryGetValue(player.PlayerUID, out var state))
            {
                if (state.InFlight.Contains(key))
                {
                    return false;
                }

                if (state.LastAttemptPosition == key && now - state.LastAttemptAt < InspectAttemptCooldown)
                {
                    return false;
                }
            }
            else
            {
                state = new InspectDispatchState();
                _states[player.PlayerUID] = state;
            }

            state.LastAttemptPosition = key;
            state.LastAttemptAt = now;
            state.InFlight.Add(key);
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await _inspectHandler(player, position.Copy()).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logFailure?.Invoke("Inspect dispatch", exception);
            }
            finally
            {
                Complete(player.PlayerUID, key);
            }
        });

        return true;
    }

    public void RemovePlayer(string playerUid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playerUid);

        lock (_sync)
        {
            _states.Remove(playerUid);
        }
    }

    private void Complete(string playerUid, BlockPosition position)
    {
        lock (_sync)
        {
            if (!_states.TryGetValue(playerUid, out var state))
            {
                return;
            }

            state.InFlight.Remove(position);
            if (state.InFlight.Count == 0 && state.LastAttemptPosition is null)
            {
                _states.Remove(playerUid);
            }
        }
    }

    private sealed class InspectDispatchState
    {
        public HashSet<BlockPosition> InFlight { get; } = [];

        public BlockPosition? LastAttemptPosition { get; set; }

        public DateTimeOffset LastAttemptAt { get; set; }
    }
}
