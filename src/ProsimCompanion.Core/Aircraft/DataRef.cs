namespace ProsimCompanion.Core.Aircraft;

/// <summary>
/// A typed ProSim dataref descriptor: wire name, subscription cadence, and the benign
/// fallback served until the first push arrives or when the pushed value cannot be coerced.
/// Declared once per ref in <see cref="ProsimDataRefNames"/> so the wire string, runtime
/// type, tier and fallback policy live in exactly one place (#83) — call sites can no
/// longer disagree about what a ref means.
/// </summary>
/// <typeparam name="T">The CLR type consumers read this ref as.</typeparam>
/// <param name="Name">Wire-protocol dataref path, consumed verbatim by the ProSim SDK.</param>
/// <param name="Tier">Subscription cadence. Shared subscriptions register at the fastest
/// tier any consumer requested, so declaring the fastest known consumer need here is safe
/// for everyone.</param>
/// <param name="Fallback">The value served while no data has arrived. This is policy, not
/// filler: pick the value that keeps downstream logic inert when the ref is dead (see the
/// non-zero fallbacks in the catalog for deliberate examples).</param>
public readonly record struct DataRef<T>(string Name, DataRefTier Tier, T Fallback);

/// <summary>
/// A typed SimConnect descriptor (SimVar or "L:" LVAR) — the <see cref="DataRef{T}"/>
/// counterpart for <see cref="ISimVars"/>. Carries the unit string because SimConnect
/// fixes a name's unit at first registration.
/// </summary>
/// <typeparam name="T">The CLR type consumers read this variable as.</typeparam>
/// <param name="Name">SimVar name ("CAMERA STATE") or LVAR ("L:FSDT_GSX_JETWAY").</param>
/// <param name="Unit">SimConnect unit string ("number", "Bool", "Enum", ...).</param>
/// <param name="Tier">Subscription cadence; maps to a SimConnect request period.</param>
/// <param name="Fallback">Value served while the sim has pushed nothing. LVAR caution: the
/// sim auto-creates unknown names as 0, so a non-zero fallback is the only way to detect
/// "GSX not present" — choose it deliberately (e.g. Jetway falls back to 2 = no jetway).</param>
public readonly record struct SimVarRef<T>(string Name, string Unit, DataRefTier Tier, T Fallback);

/// <summary>
/// A typed view over one subscribed dataref. Adds descriptor-driven reads to the untyped
/// handle; <see cref="IDataRefSubscription.RawValue"/> and
/// <see cref="IDataRefSubscription.IsStale"/> remain available because several consumers
/// use null-vs-fallback as a liveness probe — the typed layer must never hide them.
/// </summary>
public interface IDataRefSubscription<T> : IDataRefSubscription
{
    /// <summary>Cached value coerced to <typeparamref name="T"/>, or the descriptor's
    /// fallback when nothing has arrived or coercion fails.</summary>
    T Value { get; }

    /// <summary>
    /// Reads with a call-site fallback instead of the descriptor's. Reserved for sentinel
    /// probes that must distinguish "no data yet" from a real value (e.g. −1 not-yet-read);
    /// everyday reads use <see cref="Value"/> so the benign default stays in the catalog.
    /// </summary>
    T GetValueOr(T fallback);
}
