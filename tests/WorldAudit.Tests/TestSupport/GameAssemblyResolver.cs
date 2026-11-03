using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

namespace WorldAudit.Tests.TestSupport;

internal static class GameAssemblyResolver
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        var directory = typeof(GameAssemblyResolver).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == "VintageStoryDirectory").Value!;
        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            foreach (var folder in new[] { directory, Path.Combine(directory, "Lib"), Path.Combine(directory, "Mods") })
            {
                var path = Path.Combine(folder, name.Name + ".dll");
                if (File.Exists(path)) return context.LoadFromAssemblyPath(path);
            }
            return null;
        };
    }
}
