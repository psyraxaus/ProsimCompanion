using ProsimCompanion.Core.Airports.Parking;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Weather;

namespace ProsimCompanion.Core.Hosting;

/// <summary>
/// Where each settings path field expects its file or folder to be — the places the Browse…
/// dialog opens in when the field is empty or points somewhere that is gone (owner request
/// 2026-10-13). One list per field, first existing entry wins, else the system drive; the
/// lists that the app already derives elsewhere (MSFS package roots, ActiveSky snapshot
/// folders, the installer's ProSim SDK search) are reused or mirrored here so the dialog and
/// the auto-detection never disagree. Every method is cheap to call at click time; none is
/// meant for a render loop.
/// </summary>
public static class ExpectedFolders
{
    /// <summary>The last resort: <c>C:\</c> (whatever %SystemDrive% is). Handing the dialog
    /// a folder that does not exist makes Windows open its last-used one, which reads as
    /// random — so the fallback is always a real place.</summary>
    public static string SystemDrive
        => (Environment.GetEnvironmentVariable("SystemDrive") ?? "C:") + Path.DirectorySeparatorChar;

    /// <summary>The ProSim SDK folder: the same candidates the installer's DetectProSimSdk
    /// probes (installer/ProsimCompanion.iss), as folders.</summary>
    public static IReadOnlyList<string> ProSimSdk()
        =>
        [
            @"C:\prosim\prosim-system",
            @"C:\ProSim-AR",
            @"C:\ProSim",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ProSim-AR"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "ProSim-AR"),
        ];

    /// <summary>The SkalarkiIO SDK assemblies: the Profiler's <c>Tools</c> folder has the SDK
    /// plus every support assembly it needs, the install root the SDK alone (2026-10-27,
    /// docs/integrations/skalarki.md).</summary>
    public static IReadOnlyList<string> SkalarkiSdk()
        =>
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "SkalarkiIO Profiler 5.1", "Tools"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "SkalarkiIO Profiler 5.1", "Tools"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "SkalarkiIO Profiler 5.1"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "SkalarkiIO Profiler 5.1"),
        ];

    /// <summary>ProSim's navdata (the DFD .s3db): <c>%ProgramData%\ProSim-AR\Navdata</c>
    /// (owner, 2026-10-13 — it does not live beside the SDK).</summary>
    public static IReadOnlyList<string> ProSimNavdata()
        => [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ProSim-AR", "Navdata")];

    /// <summary>VoiceMeeter's default install folders (every edition ships the same remote DLL
    /// there). The audio locator probes the same list after the registry.</summary>
    public static IReadOnlyList<string> VoiceMeeter()
        =>
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "VB", "Voicemeeter"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "VB", "Voicemeeter"),
        ];

    /// <summary>The vJoy driver's 64-bit folder, where its installer puts <c>vJoyInterface.dll</c>
    /// (2026-10-24, the input relay). The relay probes the same list when the path is empty.</summary>
    public static IReadOnlyList<string> VJoy()
        =>
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "vJoy", "x64"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "vJoy", "x64"),
        ];

    /// <summary>Where ActiveSky writes its snapshot — the weather provider's own candidates.</summary>
    public static IReadOnlyList<string> ActiveSky() => ActiveSkyWxProvider.KnownSnapshotFolders();

    /// <summary>GSX's user profile folder.</summary>
    public static IReadOnlyList<string> Gsx() => [MsfsPackageRoots.GsxUserProfileFolder];

    /// <summary>The MSFS package roots the gate resolver detects (Store, Steam, UserCfg.opt).</summary>
    public static IReadOnlyList<string> MsfsPackages() => MsfsPackageRoots.Detect();

    /// <summary>The planned-rotations folder the app seeds.</summary>
    public static IReadOnlyList<string> Rotations() => [UserConfigPaths.Rotations];

    /// <summary><c>%LOCALAPPDATA%\ProsimCompanion</c>: logs, the config tree, and the state
    /// files (day state, tech log, logbook) by default.</summary>
    public static IReadOnlyList<string> AppStateFolder()
        => [Path.GetDirectoryName(UserConfigPaths.Root) ?? UserConfigPaths.Root];

    /// <summary>The shipped wear pool's folder beside the exe.</summary>
    public static IReadOnlyList<string> WearPool()
        => [Path.Combine(AppContext.BaseDirectory, "config", "techlog")];

    /// <summary>Where a downloaded key file lands: Downloads, then Documents.</summary>
    public static IReadOnlyList<string> Downloads()
        =>
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        ];
}
