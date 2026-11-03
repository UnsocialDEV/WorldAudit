using System.Runtime.InteropServices;

namespace WorldAudit.Infrastructure.Persistence;

internal static class SqliteNativeLibraryBootstrapper
{
    private static readonly object Sync = new();
    private static IntPtr _loadedHandle;

    public static void EnsureLoaded(string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);

        var nativeRoot = Path.Combine(baseDirectory, "native");
        if (!Directory.Exists(nativeRoot))
        {
            return;
        }

        var libraryPath = ResolveLibraryPath(nativeRoot);
        if (libraryPath is null)
        {
            return;
        }

        lock (Sync)
        {
            if (_loadedHandle != IntPtr.Zero)
            {
                return;
            }

            _loadedHandle = NativeLibrary.Load(libraryPath);
            // The game can already have a different e_sqlite3 loaded; bind our provider to our packaged build.
            NativeLibrary.SetDllImportResolver(typeof(SQLitePCL.SQLite3Provider_e_sqlite3).Assembly,
                (name, _, _) => name == "e_sqlite3" ? _loadedHandle : IntPtr.Zero);
        }
    }

    private static string? ResolveLibraryPath(string nativeRoot)
    {
        var fileName = GetLibraryFileName();
        var rid = TryGetRuntimeIdentifier();
        if (rid is not null)
        {
            var ridSpecificPath = Path.Combine(nativeRoot, rid, fileName);
            if (File.Exists(ridSpecificPath))
            {
                return ridSpecificPath;
            }
        }

        var flatPath = Path.Combine(nativeRoot, fileName);
        return File.Exists(flatPath) ? flatPath : null;
    }

    private static string GetLibraryFileName()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return "e_sqlite3.dll";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return "libe_sqlite3.dylib";
        }

        return "libe_sqlite3.so";
    }

    private static string? TryGetRuntimeIdentifier()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && RuntimeInformation.ProcessArchitecture == Architecture.X64)
        {
            return "win-x64";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && RuntimeInformation.ProcessArchitecture == Architecture.X64)
        {
            return "linux-x64";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
        {
            return "linux-arm64";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) && RuntimeInformation.ProcessArchitecture == Architecture.X64)
        {
            return "osx-x64";
        }

        return null;
    }
}
