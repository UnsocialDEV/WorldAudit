namespace WorldAudit.Domain;

public sealed record AuditActor
{
    public AuditActor(string? name, string? externalId = null, bool isSynthetic = false)
    {
        Name = NormalizeName(name);
        ExternalId = externalId;
        IsSynthetic = isSynthetic;
    }

    public string Name { get; init; }

    public string? ExternalId { get; init; }

    public bool IsSynthetic { get; init; }

    public static string NormalizeName(string? name, AuditCause cause = AuditCause.Unknown)
    {
        return string.IsNullOrWhiteSpace(name)
            ? $"#{cause.ToString().ToLowerInvariant()}"
            : name;
    }
}
