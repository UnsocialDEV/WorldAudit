using Vintagestory.API.Common;
using Vintagestory.API.Server;
using WorldAudit.Presentation.Chat;

namespace WorldAudit.Presentation.Commands;

internal static class AuditChatDelivery
{
    public static void SendAuditLine(this IServerPlayer player, int groupId, string message, EnumChatType type)
    {
        player.SendMessage(groupId, AuditChatMarkup.Render(message, type == EnumChatType.CommandError), type, null);
    }
}
