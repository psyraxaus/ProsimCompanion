namespace ProsimCompanion.Core.Configuration;

/// <summary>
/// The FO's own-side cockpit duties (issue #188, November 2026 batch package 9): a real pilot
/// monitoring sets their own altimeter and EFIS and cross-checks the ADIRS and the FMGC. The
/// FO does the same — and nothing more. Every write goes through the FO-side-only gate
/// (<c>FoSideDutiesGate</c>: the FO baro knob, the FO baro value, the FO ND mode and range,
/// seat-mapped) with the FCU announce → cancel window → write → verify flow. All off by default.
/// Nested under <c>speech.foSide</c>.
/// </summary>
public sealed class FoSideDutiesOptions
{
    /// <summary>The FO follows the captain's altimeter: STD when the captain's side goes to
    /// STD, QNH (the captain's value) when it comes back, the new value when the captain's
    /// QNH changes. Also "QNH one zero one three" / "your side standard" by voice. The bare
    /// "set standard" / "set QNH" stay the file commands in commands.json.</summary>
    [OptionField(FieldKind.Bool, "speechSettings.foSide.baroFollow")]
    public bool BaroFollow { get; set; }

    /// <summary>"Your range forty", "ND to arc": the FO's own ND range and mode by voice.</summary>
    [OptionField(FieldKind.Bool, "speechSettings.foSide.efisCommands")]
    public bool EfisCommands { get; set; }

    /// <summary>"Cross-check": the FO reads the ADIRS alignment and the FMGC INIT / PERF
    /// figures against the plan and the computed performance, once per departure when the
    /// FMGC init settles and on request. Read-only.</summary>
    [OptionField(FieldKind.Bool, "speechSettings.foSide.adirsCheck")]
    public bool AdirsCheck { get; set; }
}
