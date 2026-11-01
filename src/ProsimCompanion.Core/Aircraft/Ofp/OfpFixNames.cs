using ProsimCompanion.Core.Speech;

namespace ProsimCompanion.Core.Aircraft.Ofp;

/// <summary>
/// <see cref="IFixNames"/> over the loaded OFP's navlog (owner request 2026-10-29): SimBrief
/// prints every fix with its name ("BPK" → "BROOKMANS PARK"), and the route's own fixes are
/// exactly the ones a SID / STAR is named after — so no world-wide ambiguity (the three "LAM"
/// VORs) and no database needed. A procedure fix (is_sid_star) wins over an enroute one of the
/// same ident; a fix whose name only repeats its ident has no name to offer. The airport hint
/// is ignored: the navlog is already this flight's.
/// </summary>
public sealed class OfpFixNames : IFixNames
{
    private readonly OfpStore _store;

    public OfpFixNames(OfpStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    /// <inheritdoc />
    public string? Name(string ident, string? nearAirportIcao)
    {
        if (string.IsNullOrWhiteSpace(ident))
        {
            return null;
        }

        var navlog = _store.Current?.Navlog;
        if (navlog is null || navlog.Count == 0)
        {
            return null;
        }

        var wanted = ident.Trim();
        OfpFix? best = null;
        foreach (var fix in navlog)
        {
            if (!fix.Ident.Equals(wanted, StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(fix.Name)
                || fix.Name.Trim().Equals(wanted, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (best is null || (fix.IsProcedure && !best.IsProcedure))
            {
                best = fix;
            }
        }

        if (best is not null)
        {
            return best.Name?.Trim();
        }

        // A four-letter base is a five-letter fix cut short (SayIntentions' flight.json names
        // the EHAM STAR "REDF1A" for REDFA; issue #202): the one navlog fix that starts with
        // it IS the fix — its ident ("REDFA") is the word to say, never a world-wide guess
        // ("Redfern Railway Station", 2026-11-01). Two candidates = no answer.
        if (wanted.Length == 4)
        {
            string? only = null;
            foreach (var fix in navlog)
            {
                if (fix.Ident.Length == 5 && fix.Ident.StartsWith(wanted, StringComparison.OrdinalIgnoreCase))
                {
                    if (only is not null && !only.Equals(fix.Ident, StringComparison.OrdinalIgnoreCase))
                    {
                        return null;
                    }

                    only = fix.Ident;
                }
            }

            return only;
        }

        return null;
    }
}
