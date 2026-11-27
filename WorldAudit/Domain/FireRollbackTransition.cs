namespace WorldAudit.Domain;

internal static class FireRollbackTransition
{
    internal static RollbackBlockPlanEntry? Resolve(RollbackBlockPlanEntry entry, string currentCode)
    {
        var expectedFire = FireBlock.Matches(entry.ExpectedBlockCode);
        var targetFire = FireBlock.Matches(entry.TargetBlockCode);
        if (!expectedFire && !targetFire) return entry;

        // Older previews can still contain ignition, burnout, and fire ticks.
        if ((targetFire || IsAir(entry.TargetBlockCode)) && (expectedFire || IsAir(entry.ExpectedBlockCode)))
            return null;

        if (targetFire)
            return entry with { TargetBlockCode = "game:air", TargetBlockEntitySnapshot = null };

        // A flame that replaced a building block may already have burned out.
        return IsAir(currentCode)
            ? entry with { ExpectedBlockCode = "game:air", ExpectedBlockEntitySnapshot = null }
            : entry;

        static bool IsAir(string? code) => string.IsNullOrWhiteSpace(code) || string.Equals(code, "game:air", StringComparison.OrdinalIgnoreCase);
    }
}
