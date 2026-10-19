namespace ProsimCompanion.Gsx.Automation;

/// <summary>
/// The three facts the pushback shell reads from the departure automation: the automation
/// phase and the departure cycle's started/complete flags. A seam so the pushback tick's
/// inputs gatherer is faked without the automation host behind it.
/// </summary>
public interface IGsxAutomationPhaseView
{
    /// <summary>Current automation phase (derived from the flight state engine).</summary>
    GsxAutomationPhase Phase { get; }

    /// <summary>True once the departure sequence has been started this cycle.</summary>
    bool DepartureStarted { get; }

    /// <summary>True once every departure service completed or was skipped.</summary>
    bool DepartureComplete { get; }
}
