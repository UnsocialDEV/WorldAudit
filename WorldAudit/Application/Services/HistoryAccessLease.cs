namespace WorldAudit.Application.Services;

internal readonly struct HistoryAccessLease(SemaphoreSlim gate) : IDisposable
{
    public void Dispose() => gate.Release();
}
