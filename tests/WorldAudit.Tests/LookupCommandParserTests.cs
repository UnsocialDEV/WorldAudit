using WorldAudit.Application.Services;
using WorldAudit.Domain;

namespace WorldAudit.Tests;

public sealed class LookupCommandParserTests
{
    [Fact]
    public void Parse_RecognizesTimeRadiusActorAndAction()
    {
        var now = new DateTimeOffset(2026, 3, 16, 12, 0, 0, TimeSpan.Zero);
        var parser = new LookupCommandParser();

        var filters = parser.Parse(["t:45m", "r:6", "u:Dayton", "a:break"], now);

        Assert.Equal(6, filters.Radius);
        Assert.Equal("Dayton", filters.ActorName);
        Assert.Equal(BlockAuditAction.Break, filters.Action);
        Assert.NotNull(filters.TimeRange);
        Assert.Equal(now.AddMinutes(-45), filters.TimeRange!.FromInclusive);
    }

    [Fact]
    public void Parse_SyntheticActorToken_MapsToCause()
    {
        var parser = new LookupCommandParser();

        var filters = parser.Parse(["u:#fire"], DateTimeOffset.UtcNow);

        Assert.Equal(AuditCause.Fire, filters.Cause);
        Assert.Null(filters.ActorName);
    }

    [Fact]
    public void Parse_ExplosionSyntheticActorToken_MapsToCause()
    {
        var parser = new LookupCommandParser();

        var filters = parser.Parse(["u:#explosion"], DateTimeOffset.UtcNow);

        Assert.Equal(AuditCause.Explosion, filters.Cause);
        Assert.Null(filters.ActorName);
    }

    [Fact]
    public void Parse_TargetFlags_EnableContainerOnlyLookup()
    {
        var parser = new LookupCommandParser();

        var filters = parser.Parse(["-c", "t:30m"], DateTimeOffset.UtcNow);

        Assert.False(filters.IncludeBlocks);
        Assert.True(filters.IncludeContainers);
    }

    [Fact]
    public void Parse_TargetFlags_CombineBlockAndContainerWhenBothPresent()
    {
        var parser = new LookupCommandParser();

        var filters = parser.Parse(["-b", "-c"], DateTimeOffset.UtcNow);

        Assert.True(filters.IncludeBlocks);
        Assert.True(filters.IncludeContainers);
    }

    [Fact]
    public void Parse_RecognizesIncludeAndExcludeCodeFilters()
    {
        var parser = new LookupCommandParser();

        var filters = parser.Parse(["i:game:stone,game:granite", "e:game:air"], DateTimeOffset.UtcNow);

        Assert.Equal(["game:stone", "game:granite"], filters.IncludeCodes);
        Assert.Equal(["game:air"], filters.ExcludeCodes);
    }

    [Fact]
    public void Parse_RejectsEmptyIncludeTokens()
    {
        var parser = new LookupCommandParser();

        Assert.Throws<FormatException>(() => parser.Parse(["i:game:stone,,game:air"], DateTimeOffset.UtcNow));
    }
}
