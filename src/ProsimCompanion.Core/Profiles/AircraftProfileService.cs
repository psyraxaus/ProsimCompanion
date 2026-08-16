using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Aircraft.Ofp;

namespace ProsimCompanion.Core.Profiles;

/// <summary>
/// Tracks the active aircraft profile. Automatic matching follows Prosim2GSX's priority —
/// exact title, title substring, airline (OFP callsign ICAO prefix), Default fallback —
/// re-evaluated whenever the sim title, the OFP, or the profile list changes. A manual
/// override (the Profiles page's "Set Active") wins over matching until cleared or the
/// overridden profile disappears.
/// </summary>
public sealed class AircraftProfileService : IDisposable
{
    private readonly IOptionsMonitor<AircraftProfilesOptions> _options;
    private readonly OfpStore _ofp;
    private readonly ILogger<AircraftProfileService> _logger;
    private readonly IDataRefSubscription<string?> _title;
    private readonly IDisposable? _optionsSubscription;
    private string? _manualProfileId;

    public AircraftProfileService(
        IProsimDataRefs dataRefs,
        IOptionsMonitor<AircraftProfilesOptions> options,
        OfpStore ofp,
        ILogger<AircraftProfileService> logger)
    {
        ArgumentNullException.ThrowIfNull(dataRefs);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(ofp);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _ofp = ofp;
        _logger = logger;

        _title = dataRefs.Subscribe(ProsimDataRefNames.AircraftTitle);
        _title.ValueChanged += (_, _) => Rematch();
        _ofp.Changed += OnOfpChanged;
        _optionsSubscription = _options.OnChange(_ => Rematch());
    }

    /// <summary>Current aircraft title as reported by the simulator, or null before data arrives.</summary>
    public string? AircraftTitle => _title.Value;

    /// <summary>ICAO airline for the Airline match type: the OFP callsign's leading letters
    /// (e.g. "BAW123" → "BAW"), or null without a plan.</summary>
    public string? AirlineIcao
    {
        get
        {
            var callsign = _ofp.Current?.Callsign;
            if (string.IsNullOrWhiteSpace(callsign))
            {
                return null;
            }
            var letters = new string(callsign.TakeWhile(char.IsLetter).ToArray());
            return letters.Length == 0 ? null : letters.ToUpperInvariant();
        }
    }

    /// <summary>The active profile (manual override, else best automatic match, else the
    /// Default profile), or null when nothing applies (features fall back to defaults).</summary>
    public AircraftProfile? ActiveProfile { get; private set; }

    /// <summary>Non-null while a manual "Set Active" override is in force.</summary>
    public string? ManualProfileId => _manualProfileId;

    /// <summary>Raised when the active profile changes (including to null), on arbitrary threads.</summary>
    public event EventHandler? Changed;

    /// <summary>Pins a profile manually (Profiles page "Set Active"); null returns to
    /// automatic matching.</summary>
    public void SetManualActive(string? profileId)
    {
        _manualProfileId = profileId;
        Rematch();
    }

    public void Dispose()
    {
        _optionsSubscription?.Dispose();
        _ofp.Changed -= OnOfpChanged;
        _title.Dispose();
    }

    private void OnOfpChanged(object? sender, EventArgs e) => Rematch();

    private void Rematch()
    {
        var profiles = _options.CurrentValue.Profiles;

        var manual = _manualProfileId is null
            ? null
            : profiles.FirstOrDefault(profile => profile.Id == _manualProfileId);
        if (_manualProfileId is not null && manual is null)
        {
            _manualProfileId = null; // the pinned profile was deleted — fall back to matching
        }

        var matched = manual ?? AircraftProfileMatcher.Match(profiles, AircraftTitle, AirlineIcao);
        if (ReferenceEquals(matched, ActiveProfile) || matched?.Id == ActiveProfile?.Id)
        {
            return;
        }

        ActiveProfile = matched;
        _logger.LogInformation(
            "Active aircraft profile: {Profile} (title {Title}, airline {Airline}, manual {Manual})",
            matched?.Name ?? "<none>",
            AircraftTitle ?? "<unknown>",
            AirlineIcao ?? "<unknown>",
            manual is not null);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
