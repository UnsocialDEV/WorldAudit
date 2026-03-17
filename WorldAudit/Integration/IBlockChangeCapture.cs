namespace WorldAudit.Integration;

public interface IBlockChangeCapture
{
    void Capture(BlockChangeCaptureContext context)
    {
        CaptureAsync(context).GetAwaiter().GetResult();
    }

    ValueTask CaptureAsync(BlockChangeCaptureContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Capture(context);
        return ValueTask.CompletedTask;
    }
}
