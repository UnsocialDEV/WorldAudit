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
    [Theory]
    [InlineData("[WA] Lookup at 1, 65, 2\n[WA] Player: <font color=red>Player")]
    [InlineData("[WA] Preview rollback job #1 ready.\n[WA] Selection: u:<actor>")]
    [InlineData("[WA] Job #1 | rollback | running\n[WA] Requested by: <Admin>")]
    [InlineData("[WA] Status\n[WA] Consumer: faulted\n[WA] Writer error: <disk>")]
    [InlineData("[WA] Purge preview\n[WA] Total candidates: 10")]
    [InlineData("[WA] Purged 10 record(s).")]
    [InlineData("[WA] Consumer resumed.")]
    [InlineData("[WA] Reload completed.")]
    [InlineData("[WA] Inspect mode enabled.")]
    public void ReturnedAndDirectResultsShareOneSafeRendering(string message)
    {
        var api = ProxyFactory.Create<ICoreServerAPI>(_ => { });
        var context = new WorldAuditCommandContext(api, () => null!, () => new WorldAuditModConfig(),
            new LookupCommandParser(), new PurgeCommandParser(), new InspectorStateService(),
            () => new ChatAuditFormatter(), () => "main", () => (true, ""));
        string? direct = null;
        var player = ProxyFactory.Create<IServerPlayer>(proxy => proxy.SetHandler("SendMessage", args =>
        {
            direct = (string)args![1]!;
            return null;
        }));
        var args = new TextCommandCallingArgs { Caller = new Caller { Player = player } };
        var returned = context.ExecuteSafely(args, "test", () => TextCommandResult.Success(message, 7));
        player.SendAuditLine(23, message, EnumChatType.CommandSuccess);
        Assert.Equal(direct, returned.StatusMessage);
        Assert.Equal(message, AssertChatTextIsLiteral(returned.StatusMessage));
        Assert.Equal(EnumCommandStatus.Success, returned.Status);
        var console = context.ExecuteSafely(new TextCommandCallingArgs { Caller = new Caller { Type = EnumCallerType.Console } },
            "test", () => TextCommandResult.Success(message, 7));
        Assert.Equal(message, console.StatusMessage);
    }

    [Fact]
    public void Help_PreservesSyntaxAndFollowingMessages_AsLiteralText()
    {
        var text = AssertChatTextIsLiteral(AuditChatMarkup.Render(WorldAuditCommandText.HelpText));
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
        var result = context.ExecuteSafely(new TextCommandCallingArgs { Caller = new Caller { Player = ProxyFactory.Create<IServerPlayer>(_ => { }) } }, "test", () => throws ? throw new FormatException(error) : TextCommandResult.Error(error));

        var text = AssertChatTextIsLiteral(result.StatusMessage);
        Assert.Contains(error, text);
        Assert.Equal(EnumCommandStatus.Error, result.Status);
    }

    [Fact]
    public void DirectAuditReply_ShowsLiteralMarkupAndPreservesTheNextLine()
    {
        const string detail = "Selection: u:<actor> | item <font color=red>";
        string? delivered = null;
        var player = ProxyFactory.Create<IServerPlayer>(proxy => proxy.SetHandler("SendMessage", args =>
        {
            Assert.Equal(23, args![0]);
            delivered = (string)args[1]!;
            return null;
        }));
        player.SendAuditLine(23, detail, EnumChatType.CommandSuccess);
        var markup = Assert.IsType<string>(delivered);
        Assert.StartsWith("<font color=\"#FFF0ED\">", markup);
        Assert.Contains("<font color=\"#F07070\"><strong>Selection: </strong></font>", markup);
        Assert.Equal(detail, AssertChatTextIsLiteral(markup));
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
        var text = ChatText.Read(message);
        var following = Assert.IsType<VtmlTagToken>(tokens[1]);
        Assert.Equal("Following message", Assert.IsType<VtmlTextToken>(Assert.Single(following.ChildElements)).Text);
        return text;
    }
}
