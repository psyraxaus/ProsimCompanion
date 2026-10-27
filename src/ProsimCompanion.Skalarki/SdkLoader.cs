using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Extensions.Logging;

namespace ProsimCompanion.Skalarki;

/// <summary>
/// Finds the user's SkalarkiIO SDK and loads the one assembly that talks to it
/// (<c>ProsimCompanion.Skalarki.Sdk.dll</c>, built beside the app) into a load context of its
/// own, where the SDK and its .NET Framework support assemblies resolve from the user's folder
/// and everything else falls back to the app's default context — so the bridge sees the same
/// <see cref="ISkalarkiClient"/> type the service uses (ADR-0017). Nothing here touches an SDK
/// type: the main app never references the SDK at compile time.
/// </summary>
public sealed class SdkLoader : ISkalarkiClientFactory
{
    public const string SdkFileName = "SkalarkiIO.SDK.dll";

    /// <summary>The bridge assembly, built from src/ProsimCompanion.Skalarki.Sdk and copied
    /// beside the app; empty (no client type) when the app was built without the SDK.</summary>
    public const string BridgeFileName = "ProsimCompanion.Skalarki.Sdk.dll";

    /// <summary>What must sit beside <see cref="SdkFileName"/>: the SDK's own helpers and the
    /// two .NET Framework packages it was compiled against (the spike proved these four are
    /// enough on .NET 10; the Profiler's Tools folder ships all of them).</summary>
    public static readonly IReadOnlyList<string> RequiredFiles =
    [
        SdkFileName,
        "SkalarkiIO.SDK.Telemetry.dll",
        "System.Threading.Tasks.Dataflow.dll",
        "Microsoft.Diagnostics.Tracing.EventSource.dll",
    ];

    /// <summary>Assemblies the SDK context loads from the user's folder (the rest comes from
    /// the app's default context). Diagnostics.Logic is optional: internal types only.</summary>
    private static readonly HashSet<string> SdkAssemblyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "SkalarkiIO.SDK",
        "SkalarkiIO.SDK.Telemetry",
        "SkalarkiIO.SDK.Diagnostics.Logic",
        "System.Threading.Tasks.Dataflow",
        "Microsoft.Diagnostics.Tracing.EventSource",
    };

    private static readonly ConcurrentDictionary<string, SkalarkiLoadContext> Contexts = new(StringComparer.OrdinalIgnoreCase);

    private readonly ILoggerFactory _loggers;
    private readonly string _bridgeDirectory;

    public SdkLoader(ILoggerFactory loggers)
        : this(loggers, AppContext.BaseDirectory)
    {
    }

    /// <param name="bridgeDirectory">Where the bridge assembly lives; the app folder in
    /// production, a scratch folder in tests.</param>
    public SdkLoader(ILoggerFactory loggers, string bridgeDirectory)
    {
        ArgumentNullException.ThrowIfNull(loggers);
        ArgumentException.ThrowIfNullOrWhiteSpace(bridgeDirectory);
        _loggers = loggers;
        _bridgeDirectory = bridgeDirectory;
    }

    /// <summary>The folder holding <see cref="SdkFileName"/> for a configured value (a folder
    /// or the dll's full path), or null when it is empty or the dll is not there.</summary>
    public static string? Locate(string? configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return null;
        }

        var path = configuredPath.Trim();
        if (File.Exists(path) && string.Equals(Path.GetFileName(path), SdkFileName, StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetDirectoryName(path);
        }

        return Directory.Exists(path) && File.Exists(Path.Combine(path, SdkFileName)) ? path : null;
    }

    /// <summary>The SDK's file version in a folder ("5.1.919.1000"), for the settings page's
    /// "found" line; null when the dll is not there.</summary>
    public static string? DescribeSdk(string? configuredPath)
    {
        var directory = Locate(configuredPath);
        if (directory is null)
        {
            return null;
        }

        try
        {
            return FileVersionInfo.GetVersionInfo(Path.Combine(directory, SdkFileName)).FileVersion;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public string? Validate(string sdkPath)
    {
        if (string.IsNullOrWhiteSpace(sdkPath))
        {
            return "No Skalarki SDK folder is set.";
        }

        var directory = Locate(sdkPath);
        if (directory is null)
        {
            return $"{SdkFileName} was not found in the configured Skalarki SDK folder.";
        }

        foreach (var file in RequiredFiles)
        {
            if (!File.Exists(Path.Combine(directory, file)))
            {
                return $"{file} is missing beside {SdkFileName}; point the folder at the Profiler's Tools folder, which has every file.";
            }
        }

        if (!File.Exists(Path.Combine(_bridgeDirectory, BridgeFileName)))
        {
            return $"This build of the app has no Skalarki SDK bridge ({BridgeFileName}).";
        }

        return null;
    }

    public ISkalarkiClient Create(string sdkPath)
    {
        var refusal = Validate(sdkPath);
        if (refusal is not null)
        {
            throw new InvalidOperationException(refusal);
        }

        var directory = Locate(sdkPath)!;
        var context = Contexts.GetOrAdd(Path.GetFullPath(directory), dir => new SkalarkiLoadContext(dir));
        var bridge = context.LoadFromAssemblyPath(Path.Combine(_bridgeDirectory, BridgeFileName));
        var clientType = bridge.GetTypes()
            .FirstOrDefault(t => !t.IsAbstract && typeof(ISkalarkiClient).IsAssignableFrom(t))
            ?? throw new InvalidOperationException($"{BridgeFileName} was built without the Skalarki SDK and has no client in it.");

        var logger = _loggers.CreateLogger(clientType);
        return (ISkalarkiClient)(Activator.CreateInstance(clientType, logger)
            ?? throw new InvalidOperationException($"{clientType.Name} could not be created."));
    }

    /// <summary>One context per SDK folder: the SDK assemblies come from there, the rest from
    /// the default context (so Core/Skalarki types are shared, not duplicated). Not collectible:
    /// the SDK keeps static state and Dataflow threads, and a settings change rebuilds the
    /// client, never the context.</summary>
    private sealed class SkalarkiLoadContext(string directory) : AssemblyLoadContext("SkalarkiSdk:" + directory)
    {
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name is { } name && SdkAssemblyNames.Contains(name))
            {
                var path = Path.Combine(directory, name + ".dll");
                if (File.Exists(path))
                {
                    return LoadFromAssemblyPath(path);
                }
            }

            return null;
        }
    }
}
