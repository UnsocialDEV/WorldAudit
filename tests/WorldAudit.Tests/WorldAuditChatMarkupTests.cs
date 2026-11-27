using Vintagestory.API.Common;
using Vintagestory.API.Server;
using WorldAudit.Application.Services;
using WorldAudit.Mod;
using WorldAudit.Presentation.Chat;
using WorldAudit.Presentation.Commands;
using WorldAudit.Tests.TestSupport;

namespace WorldAudit.Tests;

public sealed class WorldAuditChatMarkupTests
{
    [Fact]
    public void Help_PreservesSyntaxAndFollowingMessages_AsLiteralText()
    {
        var text = AssertChatTextIsLiteral(WorldAuditCommandText.HelpText);
        Assert.Contains("/wa lookup [t:<time>]", text);
        Assert.Contains("/wa consumer <pause|resume>", text);
        Assert.Contains("/wa purge confirm t:<age>", text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CommandErrors_EscapeSyntaxAndUserInput(bool throws)
    {
        var logger = ProxyFactory.Create<ILogger>(_ => { });
        var api = ProxyFactory.Create<ICoreServerAPI>(proxy => proxy.SetValue(nameof(ICoreServerAPI.Logger), logger));
        var context = new WorldAuditCommandContext(api, () => null!, () => new WorldAuditModConfig(),
            new LookupCommandParser(), new PurgeCommandParser(), new InspectorStateService(),
            () => new ChatAuditFormatter(), () => "main", () => (true, ""));
        const string error = "Provide t:<age>; unknown filter <font color=red>.";
        var result = context.ExecuteSafely("test", () => throws ? throw new FormatException(error) : TextCommandResult.Error(error));

        var text = AssertChatTextIsLiteral(result.StatusMessage);
        Assert.Contains(error, text);
        Assert.Equal(EnumCommandStatus.Error, result.Status);
    }

    [Fact]
    public void Escape_DoesNotDoubleEncodeExistingEscapedText()
    {
        var text = WorldAuditCommandText.EscapeChatText("Use <jobId> &lt;already escaped&gt;");
        Assert.Equal(text, WorldAuditCommandText.EscapeChatText(text));
    }

    private static string AssertChatTextIsLiteral(string message)
    {
        var errors = new List<string>();
        var logger = ProxyFactory.Create<ILogger>(proxy => proxy.SetHandler("Error", args =>
        {
            errors.Add((string)args![0]!);
            return null;
        }));
        var tokens = VtmlParser.Tokenize(logger, $"<font color=\"#CCe0cfbb\">{message}</font><font color=\"#E5E9ED\">Following message</font>");
        Assert.Empty(errors);
        Assert.Equal(2, tokens.Length);
        var help = Assert.IsType<VtmlTagToken>(tokens[0]);
        Assert.Equal("font", help.Name);
        var text = string.Concat(help.ChildElements.Select(token => Assert.IsType<VtmlTextToken>(token).Text));
        var following = Assert.IsType<VtmlTagToken>(tokens[1]);
        Assert.Equal("Following message", Assert.IsType<VtmlTextToken>(Assert.Single(following.ChildElements)).Text);
        return text;
    }
}
