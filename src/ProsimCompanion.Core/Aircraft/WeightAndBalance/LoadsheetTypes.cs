namespace ProsimCompanion.Core.Aircraft.WeightAndBalance;

/// <summary>
/// Per-flight metadata rendered into a loadsheet (JSON envelope + ACARS text). Populated from
/// the OFP and live max-weight datarefs; the orchestrator fills EditionNumber and the
/// dispatcher pair. Mutable by design — the generator stamps fields as it goes.
/// </summary>
public sealed class LoadsheetContext
{
    public string Ident { get; set; } = "";
    public string Callsign { get; set; } = "";
    public string DepartureIata { get; set; } = "";
    public string ArrivalIata { get; set; } = "";
    public string AircraftTailNumber { get; set; } = "";
    public DateTime Time { get; set; } = DateTime.UtcNow;

    /// <summary>Rendered e.g. "10 10May26" (SimBrief sched_out through format "d dMMMyy").</summary>
    public string ScheduledDepartureTime { get; set; } = "";

    /// <summary>Increments per prelim (resends included); the final inherits the prelim's.</summary>
    public int EditionNumber { get; set; } = 1;

    public double MaxZfwKg { get; set; }
    public double MaxTowKg { get; set; }
    public double MaxLawKg { get; set; }

    /// <summary>Potable water + waste payload printed on the prelim (A322 default 1080 kg).</summary>
    public double WaterWasteKg { get; set; }

    public string DispatcherFirstName { get; set; } = "";
    public string DispatcherLastName { get; set; } = "";
    public string DispatcherPhone { get; set; } = "44 20543 53533";
    public string DispatcherLicense { get; set; } = "PRG235";

    public int Infants { get; set; }
    public string InfantsOutput { get; set; } = "";

    /// <summary>REVISIONS/CHK thresholds. ProSim's own generator uses 1000 kg / 2.0 %MAC; the
    /// in-house pipeline keeps the predecessor's tighter 0.5 %MAC (pilot-conservative).</summary>
    public double WeightChangeToleranceKg { get; set; } = 1000.0;
    public double MacChangeTolerancePercent { get; set; } = 0.5;
}

/// <summary>Planned figures from the OFP feeding the preliminary calculation.</summary>
public sealed class FlightPlanInputs
{
    public int PassengerCount { get; set; }
    public double CargoTotalKg { get; set; }
    public double PlannedFuelKg { get; set; }
    public double PlannedLandingFuelKg { get; set; }
    public double TaxiFuelKg { get; set; }
    public double EstimatedZeroFuelWeightKg { get; set; }
    public double EstimatedTakeoffWeightKg { get; set; }
    public double EstimatedLandingWeightKg { get; set; }
}

/// <summary>Result of a preliminary or final W&amp;B calculation.</summary>
public sealed class LoadsheetData
{
    public double ZeroFuelWeight { get; set; }
    public double ZeroFuelWeightMac { get; set; }
    public double TakeoffWeight { get; set; }
    public double TakeoffWeightMac { get; set; }
    public double FuelWeight { get; set; }
    public double LandingWeight { get; set; }

    /// <summary>Loaded indices (trim-sheet values), a linear function of the MACs — rounded to
    /// 2 dp for parity with ProSim's emitted lizfw/litow.</summary>
    public double LizfwIndex { get; set; }
    public double LitowIndex { get; set; }

    public int TotalPassengers { get; set; }
    public int[] PassengersByZone { get; set; } = new int[4];
    public double ForwardCargoWeight { get; set; }
    public double AftCargoWeight { get; set; }
}

/// <summary>Live aircraft state feeding a calculation — read from cached dataref
/// subscriptions by the orchestrator so the math stays pure and testable.</summary>
public sealed class WeightAndBalanceLiveState
{
    public int[] ZoneCapacities { get; set; } = new int[4];
    public int[] ZoneAmounts { get; set; } = new int[4];
    public double CargoForwardCapacityKg { get; set; }
    public double CargoAftCapacityKg { get; set; }
    public double CargoBulkCapacityKg { get; set; }
    public double CargoForwardKg { get; set; }
    public double CargoAftKg { get; set; }
    public double FuelCenterKg { get; set; }
    public double FuelLeftKg { get; set; }
    public double FuelRightKg { get; set; }
    public double ZfwKg { get; set; }
    public double GrossWeightKg { get; set; }

    /// <summary>aircraft.cg — gross-weight CG %MAC. Must be plausibility-validated.</summary>
    public double GrossCgMac { get; set; }

    /// <summary>aircraft.zfwcg — ZFW CG %MAC (prelim only). Must be plausibility-validated.</summary>
    public double ZfwCgMac { get; set; }
}
