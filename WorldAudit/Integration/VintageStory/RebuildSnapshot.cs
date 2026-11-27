using Vintagestory.API.Datastructures;

namespace WorldAudit.Integration.VintageStory;

internal static class RebuildSnapshot
{
    internal static byte[]? Prepare(Type? entityType, byte[]? recorded)
    {
        if (recorded is null) return null;
        for (var type = entityType; type is not null; type = type.BaseType)
        {
            Action<TreeAttribute>? disarm = type.FullName switch
            {
                "Vintagestory.GameContent.BlockEntityBomb" => UnlitBomb.Apply,
                "Vintagestory.GameContent.BlockEntityGroundStorage" => ExtinguishedPile.Apply,
                _ => null
            };
            if (disarm is null) continue;
            var tree = new TreeAttribute();
            tree.FromBytes(recorded);
            disarm(tree);
            return tree.ToBytes();
        }
        return recorded;
    }
}
