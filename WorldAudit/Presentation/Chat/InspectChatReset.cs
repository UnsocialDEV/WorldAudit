using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace WorldAudit.Presentation.Chat;

internal static class InspectChatReset
{
    public static void Clear(IServerPlayer player, int groupId)
    {
        // Vintage Story 1.22.7 retains 30 entries per tab and exposes no server-side tab-clear packet.
        for (var i = 0; i < 30; i++)
            player.SendMessage(groupId, "", EnumChatType.CommandSuccess, null);
    }
}
