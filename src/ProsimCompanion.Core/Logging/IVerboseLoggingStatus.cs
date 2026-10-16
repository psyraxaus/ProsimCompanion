namespace ProsimCompanion.Core.Logging;

/// <summary>What the Logs page shows about the verbose-logging clock (2026-10-17): when the
/// current verbose setting started and when the app will switch it off. Implemented by the
/// App's watchdog; both are null while nothing verbose is on.</summary>
public interface IVerboseLoggingStatus
{
    /// <summary>When a Debug/Verbose level or the wire trace was last switched on, UTC.</summary>
    DateTimeOffset? VerboseSinceUtc { get; }

    /// <summary>When the watchdog returns to Information, UTC; null when the pilot keeps it
    /// (<c>logging.keepVerbose</c>) or nothing verbose is on.</summary>
    DateTimeOffset? RevertAtUtc { get; }
}
