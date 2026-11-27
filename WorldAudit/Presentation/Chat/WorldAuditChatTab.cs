using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.Server;

namespace WorldAudit.Presentation.Chat;

internal sealed class WorldAuditChatTab(ICoreServerAPI api)
{
    private const string Owner = "worldaudit:moderation";
    private PlayerGroup? _group;

    public int For(IServerPlayer player)
    {
        var groups = api.Groups;
        if (_group is null || !groups.PlayerGroupsById.TryGetValue(_group.Uid, out var registered) || registered != _group)
        {
            _group = groups.PlayerGroupsById.Values.FirstOrDefault(group => group.OwnerUID == Owner);
            if (_group is null)
            {
                var name = "WorldAudit";
                for (var suffix = 2; groups.GetPlayerGroupByName(name) is not null; suffix++)
                    name = $"WorldAudit{suffix}";

                _group = new PlayerGroup { Name = name, OwnerUID = Owner, JoinPolicy = "inviteonly" };
                groups.AddPlayerGroup(_group);
                _group.Md5Identifier = GameMath.Md5Hash(_group.Uid + name);
                if (groups is PlayerDataManager data) data.playerGroupsDirty = true;
            }
        }

        var memberships = player.ServerData.PlayerGroupMemberships;
        if (!memberships.ContainsKey(_group.Uid))
        {
            var packet = new Packet_PlayerGroup
            {
                Name = _group.Name, Uid = _group.Uid, Owneruid = Owner,
                Membership = (int)EnumPlayerGroupMemberShip.Member
            };
            // The native client creates its tab from this packet before it receives its first result.
            api.Network.SendArbitraryPacket(new Packet_Server { Id = Packet_ServerIdEnum.PlayerGroup, PlayerGroup = packet }, player);
            memberships[_group.Uid] = new PlayerGroupMembership
            {
                GroupName = _group.Name, GroupUid = _group.Uid, Level = EnumPlayerGroupMemberShip.Member
            };
            if (groups is PlayerDataManager data) data.playerDataDirty = true;
        }
        return _group.Uid;
    }
}
