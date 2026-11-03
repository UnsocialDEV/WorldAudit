using System.Reflection;
using System.Text.Json;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.Common;
using Vintagestory.GameContent;
using Vintagestory.Server;
using WorldAudit.Application.Services;
using WorldAudit.Domain;
using WorldAudit.Integration.VintageStory;
using WorldAudit.Mod;

namespace WorldAudit.ServerTests;

// Deliberately a separate mod: never install this destructive fixture driver on a real world.
public sealed class ServerTests : ModSystem
{
    private ICoreServerAPI api = null!;
    private ServerMain server = null!;
    private WorldAuditRuntime runtime = null!;
    private ConnectedClient alice = null!, bob = null!;
    private readonly List<object> results = [];
    private bool running;
    private string ReportPath => Path.Combine(GamePaths.DataPath, "worldaudit-server-tests.json");
    public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Server;
    public override double ExecuteOrder() => 2;

    public override void StartServerSide(ICoreServerAPI api)
    {
        this.api = api;
        server = (ServerMain)api.World;
        api.ChatCommands.Create("watest").RequiresPrivilege(Privilege.controlserver).HandleWith(_ =>
        {
            if (!File.Exists(Path.Combine(GamePaths.DataPath, "worldaudit-server-tests.enabled")))
                return TextCommandResult.Error("Disposable-world marker is missing.");
            if (running) return TextCommandResult.Error("Tests already started; use a fresh server run.");
            running = true;
            var remaining = 4;
            for (int x = 16; x <= 17; x++)
                for (int z = 16; z <= 17; z++)
                    api.WorldManager.LoadChunkColumnPriority(x, z, new ChunkLoadOptions
                    {
                        KeepLoaded = true,
                        OnLoaded = () => { if (Interlocked.Decrement(ref remaining) == 0) api.Event.EnqueueMainThreadTask(Run, "worldaudit-live-tests"); }
                    });
            return TextCommandResult.Success("Loading isolated fixture chunks; results: " + ReportPath);
        });
    }

    private void Run()
    {
        try
        {
            runtime = (WorldAuditRuntime)AccessTools.Field(typeof(WorldAuditModSystem), "_runtime").GetValue(api.ModLoader.GetModSystem<WorldAuditModSystem>())!;
            alice = CreatePlayer(9001, "WorldAuditAlice");
            bob = CreatePlayer(9002, "WorldAuditBob");
            Check("player attribution + ordinary rollback/undo", OrdinaryBlocks);
            Check("tree felling + rollback/undo", TreeFelling);
            Check("door placement, toggle, break + rollback/undo", Door);
            Check("paired doors", PairedDoors);
            Check("restored rotated door collision boxes", RotatedDoor);
            Check("chisel conversion, voxel edit + rollback/undo", Chisel);
            Check("shared chest attribution + selective rollback/undo", SharedChest);
            Check("rollback preserves newer player changes", RollbackConflict);
        }
        catch (Exception e) { results.Add(new { name = "fixture startup", passed = false, error = e.ToString() }); }
        finally
        {
            File.WriteAllText(ReportPath, JsonSerializer.Serialize(new { game = GameVersion.ShortGameVersion, world = api.WorldManager.SaveGame.SavegameIdentifier, results }, new JsonSerializerOptions { WriteIndented = true }));
            api.Logger.Notification("[WorldAudit.ServerTests] Finished. {0}", ReportPath);
        }
    }

    private ConnectedClient CreatePlayer(int id, string name)
    {
        var network = new DummyNetwork(); network.Start();
        var socket = new DummyNetConnection();
        AccessTools.Field(typeof(DummyNetConnection), "network").SetValue(socket, network);
        var client = new ConnectedClient(id) { Socket = socket, FallBackToTcp = true };
        client.Initialise();
        client.LoadOrCreatePlayerData(server, name, "worldaudit-test-" + id);
        client.WorldData = client.WorldData;
        client.State = EnumClientState.Connected;
        client.WorldData.GameMode = EnumGameMode.Creative;
        client.WorldData.PickingRange = 5;
        client.ServerData.RoleCode = "admin";
        AccessTools.Field(typeof(ServerPlayer), "client").SetValue(client.Player, client);
        server.Clients[id] = client;
        client.Entityplayer.WatchedAttributes.SetString("playerUID", client.Player.PlayerUID);
        client.Entityplayer.Pos.SetPos(515.5, 70, 520.5);
        server.SpawnEntity(client.Entityplayer);
        client.Entityplayer.SetName(name);
        var systems = (System.Collections.IEnumerable)AccessTools.Field(typeof(ServerMain), "Systems").GetValue(server)!;
        var inventorySystem = systems.Cast<object>().Single(s => s.GetType().Name == "ServerSystemInventory");
        AccessTools.Method(inventorySystem.GetType(), "OnPlayerJoin").Invoke(inventorySystem, [client.Player]);
        var antiAbuse = systems.Cast<object>().Single(s => s.GetType().Name == "PlayerAntiAbuseMonitor");
        AccessTools.Method(antiAbuse.GetType(), "OnPlayerJoin").Invoke(antiAbuse, [client.Player]);
        return client;
    }

    private void Check(string name, Action action)
    {
        try { action(); results.Add(new { name, passed = true }); api.Logger.Notification("[WorldAudit.ServerTests] PASS {0}", name); }
        catch (Exception e) { results.Add(new { name, passed = false, error = e.ToString() }); api.Logger.Error("[WorldAudit.ServerTests] FAIL {0}: {1}", name, e); }
    }

    private void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private BlockPos Pos(int x, int z = 516, int y = 70) => new(x, y, z);
    private Block Block(string code) => api.World.GetBlock(new AssetLocation(code)) ?? throw new InvalidOperationException("Missing block " + code);
    private ItemStack Stack(string code, int quantity = 1) => api.World.GetItem(new AssetLocation(code)) is { } item ? new ItemStack(item, quantity) : new ItemStack(Block(code), quantity);
    private void Equip(ConnectedClient client, string? code, int quantity = 1) => client.Player.InventoryManager.ActiveHotbarSlot.Itemstack = code is null ? null : Stack(code, quantity);
    private void Near(ConnectedClient client, BlockPos pos, bool fromEast = false)
    {
        client.Player.Entity.Pos.SetPos(pos.X + (fromEast ? 2.5 : .5), pos.Y, pos.Z + (fromEast ? .5 : 2.5));
    }
    private void Clear(BlockPos pos, int height = 5)
    {
        for (var y = -1; y < height; y++) api.World.BlockAccessor.SetBlock(y == -1 ? Block("game:rock-granite").Id : 0, pos.AddCopy(0, y, 0));
    }
    private void Dispatch(string type, string method, ConnectedClient client, Packet_Client packet)
    {
        var systems = (System.Collections.IEnumerable)AccessTools.Field(typeof(ServerMain), "Systems").GetValue(server)!;
        var system = systems.Cast<object>().Single(s => s.GetType().Name == type);
        AccessTools.Method(system.GetType(), method).Invoke(system, [packet, client]);
    }
    private void PlaceBreak(ConnectedClient client, BlockPos pos, bool place, bool fromEast = false)
    {
        Near(client, pos, fromEast);
        Dispatch("ServerSystemBlockSimulation", "HandleBlockPlaceOrBreak", client, new Packet_Client
        {
            BlockPlaceOrBreak = new Packet_ClientBlockPlaceOrBreak { X = pos.X, Y = pos.InternalY, Z = pos.Z, Mode = place ? 1 : 0, OnBlockFace = BlockFacing.UP.Index,
                HitX = CollectibleNet.SerializeDouble(.5), HitY = CollectibleNet.SerializeDouble(.5), HitZ = CollectibleNet.SerializeDouble(.5) }
        });
    }
    private void Use(ConnectedClient client, BlockPos pos, bool held)
    {
        Near(client, pos);
        Dispatch("ServerSystemInventory", "HandleHandInteraction", client, new Packet_Client
        {
            HandInteraction = new Packet_ClientHandInteraction { X = pos.X, Y = pos.InternalY, Z = pos.Z, EnumHandInteract = held ? 0 : 4,
                UseType = (int)EnumHandInteract.HeldItemInteract, MouseButton = 2, FirstEvent = 1, SlotId = 0,
                InventoryId = client.Player.InventoryManager.GetHotbarInventory().InventoryID, OnBlockFace = BlockFacing.UP.Index,
                HitX = CollectibleNet.SerializeDoublePrecise(.5), HitY = CollectibleNet.SerializeDoublePrecise(.5), HitZ = CollectibleNet.SerializeDoublePrecise(.5) }
        });
    }
    private BlockPosition AuditPos(BlockPos pos) => new(pos.X, pos.InternalY, pos.Z);
    private IReadOnlyList<BlockAuditEvent> History(BlockPos pos)
    {
        runtime.FlushAsync().GetAwaiter().GetResult();
        return runtime.Repository.GetBlockHistoryAsync(new(runtime.Options.WorldId!, AuditPos(pos), Limit: 200)).GetAwaiter().GetResult();
    }
    private BlockAuditEvent LatestActor(BlockPos pos, ConnectedClient client)
    {
        var row = History(pos)[0];
        Require(row.Actor.ExternalId == client.Player.PlayerUID && !row.Actor.IsSynthetic, $"At {pos}: expected {client.Player.PlayerName}, got {row.Actor.Name}/{row.Actor.ExternalId}");
        return row;
    }
    private RollbackJob Rollback(BlockPos pos, BlockAuditAction? action = null, bool containers = false)
    {
        runtime.FlushAsync().GetAwaiter().GetResult();
        var filters = new LookupFilters(null, 5, alice.Player.PlayerName, AuditCause.Player, action, null, IncludeBlocks: !containers, IncludeContainers: containers);
        return Apply(runtime.RollbackPlanner.PreviewRollbackAsync(runtime.Options.WorldId!, AuditPos(pos), filters, new("ServerTests"), "live fixture").GetAwaiter().GetResult());
    }
    private RollbackJob Apply(RollbackJob job, bool conflicts = false)
    {
        Require(job.PlannedBlockCount + job.PlannedContainerCount > 0, "Empty rollback plan");
        runtime.RollbackCoordinator.QueueApplyAsync(job.Id).GetAwaiter().GetResult();
        for (var i = 0; i < 100 && runtime.HasActiveRollbackJobs; i++) runtime.ProcessRollbackTickAsync().GetAwaiter().GetResult();
        var done = runtime.Repository.GetRollbackJobAsync(job.Id).GetAwaiter().GetResult()!;
        Require(done.State == (conflicts ? RollbackJobState.CompletedWithConflicts : RollbackJobState.Completed), "Job failed: " + JsonSerializer.Serialize(done));
        return done;
    }
    private void Undo(RollbackJob job) => Apply(runtime.RollbackPlanner.PreviewUndoAsync(job.Id, new("ServerTests")).GetAwaiter().GetResult());

    private void OrdinaryBlocks()
    {
        var pos = Pos(515); Clear(pos);
        Equip(alice, "game:rock-granite"); PlaceBreak(alice, pos, true);
        Require(api.World.BlockAccessor.GetBlock(pos).Id == Block("game:rock-granite").Id, "Placement did not happen"); LatestActor(pos, alice);
        Equip(bob, null); PlaceBreak(bob, pos, false);
        Require(api.World.BlockAccessor.GetBlock(pos).Id == 0, "Break did not happen"); LatestActor(pos, bob);
        pos = pos.AddCopy(0, 0, 6); Clear(pos);
        Equip(alice, "game:rock-granite"); PlaceBreak(alice, pos, true);
        var job = Rollback(pos); Require(api.World.BlockAccessor.GetBlock(pos).Id == 0, "Rollback did not remove block");
        Undo(job); Require(api.World.BlockAccessor.GetBlock(pos).Id != 0, "Undo did not restore block");
    }
    private void TreeFelling()
    {
        var pos = Pos(526); Clear(pos);
        for (var y = 0; y < 4; y++) api.World.BlockAccessor.SetBlock(Block("game:log-grown-oak-ud").Id, pos.UpCopy(y));
        api.World.BlockAccessor.SetBlock(Block("game:leaves-grown1-oak").Id, pos.UpCopy(4));
        Equip(alice, "game:axe-felling-copper"); PlaceBreak(alice, pos, false);
        for (var y = 0; y < 5; y++) { Require(api.World.BlockAccessor.GetBlock(pos.UpCopy(y)).Id == 0, "Tree did not fell at " + y); LatestActor(pos.UpCopy(y), alice); }
        var job = Rollback(pos, BlockAuditAction.Break);
        for (var y = 0; y < 4; y++) Require(api.World.BlockAccessor.GetBlock(pos.UpCopy(y)).Id == Block("game:log-grown-oak-ud").Id, "Tree rollback missing log " + y);
        Require(api.World.BlockAccessor.GetBlock(pos.UpCopy(4)).Id == Block("game:leaves-grown1-oak").Id, "Tree rollback missing foliage");
        Undo(job);
        for (var y = 0; y < 5; y++) Require(api.World.BlockAccessor.GetBlock(pos.UpCopy(y)).Id == 0, "Tree undo missing piece " + y);
    }
    private bool DoorOpen(BlockPos pos) => api.World.BlockAccessor.GetBlockEntity(pos).GetBehavior<BEBehaviorDoor>().Opened;
    private void Door()
    {
        var pos = Pos(538); Clear(pos);
        Equip(alice, "game:door-solid-oak"); PlaceBreak(alice, pos, true);
        Require(api.World.BlockAccessor.GetBlock(pos.UpCopy()).Id != 0, "Door upper half missing");
        LatestActor(pos, alice); LatestActor(pos.UpCopy(), alice);
        Use(alice, pos, false); Require(DoorOpen(pos), "Door did not open"); LatestActor(pos, alice);
        var toggle = Rollback(pos, BlockAuditAction.Replace); Require(!DoorOpen(pos), "Door rollback did not close"); Undo(toggle); Require(DoorOpen(pos), "Door undo did not reopen");
        Equip(alice, null); PlaceBreak(alice, pos, false);
        Require(api.World.BlockAccessor.GetBlock(pos).Id == 0 && api.World.BlockAccessor.GetBlock(pos.UpCopy()).Id == 0, "Door break left a half"); LatestActor(pos.UpCopy(), alice);
        var unknownBefore = History(pos.UpCopy()).Count(e => e.Cause == AuditCause.Unknown);
        var broken = Rollback(pos, BlockAuditAction.Break);
        Require(api.World.BlockAccessor.GetBlock(pos.UpCopy()).Id != 0 && DoorOpen(pos), "Door rollback did not restore whole open door");
        Undo(broken); Require(api.World.BlockAccessor.GetBlock(pos).Id == 0 && api.World.BlockAccessor.GetBlock(pos.UpCopy()).Id == 0, "Door undo left a half");
        Require(History(pos.UpCopy()).Count(e => e.Cause == AuditCause.Unknown) == unknownBefore, "Door undo leaked an unattributed collateral mutation");
    }
    private void PairedDoors()
    {
        var pos = Pos(550); var other = pos.AddCopy(1, 0, 0); Clear(pos); Clear(other);
        Equip(alice, "game:door-solid-oak"); PlaceBreak(alice, pos, true); PlaceBreak(alice, other, true);
        var before = History(other).Count;
        Use(alice, pos, false);
        Require(DoorOpen(pos) && DoorOpen(other), "Fixture did not form paired doors");
        Require(History(other).Count > before, "Neighbor door toggle was not captured"); LatestActor(other, alice);
        var job = Rollback(pos, BlockAuditAction.Replace); Require(!DoorOpen(pos) && !DoorOpen(other), "Pair rollback left one door open");
        Undo(job); Require(DoorOpen(pos) && DoorOpen(other), "Pair undo left one door closed");
    }
    private void Chisel()
    {
        var pos = Pos(515, 540); Clear(pos); api.World.BlockAccessor.SetBlock(Block("game:rock-granite").Id, pos);
        Equip(alice, "game:chisel-copper"); Use(alice, pos, true);
        Require(api.World.BlockAccessor.GetBlockEntity(pos) is BlockEntityChisel, "Chisel conversion did not happen"); LatestActor(pos, alice);
        var codec = new VintageStoryBlockEntitySnapshotCodec(api.World); var before = codec.Capture(pos)!;
        using var data = new MemoryStream();
        using (var writer = new BinaryWriter(data, System.Text.Encoding.UTF8, true)) { writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(true); writer.Write((ushort)BlockFacing.UP.Index); writer.Write((byte)0); }
        api.World.BlockAccessor.GetBlockEntity(pos).OnReceivedClientPacket(alice.Player, 1010, data.ToArray());
        var edited = codec.Capture(pos)!;
        Require(!before.SequenceEqual(edited), "Chisel voxel was not changed"); LatestActor(pos, alice);
        var job = Rollback(pos); Require(api.World.BlockAccessor.GetBlock(pos).Id == Block("game:rock-granite").Id, "Chisel rollback failed");
        Undo(job); Require(api.World.BlockAccessor.GetBlockEntity(pos) is BlockEntityChisel && codec.AreEquivalent(edited, codec.Capture(pos)), "Chisel undo changed recorded geometry or snow cover");
    }
    private void RotatedDoor()
    {
        var pos = Pos(550, 528); Clear(pos);
        Equip(alice, "game:door-solid-oak"); PlaceBreak(alice, pos, true, fromEast: true);
        var door = api.World.BlockAccessor.GetBlockEntity(pos).GetBehavior<BEBehaviorDoor>();
        Require(door.RotateYRad != 0, "Rotated fixture has default orientation");
        var boxes = door.ColSelBoxes.Select(b => b.ToString()).ToArray();
        Equip(alice, null); PlaceBreak(alice, pos, false);
        var job = Rollback(pos, BlockAuditAction.Break);
        var restored = api.World.BlockAccessor.GetBlockEntity(pos).GetBehavior<BEBehaviorDoor>();
        Require(boxes.SequenceEqual(restored.ColSelBoxes.Select(b => b.ToString())), "Door server collision boxes differ after restore");
        Undo(job);
    }
    private void SharedChest()
    {
        var pos = Pos(530, 540); Clear(pos);
        var chest = api.World.Blocks.First(b => b.Code?.Path == "chest-east");
        api.World.BlockAccessor.SetBlock(chest.Id, pos);
        var inventory = (InventoryBase)((IBlockEntityContainer)api.World.BlockAccessor.GetBlockEntity(pos)).Inventory;
        alice.Player.InventoryManager.OpenInventory(inventory); bob.Player.InventoryManager.OpenInventory(inventory);
        Move(alice, inventory, 0, Stack("game:rock-granite", 4));
        Move(bob, inventory, 1, Stack("game:rock-basalt", 3));
        runtime.FlushAsync().GetAwaiter().GetResult();
        var history = runtime.Repository.GetContainerHistoryAsync(new(runtime.Options.WorldId!, AuditPos(pos), Limit: 100)).GetAwaiter().GetResult();
        Require(history.Count == 2, "Expected exactly two chest transactions; got " + history.Count);
        Require(history[0].Actor.ExternalId == bob.Player.PlayerUID && history[1].Actor.ExternalId == alice.Player.PlayerUID, "Chest actor follows viewer instead of mover");
        var job = Rollback(pos, containers: true);
        Require(inventory[0].Empty && inventory[1].StackSize == 3, "Chest rollback changed Bob's slot or failed to remove Alice's items");
        Undo(job); Require(inventory[0].StackSize == 4 && inventory[1].StackSize == 3, "Chest undo lost contents");
        runtime.FlushAsync().GetAwaiter().GetResult();
        history = runtime.Repository.GetContainerHistoryAsync(new(runtime.Options.WorldId!, AuditPos(pos), Limit: 100)).GetAwaiter().GetResult();
        Require(history.Count(t => t.SourceJobId is null) == 2, "Rollback leaked ordinary chest audit events");
    }
    private void Move(ConnectedClient client, InventoryBase inventory, int slot, ItemStack stack)
    {
        var cursor = client.Player.InventoryManager.MouseItemSlot; cursor.Itemstack = stack;
        var op = new ItemStackMoveOperation(api.World, EnumMouseButton.Left, 0, EnumMergePriority.DirectMerge, stack.StackSize) { ActingPlayer = client.Player };
        var packet = (Packet_Client)inventory.InvNetworkUtil.GetActivateSlotPacket(slot, op);
        ((InventoryNetworkUtil)inventory.InvNetworkUtil).HandleClientPacket(client.Player, 7, packet);
        Require(cursor.Empty, "Chest transfer failed");
    }
    private void RollbackConflict()
    {
        var pos = Pos(550, 540); Clear(pos); Equip(alice, "game:rock-granite"); PlaceBreak(alice, pos, true);
        runtime.FlushAsync().GetAwaiter().GetResult();
        var job = runtime.RollbackPlanner.PreviewRollbackAsync(runtime.Options.WorldId!, AuditPos(pos), new(null, 1, alice.Player.PlayerName, null, null, null), new("ServerTests"), "conflict").GetAwaiter().GetResult();
        Equip(bob, null); PlaceBreak(bob, pos, false); Equip(bob, "game:rock-basalt"); PlaceBreak(bob, pos, true);
        Apply(job, conflicts: true); Require(api.World.BlockAccessor.GetBlock(pos).Id == Block("game:rock-basalt").Id, "Rollback overwrote Bob's new block");
    }
}
