namespace ProsimCompanion.Prosim.Sdk;

/// <summary>
/// Resolves the directory containing ProSimSDK.dll from the user-configured path. There is no
/// assumed install location: the path comes from config/settings.json (installer or web Settings).
/// </summary>
public static class SdkLocator
{
    private const string SdkFileName = "ProSimSDK.dll";

    /// <summary>
    /// Returns the directory containing ProSimSDK.dll, or null when the configured path is empty
    /// or the dll cannot be found there. Accepts either a directory or a full dll path.
    /// </summary>
    public static string? Locate(string? configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return null;
        }

        var path = configuredPath.Trim();

        if (File.Exists(path))
        {
            return Path.GetDirectoryName(path);
        }

        if (Directory.Exists(path) && File.Exists(Path.Combine(path, SdkFileName)))
        {
            return path;
        }

        return null;
    }
}
