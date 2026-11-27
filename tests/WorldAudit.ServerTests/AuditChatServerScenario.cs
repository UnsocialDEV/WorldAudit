using System.Diagnostics;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.Server;
using WorldAudit.Mod;
using WorldAudit.Presentation.Commands;

namespace WorldAudit.ServerTests;

public sealed partial class ServerTests
{
    private async Task VerifyAuditChat()
    {
        const string name = "private inspect, lookup and job packets during busy General chat";
        try
        {
            foreach (var command in new[] { "/wa", "/wa help", "/wa consumer resume", "/wa lookup invalid-filter" })
            {
                TextCommandResult? response = null;
                api.ChatCommands.ExecuteUnparsed(command, new TextCommandCallingArgs
                {
                    Caller = new Caller { Type = EnumCallerType.Player, Player = alice.Player, CallerPrivileges = ["*"] },
                    LanguageCode = "en"
                }, result => response = result);
                Require(response?.StatusMessage?.StartsWith("<font color=\"#FFF0ED\">") == true,
                    "Returned command output is missing shared VTML formatting: " + command);
                Require(response!.Status == (command.EndsWith("invalid-filter") ? EnumCommandStatus.Error : EnumCommandStatus.Success),
                    "Formatting changed a command's status.");
            }
            var latest = runtime.Repository.GetRollbackJobsAsync(1).GetAwaiter().GetResult().Single();
            var commands = (WorldAuditChatCommands)AccessTools.Field(typeof(WorldAuditModSystem), "_chatCommands")
                .GetValue(api.ModLoader.GetModSystem<WorldAuditModSystem>())!;
            AuditChatPackets.Read(alice, plainText: false);
            AuditChatPackets.Read(bob, plainText: false);
            // Both players are group members so a mistaken group broadcast would reach Bob.
            RunCommand(bob, $"/wa job {latest.Id}");
            var bobGroup = AuditChatPackets.Read(bob, plainText: false).Single(p => p.Id == Packet_ServerIdEnum.PlayerGroup).PlayerGroup;
            Require(bobGroup.Name == "WorldAudit", "Bob did not receive the audit tab declaration.");
            RunCommand(alice, $"/wa job {latest.Id}");
            var inspect = commands.SendInspectHistoryAsync(alice.Player, Pos(520));
            RunCommand(alice, "/wa lookup p:1");
            for (var i = 0; i < 32; i++)
            {
                foreach (var client in new[] { alice, bob })
                    client.Player.SendMessage(GlobalConstants.GeneralChatGroup, $"General test line {i}", EnumChatType.OthersMessage);
            }

            await inspect.WaitAsync(TimeSpan.FromSeconds(8));
            var received = new List<Packet_Server>();
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(8))
            {
                received.AddRange(AuditChatPackets.Read(alice, plainText: false));
                if (received.Any(p => p.Chatline?.Message?.Contains("[WA] Lookup at") == true)) break;
                await Task.Delay(40);
            }

            var groupIndex = received.FindIndex(p => p.Id == Packet_ServerIdEnum.PlayerGroup);
            Require(groupIndex >= 0, "Alice never received the dedicated tab declaration.");
            var group = received[groupIndex].PlayerGroup;
            Require(group.Name == "WorldAudit" && group.Uid == bobGroup.Uid && group.Uid != GlobalConstants.GeneralChatGroup, "Unexpected audit tab name or ID.");
            var lines = received.Where(p => p.Chatline != null).Select(p => p.Chatline).ToArray();
            var audit = lines.Where(p => p.Groupid == group.Uid).ToArray();
            var inspectIndex = Array.FindIndex(audit, line => line.Message.Contains("[WA] Inspect:"));
            Require(inspectIndex >= 30 && audit.Skip(inspectIndex - 30).Take(30).All(line => line.Message == ""),
                "Inspect page was not preceded by a private tab reset.");
            var visible = new Vintagestory.API.Datastructures.LimitedList<string>(30);
            visible.Add("Previous inspected location");
            foreach (var line in audit.Skip(inspectIndex - 30)) visible.Add(line.Message);
            Require(!visible.Contains("Previous inspected location"), "Previous inspection remains in native chat history.");
            Require(audit.Where(line => line.Message.Contains("[WA]")).All(line => line.Message.StartsWith("<font color=\"")),
                "Audit results are missing VTML color formatting.");
            Require(received.FindIndex(p => p.Chatline?.Groupid == group.Uid) > groupIndex, "Audit output preceded the tab declaration.");
            foreach (var text in new[] { $"[WA] Job #{latest.Id}", "[WA] Filters:", "[WA] Inspect:", "[WA] Lookup at" })
                Require(audit.Any(line => line.Message.Contains(text)), "Missing audit output: " + text);
            Require(lines.Where(line => line.Message.Contains("[WA]")).All(line => line.Groupid == group.Uid), "Audit output appeared in General.");
            Require(lines.Count(line => line.Message.StartsWith("General test line") && line.Groupid == GlobalConstants.GeneralChatGroup) == 32, "General chat lines were lost or misrouted.");
            Require(audit.All(line => !line.Message.Contains("General test line")), "General chat entered the audit tab.");
            var bobMessages = AuditChatPackets.Read(bob, plainText: false).Where(p => p.Chatline != null).Select(p => p.Chatline).ToArray();
            Require(bobMessages.Count(line => line.Message.StartsWith("General test line")) == 32, "Observer did not receive General chat.");
            Require(bobMessages.All(line => line.Groupid != group.Uid), "A second moderator received Alice's private results.");
            // Follow the command printed under Alice's two interleaved chest transfers.
            string? detailsCommand = null;
            for (var page = 0; page < 4 && detailsCommand is null; page++)
            {
                await commands.SendInspectHistoryAsync(alice.Player, Pos(530, 540));
                var pageLines = AuditChatPackets.Read(alice).Where(p => p.Chatline?.Groupid == group.Uid)
                    .Select(p => p.Chatline.Message).ToArray();
                var movement = Array.FindIndex(pageLines, line => line.Contains("Added 6 Rock Granite"));
                if (movement < 0) continue;
                var hint = pageLines.Skip(movement + 1).First(line => line.Contains("/wa i details "));
                detailsCommand = hint[hint.IndexOf("/wa i details ", StringComparison.Ordinal)..];
            }
            Require(detailsCommand != null, "Missing grouped visit or details hint for the shared chest.");
            await OnServer(() => RunCommand(alice, detailsCommand!));
            var details = new List<string>();
            timer.Restart();
            while (timer.Elapsed < TimeSpan.FromSeconds(8))
            {
                details.AddRange(AuditChatPackets.Read(alice).Where(p => p.Chatline?.Groupid == group.Uid)
                    .Select(p => p.Chatline.Message));
                if (details.Any(line => line.Contains("slot 2"))) break;
                await Task.Delay(40);
            }
            Require(details.Any(line => line.Contains("+4 Rock Granite (slot 0)")) &&
                details.Any(line => line.Contains("+2 Rock Granite (slot 2)")) &&
                details.All(line => !line.Contains("Basalt")), "Visit details did not show Alice's original slot changes.");
            results.Add(new { name, passed = true, auditLines = audit.Length, generalLines = 32 });
        }
        catch (Exception error) { results.Add(new { name, passed = false, error = error.ToString() }); }

        void RunCommand(ConnectedClient client, string command)
        {
            api.ChatCommands.ExecuteUnparsed(command, new TextCommandCallingArgs
            {
                Caller = new Caller { Type = EnumCallerType.Player, Player = client.Player, CallerPrivileges = ["*"] },
                LanguageCode = "en"
            }, result => Require(result.Status != EnumCommandStatus.Error, command + ": " + result.StatusMessage));
        }
    }
}
