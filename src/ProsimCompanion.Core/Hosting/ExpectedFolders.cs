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

    /// <summary><c>%LOCALAPPDATA%\ProsimCompanion</c>: logs, state files, the config tree.</summary>
    public static string AppState
        => Path.GetDirectoryName(UserConfigPaths.Root) ?? UserConfigPaths.Root;

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

    /// <summary>Where ActiveSky writes its snapshot — the weather provider's own candidates.</summary>
    public static IReadOnlyList<string> ActiveSky() => ActiveSkyWxProvider.KnownSnapshotFolders();

    /// <summary>GSX's user profile folder.</summary>
    public static IReadOnlyList<string> Gsx() => [MsfsPackageRoots.GsxUserProfileFolder];

    /// <summary>The MSFS package roots the gate resolver detects (Store, Steam, UserCfg.opt).</summary>
    public static IReadOnlyList<string> MsfsPackages() => MsfsPackageRoots.Detect();

    /// <summary>The planned-rotations folder the app seeds.</summary>
    public static IReadOnlyList<string> Rotations() => [UserConfigPaths.Rotations];

    /// <summary>The per-user state folder (day state, tech log, logbook live there by default).</summary>
    public static IReadOnlyList<string> AppStateFolder() => [AppState];

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
