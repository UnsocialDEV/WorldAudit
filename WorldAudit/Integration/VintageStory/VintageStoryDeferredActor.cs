using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace WorldAudit.Integration.VintageStory;

internal static class VintageStoryDeferredActor
{
    internal static Delegate Bind(Delegate callback, Func<IDisposable?> enter) => callback switch
    {
        Action task => (Action)(() =>
        {
            using var scope = enter();
            task();
        }),
        Action<float> delayed => (Action<float>)(elapsed =>
        {
            using var scope = enter();
            delayed(elapsed);
        }),
        Action<IWorldAccessor, BlockPos, float> positioned => (Action<IWorldAccessor, BlockPos, float>)((world, pos, elapsed) =>
        {
            using var scope = enter();
            positioned(world, pos, elapsed);
        }),
        _ => callback
    };
}
