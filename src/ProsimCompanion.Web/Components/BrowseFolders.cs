using ProsimCompanion.Core.Hosting;

namespace ProsimCompanion.Web.Components;

/// <summary>
/// Names the expected-folder lists a path field's Browse… dialog may open in
/// (<see cref="OptionFieldAttribute.BrowseStartIn"/> → a method of <see cref="ExpectedFolders"/>).
/// A map rather than reflection on the method name, so a renamed method fails here, in one
/// place, and the completeness test can prove every description names a real list. The lists
/// stay functions: some probe the file system and must run at click time, never per render.
/// </summary>
public static class BrowseFolders
{
    private static readonly IReadOnlyDictionary<string, Func<IReadOnlyList<string>>> Lists =
        new Dictionary<string, Func<IReadOnlyList<string>>>(StringComparer.Ordinal)
        {
            [nameof(ExpectedFolders.ProSimSdk)] = ExpectedFolders.ProSimSdk,
            [nameof(ExpectedFolders.ProSimNavdata)] = ExpectedFolders.ProSimNavdata,
            [nameof(ExpectedFolders.VoiceMeeter)] = ExpectedFolders.VoiceMeeter,
            [nameof(ExpectedFolders.ActiveSky)] = ExpectedFolders.ActiveSky,
            [nameof(ExpectedFolders.Gsx)] = ExpectedFolders.Gsx,
            [nameof(ExpectedFolders.MsfsPackages)] = ExpectedFolders.MsfsPackages,
            [nameof(ExpectedFolders.Rotations)] = ExpectedFolders.Rotations,
            [nameof(ExpectedFolders.AppStateFolder)] = ExpectedFolders.AppStateFolder,
            [nameof(ExpectedFolders.WearPool)] = ExpectedFolders.WearPool,
            [nameof(ExpectedFolders.Downloads)] = ExpectedFolders.Downloads,
        };

    /// <summary>True when <paramref name="name"/> names a list.</summary>
    public static bool IsKnown(string name) => Lists.ContainsKey(name);

    /// <summary>The list for a name; null for null/blank (no start folder), an exception for
    /// a name that is not a list — a description typo is a build-time mistake, not a silent
    /// dialog that opens on the system drive.</summary>
    public static Func<IReadOnlyList<string>>? Resolve(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return Lists.TryGetValue(name, out var list)
            ? list
            : throw new InvalidOperationException($"'{name}' is not an ExpectedFolders list; see BrowseFolders.");
    }
}
