using Microsoft.Win32;

namespace ProsimCompanion.Audio.Backends.VoiceMeeter;

/// <summary>
/// Finds VoicemeeterRemote64.dll without requiring the user to know where VoiceMeeter is
/// installed. An explicitly configured path that exists always wins (non-standard installs);
/// otherwise the vendor-documented discovery route is used — the VoiceMeeter uninstall
/// registry key points at the install folder, which is shared by every edition
/// (Standard/Banana/Potato all ship the same remote DLL) — with the well-known default
/// install folders as a last resort.
/// </summary>
public static class VoiceMeeterLocator
{
    public const string DllFileName = "VoicemeeterRemote64.dll";

    /// <summary>The installer writes a 32-bit uninstall key; opened via the Registry32 view so
    /// the same path works from a 64-bit process.</summary>
    private const string UninstallKeyPath =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\VB:Voicemeeter {17359A74-1236-5467}";

    /// <summary>Resolves the DLL path to use, or "" when VoiceMeeter cannot be found at all
    /// (the backend then keeps degrading exactly as an unconfigured path always has).</summary>
    public static string Resolve(string configuredPath) =>
        Resolve(configuredPath, File.Exists, ReadUninstallString);

    /// <summary>Testable core — candidate precedence only; filesystem and registry probing
    /// are injected.</summary>
    public static string Resolve(
        string configuredPath, Func<string, bool> fileExists, Func<string?> readUninstallString)
    {
        ArgumentNullException.ThrowIfNull(fileExists);
        ArgumentNullException.ThrowIfNull(readUninstallString);

        if (!string.IsNullOrWhiteSpace(configuredPath) && fileExists(configuredPath))
        {
            return configuredPath;
        }

        var installDir = InstallDirFromUninstallString(readUninstallString());
        if (installDir is not null)
        {
            var candidate = Path.Combine(installDir, DllFileName);
            if (fileExists(candidate))
            {
                return candidate;
            }
        }

        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                 })
        {
            if (string.IsNullOrEmpty(root))
            {
                continue;
            }

            var candidate = Path.Combine(root, "VB", "Voicemeeter", DllFileName);
            if (fileExists(candidate))
            {
                return candidate;
            }
        }

        return "";
    }

    /// <summary>UninstallString is the uninstaller's full path (sometimes quoted); its folder
    /// is the install folder.</summary>
    private static string? InstallDirFromUninstallString(string? uninstallString)
    {
        if (string.IsNullOrWhiteSpace(uninstallString))
        {
            return null;
        }

        try
        {
            return Path.GetDirectoryName(uninstallString.Trim().Trim('"'));
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException)
        {
            return null;
        }
    }

    private static string? ReadUninstallString()
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
            using var key = baseKey.OpenSubKey(UninstallKeyPath);
            return key?.GetValue("UninstallString") as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
