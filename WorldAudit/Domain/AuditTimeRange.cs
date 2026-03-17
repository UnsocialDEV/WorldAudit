namespace WorldAudit.Domain;

public sealed record AuditTimeRange(DateTimeOffset? FromInclusive, DateTimeOffset? ToInclusive)
{
    public static AuditTimeRange OpenEnded { get; } = new(null, null);

    public bool Contains(DateTimeOffset value)
    {
        if (FromInclusive is { } from && value < from)
        {
            return false;
        }

        if (ToInclusive is { } to && value > to)
        {
            return false;
        }

        return true;
    }
}
