namespace ProsimCompanion.Core.Deice;

/// <summary>Which witness named the fluid GSX sprayed.</summary>
public enum DeiceFluidWitness
{
    /// <summary>Nothing usable: the app did not answer the fluid menu and the LVAR was not
    /// delivered or is outside 0–3.</summary>
    None,

    /// <summary>The entry the app itself picked on GSX's fluid menu in this treatment.</summary>
    MenuAnswer,

    /// <summary><c>L:FSDT_GSX_DEICING_TYPE</c>, read as a zero-based index (an inference).</summary>
    LvarZeroBased,
}

/// <summary>The fluid of one de-icing treatment as the completed edge resolves it.</summary>
/// <param name="FluidType">1 = Type I … 4 = Type IV; 0 = unknown.</param>
/// <param name="Witness">Where <paramref name="FluidType"/> came from.</param>
/// <param name="LvarRaw">The LVAR as read (null = not delivered).</param>
/// <param name="LvarFluidType">The LVAR under the zero-based mapping (1–4), null when it does not map.</param>
/// <param name="Disagrees">The app answered the menu AND the LVAR was delivered AND the two
/// name different fluids under the zero-based mapping — worth one log line with both values:
/// either GSX sprayed something else, or the mapping is wrong.</param>
public sealed record DeiceAppliedFluid(int FluidType, DeiceFluidWitness Witness, int? LvarRaw, int? LvarFluidType, bool Disagrees)
{
    /// <summary>
    /// <c>L:FSDT_GSX_DEICING_TYPE</c> as a fluid type, read as a ZERO-BASED index: 0 = Type I …
    /// 3 = Type IV. Inferred from two flights, not from GSX's documentation (issue #166):
    /// EFHK 2026-10-10 — GSX sprayed Type I and the LVAR read 0 through the whole treatment;
    /// EGLL 2026-11-14 — the app picked "Fluid Type IV" at 04:42:06Z and the LVAR read 3 from
    /// 04:42:07Z, and the card (which read it as 1 = Type I … 4 = Type IV) armed as "Type III".
    /// Only meaningful while a treatment is Active or Completed: at rest the LVAR is 0 too
    /// (or still holds the previous treatment's value), which is not "Type I".
    /// </summary>
    /// <returns>1–4, or null when the value was not delivered or is outside 0–3.</returns>
    public static int? FromLvar(double? raw)
    {
        if (raw is not { } value || double.IsNaN(value))
        {
            return null;
        }

        var index = (int)Math.Round(value);
        return index is >= 0 and <= 3 ? index + 1 : null;
    }

    /// <summary>
    /// The fluid at the de-icing Completed edge. The entry the app itself picked on the fluid
    /// menu in this treatment is the truth — the app knows what it clicked; the LVAR is then
    /// only compared. Without a menu answer (GSX's own choice, or the pilot's pick on GSX's
    /// menu) the LVAR is taken under the zero-based mapping. Pure, for tests.
    /// </summary>
    /// <param name="lvarRaw"><c>L:FSDT_GSX_DEICING_TYPE</c> at the edge; null = not delivered.</param>
    /// <param name="menuFluidType">The fluid-type menu entry the app answered this cycle (1–4), if any.</param>
    public static DeiceAppliedFluid Resolve(double? lvarRaw, int? menuFluidType)
    {
        var lvarType = FromLvar(lvarRaw);
        int? raw = lvarRaw is { } value && !double.IsNaN(value) ? (int)Math.Round(value) : null;

        if (menuFluidType is >= 1 and <= 4)
        {
            return new DeiceAppliedFluid(menuFluidType.Value, DeiceFluidWitness.MenuAnswer, raw, lvarType, raw is not null && lvarType != menuFluidType);
        }

        return lvarType is { } type
            ? new DeiceAppliedFluid(type, DeiceFluidWitness.LvarZeroBased, raw, lvarType, false)
            : new DeiceAppliedFluid(0, DeiceFluidWitness.None, raw, null, false);
    }

    /// <summary>"Type I" … "Type IV"; "unknown" for anything else.</summary>
    public static string Label(int fluidType) => fluidType switch
    {
        1 => "Type I",
        2 => "Type II",
        3 => "Type III",
        4 => "Type IV",
        _ => "unknown",
    };
}
