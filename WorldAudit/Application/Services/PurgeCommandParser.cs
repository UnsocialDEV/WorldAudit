namespace WorldAudit.Application.Services;

public sealed class PurgeCommandParser
{
    public PurgeCommandRequest Parse(IReadOnlyList<string> tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);

        if (tokens.Count == 0)
        {
            throw new FormatException("Provide t:<age>, for example /wa purge t:30d.");
        }

        var confirm = false;
        var ageTokenIndex = 0;
        if (string.Equals(tokens[0], "confirm", StringComparison.OrdinalIgnoreCase))
        {
            confirm = true;
            ageTokenIndex = 1;
        }

        if (tokens.Count != ageTokenIndex + 1)
        {
            throw new FormatException("Phase 6 purge supports only t:<age>.");
        }

        var ageToken = tokens[ageTokenIndex];
        if (!ageToken.StartsWith("t:", StringComparison.OrdinalIgnoreCase))
        {
            throw new FormatException("Phase 6 purge supports only t:<age>.");
        }

        var age = TimeRangeParser.ParseDurationToken(ageToken[2..]);
        if (age <= TimeSpan.Zero)
        {
            throw new FormatException("Purge age must be greater than zero.");
        }

        return new PurgeCommandRequest(confirm, age);
    }
}

public sealed record PurgeCommandRequest(bool Confirm, TimeSpan Age);
