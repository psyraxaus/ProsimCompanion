using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.Configuration;

namespace ProsimCompanion.Core.Profiles;

/// <summary>
/// Applies the active profile's stored GSX settings block to the live <c>gsx</c> section when
/// the profile changes (the Prosim2GSX per-profile model, realised through the existing
/// settings file so every IOptionsMonitor&lt;GsxOptions&gt; consumer reloads untouched).
/// Profiles without a stored block leave the live section alone — they capture one the first
/// time the GSX Settings page saves while they are active. <c>fuelFobSaved</c> is
/// app-maintained state, not configuration, and always survives a profile switch.
/// </summary>
public sealed class ProfileGsxApplier : IHostedService
{
    private static readonly JsonSerializerOptions SerializeOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly AircraftProfileService _profiles;
    private readonly JsonSettingsFile _settings;
    private readonly ILogger<ProfileGsxApplier> _logger;

    public ProfileGsxApplier(
        AircraftProfileService profiles,
        JsonSettingsFile settings,
        ILogger<ProfileGsxApplier> logger)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);
        _profiles = profiles;
        _settings = settings;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _profiles.Changed += OnProfileChanged;
        Apply(); // a profile may already be active at startup
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _profiles.Changed -= OnProfileChanged;
        return Task.CompletedTask;
    }

    private void OnProfileChanged(object? sender, EventArgs e) => Apply();

    private void Apply()
    {
        var profile = _profiles.ActiveProfile;
        if (profile?.GsxSettings is null)
        {
            return;
        }

        try
        {
            var block = JsonSerializer.SerializeToNode(profile.GsxSettings, SerializeOptions)!.AsObject();

            _settings.Update(root =>
            {
                var current = root[GsxOptions.SectionName] as JsonObject;

                // FOB persistence is app state — carry the live values across the switch.
                if (current?["fuelFobSaved"] is { } fob)
                {
                    block["fuelFobSaved"] = fob.DeepClone();
                }

                // Idempotence guard: profile Changed can fire without the block differing
                // (e.g. manual pin of the already-matched profile) — skip the no-op write so
                // options reloads don't cascade.
                if (current is not null && JsonNode.DeepEquals(current, block))
                {
                    return;
                }

                root[GsxOptions.SectionName] = block;
            });

            _logger.LogInformation("Applied GSX settings block of profile {Profile}", profile.Name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning(ex, "Applying profile {Profile} GSX settings failed", profile.Name);
        }
    }
}
