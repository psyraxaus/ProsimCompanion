using System.Reflection;
using System.Runtime.InteropServices;

namespace ProsimCompanion.Core.Diagnostics;

/// <summary>
/// <see cref="IAppBuildInfo"/> read from an assembly's <see cref="AssemblyInformationalVersionAttribute"/>.
/// The entry assembly is the right source in the app (every project shares the version from
/// Directory.Build.props, but the exe is what the user actually installed); a host without a
/// managed entry point (or the test runner) falls back to the Core assembly so the value is
/// still the repository's version rather than the harness's.
/// </summary>
public sealed class AppBuildInfo : IAppBuildInfo
{
    private static readonly Lazy<AppBuildInfo> CurrentInstance = new(() => FromAssembly(
        Assembly.GetEntryAssembly() ?? typeof(AppBuildInfo).Assembly));

    private AppBuildInfo(string informationalVersion, string runtime, string os, string architecture)
    {
        InformationalVersion = informationalVersion;
        var plus = informationalVersion.IndexOf('+', StringComparison.Ordinal);
        Version = plus > 0 ? informationalVersion[..plus] : informationalVersion;
        Commit = plus > 0 && plus < informationalVersion.Length - 1 ? informationalVersion[(plus + 1)..] : null;
        Runtime = runtime;
        Os = os;
        Architecture = architecture;
    }

    /// <summary>The running process's build, resolved once.</summary>
    public static AppBuildInfo Current => CurrentInstance.Value;

    public string Version { get; }

    public string InformationalVersion { get; }

    public string? Commit { get; }

    public string Runtime { get; }

    public string Os { get; }

    public string Architecture { get; }

    /// <summary>Reads the version from <paramref name="assembly"/>: the informational version,
    /// else the three-part assembly version, else "0.0.0". Exposed so tests can stamp a
    /// known assembly instead of whatever the test host is.</summary>
    public static AppBuildInfo FromAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(informational))
        {
            informational = assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        }

        return new AppBuildInfo(
            informational,
            RuntimeInformation.FrameworkDescription,
            RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString());
    }

    /// <summary>A fully specified instance for tests and fixtures — no reflection involved.</summary>
    public static AppBuildInfo Create(string informationalVersion, string runtime, string os, string architecture)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(informationalVersion);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(os);
        ArgumentNullException.ThrowIfNull(architecture);
        return new AppBuildInfo(informationalVersion, runtime, os, architecture);
    }

    public string Describe()
        => $"ProsimCompanion {Version} ({Commit ?? "no commit"}) on {Os} / {Runtime}, {Architecture}";
}
