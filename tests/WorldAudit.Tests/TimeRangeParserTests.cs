using WorldAudit.Application.Services;

namespace WorldAudit.Tests;

public sealed class TimeRangeParserTests
{
    [Fact]
    public void Parse_SingleDuration_BuildsOpenEndedWindowToNow()
    {
        var now = new DateTimeOffset(2026, 3, 16, 12, 0, 0, TimeSpan.Zero);

        var range = TimeRangeParser.Parse("2h", now);

        Assert.Equal(now.AddHours(-2), range.FromInclusive);
        Assert.Equal(now, range.ToInclusive);
    }

    [Fact]
    public void Parse_BoundedDuration_SortsOlderAndNewerBounds()
    {
        var now = new DateTimeOffset(2026, 3, 16, 12, 0, 0, TimeSpan.Zero);

        var range = TimeRangeParser.Parse("30m-2h", now);

        Assert.Equal(now.AddHours(-2), range.FromInclusive);
        Assert.Equal(now.AddMinutes(-30), range.ToInclusive);
    }

    [Fact]
    public void Parse_InvalidDuration_Throws()
    {
        var now = new DateTimeOffset(2026, 3, 16, 12, 0, 0, TimeSpan.Zero);

        Assert.Throws<FormatException>(() => TimeRangeParser.Parse("nope", now));
    }
}
