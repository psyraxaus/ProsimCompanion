namespace ProsimCompanion.Core.Updates;

/// <summary>
/// A release tag or running version read as a semantic version: <c>major.minor.patch</c>, an
/// optional pre-release part (<c>-rc.18</c>) and optional build metadata (<c>+sha</c>, ignored).
///
/// Issue #159 (2026-10-05): the update check compared with <see cref="Version"/> and stripped
/// the pre-release part from the running version only, so the tag <c>v0.6.0-rc.18</c> never
/// parsed and every release-candidate user read "update available: False". Stripping it from
/// both sides would not have helped either — rc.16 and rc.18 are then the same 0.6.0 — so the
/// pre-release identifiers are compared, by the semver.org precedence rules.
/// </summary>
internal readonly struct ReleaseVersion : IComparable<ReleaseVersion>
{
    private readonly Version _core;
    private readonly string[] _preRelease;

    private ReleaseVersion(Version core, string[] preRelease)
    {
        _core = core;
        _preRelease = preRelease;
    }

    /// <summary>Reads an optionally v-prefixed version. False for anything that is not one
    /// ("nightly", an empty string, "1.0.0-" with an empty identifier).</summary>
    public static bool TryParse(string? text, out ReleaseVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var value = text.Trim().TrimStart('v', 'V');
        var metadata = value.IndexOf('+', StringComparison.Ordinal);
        if (metadata >= 0)
        {
            value = value[..metadata];
        }

        var preRelease = Array.Empty<string>();
        var dash = value.IndexOf('-', StringComparison.Ordinal);
        if (dash >= 0)
        {
            preRelease = value[(dash + 1)..].Split('.');
            if (preRelease.Any(string.IsNullOrWhiteSpace))
            {
                return false;
            }

            value = value[..dash];
        }

        if (!Version.TryParse(value, out var core))
        {
            return false;
        }

        // "1.0" and "1.0.0" are the same release: Version reads a missing part as -1.
        version = new ReleaseVersion(new Version(core.Major, core.Minor, Math.Max(core.Build, 0)), preRelease);
        return true;
    }

    /// <summary>Semver precedence: the core version first; on a tie a final release is above
    /// its pre-releases; two pre-releases compare identifier by identifier.</summary>
    public int CompareTo(ReleaseVersion other)
    {
        var core = (_core ?? new Version(0, 0, 0)).CompareTo(other._core ?? new Version(0, 0, 0));
        if (core != 0)
        {
            return core;
        }

        var mine = _preRelease ?? [];
        var theirs = other._preRelease ?? [];
        if (mine.Length == 0 || theirs.Length == 0)
        {
            // 0.6.0 is newer than 0.6.0-rc.18: the side with no pre-release part wins.
            return (mine.Length == 0 ? 1 : 0) - (theirs.Length == 0 ? 1 : 0);
        }

        for (var i = 0; i < Math.Min(mine.Length, theirs.Length); i++)
        {
            var identifier = CompareIdentifier(mine[i], theirs[i]);
            if (identifier != 0)
            {
                return identifier;
            }
        }

        // rc.1.1 is above rc.1.
        return mine.Length.CompareTo(theirs.Length);
    }

    private static int CompareIdentifier(string left, string right)
    {
        var leftNumeric = ulong.TryParse(left, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var leftNumber);
        var rightNumeric = ulong.TryParse(right, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var rightNumber);

        if (leftNumeric && rightNumeric)
        {
            // rc.2 is below rc.10: numbers compare as numbers, not as text.
            return leftNumber.CompareTo(rightNumber);
        }

        if (leftNumeric != rightNumeric)
        {
            // A number is below a word.
            return leftNumeric ? -1 : 1;
        }

        return Math.Sign(string.Compare(left, right, StringComparison.OrdinalIgnoreCase));
    }
}
