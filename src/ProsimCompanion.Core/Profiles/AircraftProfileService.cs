using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Aircraft;

namespace ProsimCompanion.Core.Profiles;

/// <summary>
/// Tracks the active aircraft profile: watches the aircraft title dataref and the configured
/// profile list (hot-reloaded) and re-matches when either changes. <see cref="Changed"/> fires
/// on arbitrary threads — consumers marshal themselves.
/// </summary>
public sealed class AircraftProfileService : IDisposable
{
    private readonly IOptionsMonitor<AircraftProfilesOptions> _options;
    private readonly ILogger<AircraftProfileService> _logger;
    private readonly IDataRefSubscription _title;
    private readonly IDisposable? _optionsSubscription;

    public AircraftProfileService(
        IProsimDataRefs dataRefs,
        IOptionsMonitor<AircraftProfilesOptions> options,
        ILogger<AircraftProfileService> logger)
    {
        ArgumentNullException.ThrowIfNull(dataRefs);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _logger = logger;

        _title = dataRefs.Subscribe(ProsimDataRefNames.AircraftTitle, DataRefTier.Infrequent);
        _title.ValueChanged += (_, _) => Rematch();
        _optionsSubscription = _options.OnChange(_ => Rematch());
    }

    /// <summary>Current aircraft title as reported by the simulator, or null before data arrives.</summary>
    public string? AircraftTitle => _title.GetValue<string?>(null);

    /// <summary>The matched profile, or null when no profile matches (features fall back to defaults).</summary>
    public AircraftProfile? ActiveProfile { get; private set; }

    /// <summary>Raised when the active profile changes (including to null), on arbitrary threads.</summary>
    public event EventHandler? Changed;

    public void Dispose()
    {
        _optionsSubscription?.Dispose();
        _title.Dispose();
    }

    private void Rematch()
    {
        var matched = AircraftProfileMatcher.Match(_options.CurrentValue.Profiles, AircraftTitle);
        if (ReferenceEquals(matched, ActiveProfile) || matched?.Id == ActiveProfile?.Id)
        {
            return;
        }

        ActiveProfile = matched;
        _logger.LogInformation(
            "Active aircraft profile: {Profile} (title {Title})",
            matched?.Name ?? "<none>",
            AircraftTitle ?? "<unknown>");
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
