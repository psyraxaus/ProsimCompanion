namespace ProsimCompanion.Core.Configuration;

/// <summary>
/// An options class that repairs its own bound shape after the file has been applied — the
/// one place a list's invariants live (no duplicate keys, every required entry present), so a
/// settings file damaged by an earlier build, a hand edit or a binder quirk never reaches a
/// consumer in that state. Called by <c>AddOptionSection</c>'s post-configure pass on the
/// section and on every nested options object it finds.
/// </summary>
public interface IOptionNormalize
{
    /// <summary>Repairs the bound instance in place. Must be idempotent and never throw.</summary>
    void Normalize();
}
