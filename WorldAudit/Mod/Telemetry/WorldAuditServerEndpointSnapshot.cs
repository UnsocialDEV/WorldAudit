namespace WorldAudit.Mod.Telemetry;

internal sealed record WorldAuditServerEndpointSnapshot(string IpAddress, int Port)
{
    public string DisplayValue => $"{IpAddress}:{Port}";
}
