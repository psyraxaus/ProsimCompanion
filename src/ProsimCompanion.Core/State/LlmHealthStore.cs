namespace ProsimCompanion.Core.State;

/// <summary>Health of the single app-wide LLM endpoint (briefing/debrief/persona styling).</summary>
public enum LlmHealthState
{
    /// <summary>Not yet exercised this session (or not configured).</summary>
    Unknown,

    /// <summary>Last call succeeded.</summary>
    Healthy,

    /// <summary>The endpoint rejected the credentials (HTTP 401/403) — a config problem the
    /// user must fix; retrying without a key change will not recover.</summary>
    AuthFailed,

    /// <summary>Transport-level failure (refused, DNS, timeout) or a non-auth HTTP error —
    /// may self-heal when the host comes up, so the periodic re-probe keeps watching.</summary>
    Unreachable,
}

/// <summary>Point-in-time LLM health. <paramref name="LastError"/> is a short summary safe
/// for display and logs — it must NEVER contain the API key.</summary>
public sealed record LlmHealthSnapshot(
    LlmHealthState State,
    string? LastError,
    DateTimeOffset? LastCheckedUtc)
{
    public static LlmHealthSnapshot Empty { get; } = new(LlmHealthState.Unknown, null, null);

    /// <summary>True when the web UI should show the offline warning — the two states where
    /// free-form understanding and LLM styling are known to be dead (issue #66: a 401 ran a
    /// whole flight with zero surfacing).</summary>
    public bool IsUnhealthy => State is LlmHealthState.AuthFailed or LlmHealthState.Unreachable;
}

/// <summary>
/// Observable store of LLM endpoint health, written by the LLM client on every call outcome
/// and by the periodic re-probe. Kept in Core so the Web project (which references only Core)
/// can render the warning banner (issue #66). <see cref="Changed"/> fires on the writer's
/// thread — consumers marshal to their own context.
/// </summary>
public sealed class LlmHealthStore : SnapshotStore<LlmHealthSnapshot>
{
    public LlmHealthStore()
        : base(LlmHealthSnapshot.Empty, StateAndErrorComparer.Instance)
    {
    }

    /// <summary>Records a call outcome. <paramref name="errorSummary"/> must never contain
    /// credentials of any kind. Observers are notified only when the state actually changes
    /// (the store's comparer ignores <see cref="LlmHealthSnapshot.LastCheckedUtc"/>, which
    /// refreshes on every report) so per-call Healthy reports don't churn the UI.</summary>
    public void Report(LlmHealthState state, string? errorSummary = null)
        => Update(_ => new LlmHealthSnapshot(state, errorSummary, DateTimeOffset.UtcNow));

    private sealed class StateAndErrorComparer : IEqualityComparer<LlmHealthSnapshot>
    {
        public static StateAndErrorComparer Instance { get; } = new();

        public bool Equals(LlmHealthSnapshot? x, LlmHealthSnapshot? y)
            => ReferenceEquals(x, y)
                || (x is not null && y is not null && x.State == y.State && x.LastError == y.LastError);

        public int GetHashCode(LlmHealthSnapshot obj) => HashCode.Combine(obj.State, obj.LastError);
    }
}
