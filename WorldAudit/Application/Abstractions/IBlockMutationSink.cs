using WorldAudit.Integration;

namespace WorldAudit.Application.Abstractions;

public interface IBlockMutationSink
{
    void Observe(BlockMutationObservation observation)
    {
        ObserveAsync(observation).GetAwaiter().GetResult();
    }

    ValueTask ObserveAsync(BlockMutationObservation observation, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Observe(observation);
        return ValueTask.CompletedTask;
    }
}
