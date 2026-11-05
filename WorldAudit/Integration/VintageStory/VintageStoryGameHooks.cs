using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using WorldAudit.Domain;

namespace WorldAudit.Integration.VintageStory;

internal sealed class VintageStoryGameHooks(ICoreServerAPI api, VintageStoryMutationMonitor monitor) : IDisposable
{
    private readonly ICoreServerAPI _api = api;
    private readonly VintageStoryMutationMonitor _monitor = monitor;
    private const string PatchId = "worldaudit.1.22.7";
    private readonly Harmony _harmony = new(PatchId);
    private static VintageStoryGameHooks? _current;
    private readonly ConditionalWeakTable<IBulkBlockAccessor, Dictionary<(BlockPos Position, int Layer), BlockMutationScope>> _queuedActors = new();
    internal static bool Active => _current is not null;
    private static readonly BindingFlags DeclaredMethods = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    internal void Register()
    {
        if (_current is not null) throw new InvalidOperationException("WorldAudit game hooks are already registered.");
        try
        {
            Patch("Vintagestory.Common.BlockAccessorBase", "SetBlockInternal");
            Patch("Vintagestory.Common.BlockAccessorBase", "BreakBlock");
            Patch("Vintagestory.Server.ServerSystemBlockSimulation", "HandleBlockPlaceOrBreak", "HandleBlockInteract", "HandleBlockEntityPacket");
            Patch("Vintagestory.Common.InventoryNetworkUtil", "HandleClientPacket");
            Patch("Vintagestory.API.Common.InventoryBase", "ActivateSlot", "TryMoveItemStack");
            Patch("Vintagestory.Server.ServerSystemInventory", "HandleHandInteraction", "callOnUsing");
            Patch("Vintagestory.GameContent.BEBehaviorDoor", "ToggleDoorWing");
            _harmony.Patch(AccessTools.Method(AccessTools.TypeByName("Vintagestory.GameContent.BlockBehaviorDoor"), "getDoorAt"),
                postfix: new HarmonyMethod(typeof(VintageStoryGameHooks), nameof(DoorResolved)));
            Patch("Vintagestory.Server.ServerMain", "CreateExplosion");
            Patch("Vintagestory.GameContent.BlockEntityBomb", "Combust");
            Patch("Vintagestory.GameContent.BEBehaviorBurning", "OnTick", "OnSlowServerTick", "KillFire", "SpreadTo");
            Patch("Vintagestory.GameContent.EntityBlockFalling", "Initialize", "UpdateBlock", "UpdateSnowLayer");
            Patch("Vintagestory.GameContent.BlockBehaviorFiniteSpreadingLiquid", "OnDelayedWaterUpdateCheck");
            Patch("Vintagestory.GameContent.LeafBlockDecay", "doDecay");
            Patch("Vintagestory.GameContent.BlockEntitySapling", "CheckGrow");
            Patch("Vintagestory.GameContent.BlockEntityFarmland", "TryGrowCrop", "UpdateFarmlandBlock");
            Patch("Vintagestory.Server.BlockAccessorWorldGen", "SetBlock", "SetFluidBlock");
            Patch("Vintagestory.Common.BlockAccessorRelaxedBulkUpdate", "Commit");
            Patch("Vintagestory.Common.BlockAccessorBulkMinimalUpdate", "FastCommit");
            var bulkType = AccessTools.TypeByName("Vintagestory.Common.BlockAccessorRelaxedBulkUpdate");
            _harmony.Patch(AccessTools.DeclaredMethod(bulkType, "Commit"), transpiler: new HarmonyMethod(typeof(VintageStoryGameHooks), nameof(CaptureBeforeBulkWrites)));
            foreach (var method in bulkType.GetMethods(DeclaredMethods).Where(m => m.Name is "SetBlock" or "ExchangeBlock" or "SetFluidBlock"))
                _harmony.Patch(method, postfix: new HarmonyMethod(typeof(VintageStoryGameHooks), nameof(QueuedBlock)));
            _harmony.Patch(AccessTools.DeclaredMethod(bulkType, "Rollback"), postfix: new HarmonyMethod(typeof(VintageStoryGameHooks), nameof(DiscardQueued)));
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name is "VintagestoryAPI" or "VintagestoryLib" or "VSSurvivalMod" or "VSEssentials" or "VSCreativeMod"))
            {
                foreach (var type in GetLoadableTypes(assembly, _api.Logger).Where(type =>
                    typeof(IBlockAccessor).IsAssignableFrom(type) || typeof(Block).IsAssignableFrom(type) || typeof(BlockEntity).IsAssignableFrom(type)))
                {
                    foreach (var method in type.GetMethods(DeclaredMethods).Where(m => !m.IsAbstract && !m.ContainsGenericParameters))
                    {
                        if (typeof(IBlockAccessor).IsAssignableFrom(type) && !typeof(IBulkBlockAccessor).IsAssignableFrom(type) && method.Name == "ExchangeBlock"
                            || typeof(Block).IsAssignableFrom(type) && method.Name == "OnBlockBroken"
                            || typeof(BlockEntity).IsAssignableFrom(type) && method.Name == "OnReceivedClientPacket")
                            PatchOperation(method);
                        if (typeof(BlockEntity).IsAssignableFrom(type) && method.Name is "Initialize" or "FromTreeAttributes")
                            _harmony.Patch(method, postfix: new HarmonyMethod(typeof(VintageStoryGameHooks), nameof(ContainerAvailable)));
                        if (typeof(BlockEntity).IsAssignableFrom(type) && method.Name is "OnBlockUnloaded" or "OnBlockRemoved")
                            _harmony.Patch(method, prefix: new HarmonyMethod(typeof(VintageStoryGameHooks), nameof(ContainerRemoved)));
                    }
                }
            }
            _current = this;
        }
        catch
        {
            _harmony.UnpatchAll(PatchId);
            throw;
        }
    }

    internal static Type[] GetLoadableTypes(Assembly assembly, ILogger logger)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            var types = exception.Types.OfType<Type>().ToArray();
            var failures = string.Join(" | ", exception.LoaderExceptions.OfType<Exception>().Select(error => error.Message).Distinct());
            logger.Warning($"[WorldAudit] Assembly {assembly.GetName().Name}: scanning {types.Length} loadable types; {exception.Types.Length - types.Length} types unavailable. Loader errors: {failures}");
            return types;
        }
    }

    private void Patch(string typeName, params string[] names)
    {
        var type = AccessTools.TypeByName(typeName) ?? throw new MissingMemberException(typeName);
        foreach (var name in names)
        {
            var methods = type.GetMethods(DeclaredMethods).Where(m => m.Name == name).ToArray();
            if (methods.Length == 0) throw new MissingMethodException(typeName, name);
            foreach (var method in methods) PatchOperation(method);
        }
    }

    private void PatchOperation(MethodInfo method)
        => _harmony.Patch(method,
            prefix: new HarmonyMethod(typeof(VintageStoryGameHooks), nameof(BeforeOperation)),
            finalizer: new HarmonyMethod(typeof(VintageStoryGameHooks), nameof(AfterOperation)));

    private static void BeforeOperation(object __instance, object[] __args, MethodBase __originalMethod, out IDisposable? __state)
    {
        __state = null;
        var current = _current;
        if (current is null) return;
        try
        {
            if (__args.OfType<IWorldAccessor>().Any(w => !ReferenceEquals(w, current._api.World))
                || __args.OfType<ICoreAPI>().Any(a => a.Side != EnumAppSide.Server)
                || __instance is BlockEntity be && be.Api?.Side != EnumAppSide.Server
                || __instance is BlockEntityBehavior behaviorInstance && behaviorInstance.Blockentity.Api?.Side != EnumAppSide.Server
                || __instance is Entity entityInstance && entityInstance.World is not null && entityInstance.World.Side != EnumAppSide.Server
                || __instance is InventoryBase inv && inv.Api?.Side != EnumAppSide.Server
                || __instance is IBlockAccessor && Member(__instance, "worldAccessor") is IWorldAccessor accessorWorld && !ReferenceEquals(accessorWorld, current._api.World)
                || __instance.GetType().FullName == "Vintagestory.Common.InventoryNetworkUtil" && Member(__instance, "Api") is ICoreAPI inventoryApi && inventoryApi.Side != EnumAppSide.Server)
                return;
            var player = __args.OfType<IPlayer>().FirstOrDefault();
            player ??= __args.OfType<ItemStackMoveOperation>().FirstOrDefault()?.ActingPlayer;
            foreach (var argument in __args)
            {
                if (player is not null) break;
                if (argument is EntityPlayer entity) player = current._api.World.PlayerByUid(entity.PlayerUID);
                else if (argument?.GetType().FullName == "Vintagestory.Server.ConnectedClient")
                    player = Member(argument, "Player") as IPlayer;
            }
            var cause = __originalMethod.DeclaringType?.Name switch
            {
                "BlockEntityBomb" => AuditCause.Explosion,
                "BEBehaviorBurning" => AuditCause.Fire,
                "EntityBlockFalling" => AuditCause.Gravity,
                "BlockBehaviorFiniteSpreadingLiquid" => AuditCause.Liquid,
                "LeafBlockDecay" => AuditCause.Decay,
                "BlockAccessorWorldGen" => AuditCause.Worldgen,
                "BlockEntitySapling" or "BlockEntityFarmland" when current._monitor.CurrentScope is null => AuditCause.System,
                "ServerMain" when __originalMethod.Name == "CreateExplosion" => AuditCause.Explosion,
                _ => (AuditCause?)null
            };
            var uid = cause == AuditCause.Fire ? Member(__instance, "startedByPlayerUid") as string
                : cause == AuditCause.Explosion ? Member(__instance, "ignitedByPlayerUid") as string : null;
            if (__originalMethod.Name == "CreateExplosion") uid = __args.LastOrDefault() as string;
            BlockMutationScope? scope = player is null ? null : BlockMutationScope.Player(player.PlayerName, player.PlayerUID);
            if (cause is { } explicitCause)
                scope = string.IsNullOrEmpty(uid) ? BlockMutationScope.Synthetic(explicitCause)
                    : new BlockMutationScope(current._api.World.PlayerByUid(uid)?.PlayerName ?? uid, uid, explicitCause);
            __state = current._monitor.Begin(scope);
            if (__instance is IBulkBlockAccessor bulk && __originalMethod.Name is "Commit" or "FastCommit") CaptureStaged(bulk);
            var positions = __args.OfType<BlockPos>().Concat(__args.OfType<BlockSelection>().Select(s => s.Position)).ToList();
            if (__instance is BlockEntity blockEntity) positions.Add(blockEntity.Pos);
            if (__instance is BlockEntityBehavior behavior) positions.Add(behavior.Blockentity.Pos);
            if (__instance.GetType().FullName == "Vintagestory.Common.InventoryNetworkUtil"
                && AccessTools.Field(__instance.GetType(), "inv")?.GetValue(__instance) is InventoryBase inventory)
                VintageStoryContainerEventBridge.Current?.TrackInventory(inventory);
            if (__instance is InventoryBase actedInventory) VintageStoryContainerEventBridge.Current?.TrackInventory(actedInventory);
            foreach (var packet in __args.Where(a => a?.GetType().Name == "Packet_Client"))
            {
                var member = __originalMethod.Name switch
                {
                    "HandleBlockEntityPacket" => "BlockEntityPacket",
                    "HandleBlockPlaceOrBreak" => "BlockPlaceOrBreak",
                    "HandleBlockInteract" or "HandleHandInteraction" => "HandInteraction",
                    _ => null
                };
                var payload = member is null ? null : Member(packet, member);
                if (payload is not null && Member(payload, "X") is int x && Member(payload, "Y") is int y && Member(payload, "Z") is int z)
                    positions.Add(new BlockPos(x, y, z));
            }
            foreach (var pos in positions.Where(p => p is not null).Distinct())
            {
                current._monitor.Touch(pos, accessor: cause == AuditCause.Worldgen ? __instance as IBlockAccessor : null);
                if (cause != AuditCause.Worldgen) VintageStoryContainerEventBridge.Current?.TrackAt(pos);
            }
        }
        catch (Exception exception)
        {
            current._api.Logger.Error("[WorldAudit] Game operation capture failed: {0}", exception);
        }
    }

    private static object? Member(object value, string name)
        => AccessTools.Field(value.GetType(), name)?.GetValue(value) ?? AccessTools.Property(value.GetType(), name)?.GetValue(value);

    // Pairing edits the resolved neighbor's handles and offsets before its own methods run.
    private static void DoorResolved(object? __result)
    {
        if (__result is BlockEntityBehavior door) _current?._monitor.Touch(door.Blockentity.Pos);
    }

    private static void QueuedBlock(IBulkBlockAccessor __instance, object[] __args, MethodBase __originalMethod)
    {
        var current = _current;
        var pos = __args.OfType<BlockPos>().FirstOrDefault();
        if (current is null || pos is null || !__instance.StagedBlocks.ContainsKey(pos)) return;
        var layer = __originalMethod.Name == "SetFluidBlock" ? 2
            : __args.Length > 2 && __args[2] is int requested && requested != 0 ? requested
            : current._api.World.GetBlock((int)__args[0]).ForFluidsLayer ? 2 : 1;
        current._queuedActors.GetOrCreateValue(__instance)[(pos.Copy(), layer)] = current._monitor.CurrentScope ?? BlockMutationScope.Synthetic(AuditCause.Unknown);
    }

    private static void DiscardQueued(IBulkBlockAccessor __instance) => _current?._queuedActors.Remove(__instance);

    private static void CaptureStaged(IBulkBlockAccessor accessor)
    {
        var current = _current;
        if (current is null) return;
        var actors = current._queuedActors.GetOrCreateValue(accessor);
        foreach (var pos in accessor.StagedBlocks.Keys.ToArray())
        {
            foreach (var layer in new[] { 1, 2 })
            {
                actors.TryGetValue((pos, layer), out var scope);
                current._monitor.Touch(pos, scope, onlyLayer: layer);
            }
        }
    }

    private static IEnumerable<CodeInstruction> CaptureBeforeBulkWrites(IEnumerable<CodeInstruction> instructions)
    {
        var inserted = false;
        foreach (var instruction in instructions)
        {
            if (!inserted && instruction.operand is MethodInfo method && method.Name == "GetEnumerator"
                && method.DeclaringType?.GenericTypeArguments.Contains(typeof(BlockUpdate)) == true)
            {
                yield return new CodeInstruction(OpCodes.Ldarg_0);
                yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(VintageStoryGameHooks), nameof(CaptureStaged)));
                inserted = true;
            }
            yield return instruction;
        }
        if (!inserted) throw new InvalidOperationException("Vintage Story bulk commit no longer matches the verified 1.22.7 implementation.");
    }

    private static void AfterOperation(object __instance, MethodBase __originalMethod, IDisposable? __state)
    {
        try { __state?.Dispose(); }
        catch (Exception exception) { _current?._api.Logger.Error("[WorldAudit] Game operation completion failed: {0}", exception); }
        if (__instance is IBulkBlockAccessor bulk && __originalMethod.Name is "Commit" or "FastCommit"
            && _current?._queuedActors.TryGetValue(bulk, out var actors) == true)
            foreach (var key in actors.Keys.Where(p => !bulk.StagedBlocks.ContainsKey(p.Position)).ToArray()) actors.Remove(key);
    }

    private static void ContainerAvailable(BlockEntity __instance)
    {
        if (__instance.Api?.Side == EnumAppSide.Server && __instance is IBlockEntityContainer container && container.Inventory is InventoryBase inventory)
            VintageStoryContainerEventBridge.Current?.TrackInventory(inventory);
    }

    private static void ContainerRemoved(BlockEntity __instance)
    {
        if (__instance is IBlockEntityContainer container && container.Inventory is InventoryBase inventory)
            VintageStoryContainerEventBridge.Current?.UntrackInventory(inventory);
    }

    public void Dispose()
    {
        if (!ReferenceEquals(_current, this)) return;
        _current = null;
        _harmony.UnpatchAll(PatchId);
    }
}
