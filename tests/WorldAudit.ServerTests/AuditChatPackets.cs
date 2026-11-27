using HarmonyLib;
using Vintagestory.Common;
using Vintagestory.Server;
using Vintagestory.API.Common;

namespace WorldAudit.ServerTests;

internal static class AuditChatPackets
{
    public static Packet_Server[] Read(ConnectedClient player, bool plainText = true)
    {
        var network = (DummyNetwork)AccessTools.Field(typeof(DummyNetConnection), "network").GetValue(player.Socket)!;
        var queue = (Queue<object>)AccessTools.Field(typeof(DummyNetwork), "ClientReceiveBuffer").GetValue(network)!;
        var gate = AccessTools.Field(typeof(DummyNetwork), "ClientReceiveBufferLock").GetValue(network)!;
        lock (gate)
        {
            var packets = new List<Packet_Server>();
            while (queue.TryDequeue(out var entry))
            {
                if (entry is DummyNetworkPacket packet)
                {
                    var decoded = Packet_ServerSerializer.DeserializeBuffer(packet.Data, packet.Length, new Packet_Server());
                    if (plainText && decoded.Chatline?.Message is { } text)
                        decoded.Chatline.Message = string.Concat(VtmlParser.Tokenize(null!, text).Select(ReadText));
                    packets.Add(decoded);
                }
            }
            return packets.ToArray();
        }

        static string ReadText(VtmlToken token) => token is VtmlTextToken text ? text.Text
            : string.Concat(((VtmlTagToken)token).ChildElements.Select(ReadText));
    }
}
