namespace ProsimCompanion.Core.Configuration;

/// <summary>
/// A top-level options section of config/settings.json. The static section name is what ties a
/// class to its camelCase JSON section at compile time — the registry, the defaults writer and
/// the expression-based settings writes all key off it, so a section can never drift between
/// the binding, the file and the web UI (campaign #84).
/// </summary>
public interface IOptionSection
{
    /// <summary>The camelCase settings.json section this class binds ("gsx", "webUi", …).</summary>
    static abstract string SectionName { get; }
}
