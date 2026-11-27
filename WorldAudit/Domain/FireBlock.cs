namespace WorldAudit.Domain;

internal static class FireBlock
{
    internal const string Code = "game:fire";

    internal static bool Matches(string? code) => string.Equals(code, Code, StringComparison.OrdinalIgnoreCase);
}
