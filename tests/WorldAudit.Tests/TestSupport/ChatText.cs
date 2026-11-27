using Vintagestory.API.Common;

namespace WorldAudit.Tests.TestSupport;

internal static class ChatText
{
    public static string Read(string markup)
    {
        var logger = ProxyFactory.Create<ILogger>(_ => { });
        return string.Concat(VtmlParser.Tokenize(logger, markup).Select(ReadToken));

        static string ReadToken(VtmlToken token) => token is VtmlTextToken text ? text.Text
            : string.Concat(((VtmlTagToken)token).ChildElements.Select(ReadToken));
    }
}
