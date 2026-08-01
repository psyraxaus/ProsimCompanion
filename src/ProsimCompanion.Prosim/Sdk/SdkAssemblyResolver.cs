using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace ProsimCompanion.Prosim.Sdk;

/// <summary>
/// Loads ProSimSDK.dll at runtime from the user's ProSim installation. The assembly is compiled
/// against with Private=false, so it is never in our output directory — this resolver supplies it
/// when the CLR first needs it, and <c>SetDllDirectory</c> lets its native dependencies resolve
/// from the same folder.
/// </summary>
internal static class SdkAssemblyResolver
{
    private const string SdkAssemblyName = "ProSimSDK";

    private static string? _directory;
    private static int _registered;

    /// <summary>Registers the resolver for the given SDK directory. Idempotent; the most recently
    /// registered directory wins if called again (e.g. after a settings change).</summary>
    public static void Register(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        _directory = directory;
        SetDllDirectory(directory);

        if (Interlocked.Exchange(ref _registered, 1) == 1)
        {
            return;
        }

        AssemblyLoadContext.Default.Resolving += (context, assemblyName) =>
        {
            if (!string.Equals(assemblyName.Name, SdkAssemblyName, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var directory = _directory;
            if (directory is null)
            {
                return null;
            }

            var path = Path.Combine(directory, SdkAssemblyName + ".dll");
            return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
        };
    }

#pragma warning disable SYSLIB1054 // LibraryImport would force AllowUnsafeBlocks on the project for one call
    [DllImport("kernel32.dll", EntryPoint = "SetDllDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDllDirectory(string path);
#pragma warning restore SYSLIB1054
}
