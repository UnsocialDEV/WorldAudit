using Vintagestory.API.Datastructures;

namespace WorldAudit.Integration.VintageStory;

internal static class ExtinguishedPile
{
    internal static void Apply(TreeAttribute tree)
    {
        tree.SetBool("burning", false);
        tree.SetBool("isBurning", false);
    }
}
