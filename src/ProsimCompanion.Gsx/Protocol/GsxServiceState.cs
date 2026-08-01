namespace ProsimCompanion.Gsx.Protocol;

/// <summary>The service-state model consumers key on (carried from the predecessor).</summary>
public enum GsxServiceState
{
    Unknown = 0,
    NotAvailable,
    Callable,
    Bypassed,
    Requested,
    Active,
    Completed,
}

/// <summary>
/// Maps the mirror's semantic state string to the model. Locked decision 5: control flow keys
/// ONLY on this semantic string — never on <c>stateRaw</c> or free-text fields.
/// </summary>
public static class GsxServiceStateMapper
{
    public static GsxServiceState Map(string? semanticState) => semanticState?.ToLowerInvariant() switch
    {
        "available" => GsxServiceState.Callable,
        "unavailable" => GsxServiceState.NotAvailable,
        "bypassed" => GsxServiceState.Bypassed,
        "requested" => GsxServiceState.Requested,
        "performing" => GsxServiceState.Active,
        // Transient tail of an active service; treated as Active (agrees with both performing
        // and completed in the predecessor's discrepancy probe).
        "completing" => GsxServiceState.Active,
        "completed" => GsxServiceState.Completed,
        _ => GsxServiceState.Unknown,
    };
}
