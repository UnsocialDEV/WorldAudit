using Vintagestory.API.Datastructures;

namespace WorldAudit.Integration.VintageStory;

internal static class UnlitBomb
{
    internal static void Apply(TreeAttribute tree)
    {
        tree.SetInt("lit", 0);
        tree.SetFloat("remainingSeconds", 0);
        tree.SetString("ignitedByPlayerUid", string.Empty);
    }
}
