using WorldAudit.Application.Services;

namespace WorldAudit.Tests;

public sealed class PurgeCommandParserTests
{
    private readonly PurgeCommandParser _parser = new();

    [Fact]
    public void Parse_AcceptsPreviewAndConfirmForms()
    {
        var preview = _parser.Parse(["t:30d"]);
        var confirm = _parser.Parse(["confirm", "t:12h"]);

        Assert.False(preview.Confirm);
        Assert.Equal(TimeSpan.FromDays(30), preview.Age);
        Assert.True(confirm.Confirm);
        Assert.Equal(TimeSpan.FromHours(12), confirm.Age);
    }

    [Fact]
    public void Parse_RejectsUnsupportedTokens()
    {
        Assert.Throws<FormatException>(() => _parser.Parse([]));
        Assert.Throws<FormatException>(() => _parser.Parse(["u:Dayton", "t:30d"]));
        Assert.Throws<FormatException>(() => _parser.Parse(["confirm"]));
    }
}
