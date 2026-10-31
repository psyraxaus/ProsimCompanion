using System.Collections.Frozen;

namespace ProsimCompanion.Prosim.Failures;

/// <summary>
/// The code-level gate on ProSim FAILURE writes (issue #197, ADR-0018) — separate from
/// <see cref="DataRefs.ProsimWriteGate"/> on purpose: that gate lists datarefs, this one lists
/// ProSim failure NAMES, and the two never meet. A name passes only when it is (a) in the
/// scenario catalogue's verified entries, installed once at start by the injector from the
/// Core bank, AND (b) in ProSim's own live list (<c>getFailures()</c>) at the moment of the
/// write. There is no dataref path through this type: nothing here takes a dataref name, and
/// the <c>system.config.</c> family (ProSim's random-failure generator) stays as closed as it
/// was on <see cref="DataRefs.ProsimWriteGate"/> — a test pins both facts.
/// </summary>
public static class ProsimFailureGate
{
    private static FrozenSet<string> _catalogue = FrozenSet<string>.Empty;

    /// <summary>The catalogue's verified failure names, as the injector installed them.</summary>
    public static IReadOnlySet<string> CatalogueNames => _catalogue;

    /// <summary>Installs the catalogue's verified failure names (the ONE widening, at start and
    /// on a catalogue reload). Internal on purpose: only this assembly — and the test project
    /// through InternalsVisibleTo — can install a list, so "one implementer" is enforced by
    /// the compiler.</summary>
    internal static void InstallCatalogue(IEnumerable<string> verifiedFailureNames)
    {
        ArgumentNullException.ThrowIfNull(verifiedFailureNames);
        _catalogue = verifiedFailureNames
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .ToFrozenSet(StringComparer.Ordinal);
    }

    /// <summary>True when the failure may be armed by this application: in the installed
    /// catalogue AND in ProSim's live list. A blank name, an empty catalogue or an empty live
    /// list refuse.</summary>
    public static bool IsAllowed(string failureName, IReadOnlySet<string> liveFailureNames)
    {
        ArgumentNullException.ThrowIfNull(liveFailureNames);
        if (string.IsNullOrWhiteSpace(failureName))
        {
            return false;
        }

        var name = failureName.Trim();
        return _catalogue.Contains(name) && liveFailureNames.Contains(name);
    }

    /// <summary>Throws when the failure is not allow-listed for arming.</summary>
    public static void EnsureAllowed(string failureName, IReadOnlySet<string> liveFailureNames)
    {
        if (!IsAllowed(failureName, liveFailureNames))
        {
            throw new InvalidOperationException(
                $"Failure '{failureName}' is not on the scenario allow-list (catalogue ∩ ProSim's live list). " +
                "Failure writes are gated by design — add a verified catalogue entry if this is intended.");
        }
    }
}
