namespace WorldAudit.Integration;

public interface IContainerTransactionCapture
{
    void Capture(ContainerTransactionCaptureContext context)
    {
        CaptureAsync(context).GetAwaiter().GetResult();
    }

    ValueTask CaptureAsync(ContainerTransactionCaptureContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Capture(context);
        return ValueTask.CompletedTask;
    }
}
