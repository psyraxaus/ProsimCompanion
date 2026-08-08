using System.Net.Http;
using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Core.Updates;

/// <summary>
/// Periodic GitHub-releases version check feeding the web layout's update banner. Degrades
/// silently: no network, API rate limit, or an unparseable tag all log at Debug and leave the
/// store unchanged. Never blocks startup — the first check runs after a short delay on the
/// background loop. The predecessor accidentally checked its upstream fork while linking the
/// maintained one; here both point at the same repository on purpose.
/// </summary>
public sealed class UpdateCheckService : BackgroundService
{
    private const string RepoSlug = "psyraxaus/ProsimCompanion";
    private const string ReleasesApiUrl = $"https://api.github.com/repos/{RepoSlug}/releases/latest";
    private const string ReleasesPageUrl = $"https://github.com/{RepoSlug}/releases/latest";
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(15);

    private readonly UpdateStore _store;
    private readonly IOptionsMonitor<UpdateCheckOptions> _options;
    private readonly ILogger<UpdateCheckService> _logger;
    private readonly HttpClient _http;

    public UpdateCheckService(
        UpdateStore store,
        IOptionsMonitor<UpdateCheckOptions> options,
        ILogger<UpdateCheckService> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _store = store;
        _options = options;
        _logger = logger;
        _http = new HttpClient(new SocketsHttpHandler { UseProxy = false })
        {
            Timeout = TimeSpan.FromSeconds(10),
        };
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github.v3+json");
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("ProsimCompanion");
    }

    /// <summary>The running app's version (assembly informational version, any "+commit"
    /// build-metadata suffix stripped).</summary>
    public static string CurrentVersion
    {
        get
        {
            var informational = typeof(UpdateCheckService).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            var version = informational ?? typeof(UpdateCheckService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
            var metadata = version.IndexOf('+', StringComparison.Ordinal);
            return metadata > 0 ? version[..metadata] : version;
        }
    }

    /// <summary>Pure comparison core (tested without the network): true when
    /// <paramref name="latestTag"/> (an optionally v-prefixed semver) is newer than
    /// <paramref name="currentVersion"/>. Unparseable input is never "newer".</summary>
    public static bool IsNewer(string latestTag, string currentVersion)
    {
        var tag = latestTag.Trim().TrimStart('v', 'V');
        var current = currentVersion.Trim();
        var currentMeta = current.IndexOf('-', StringComparison.Ordinal);
        if (currentMeta > 0)
        {
            current = current[..currentMeta]; // 0.2.0-beta compares as 0.2.0
        }

        return Version.TryParse(tag, out var latest)
            && Version.TryParse(current, out var running)
            && latest > running;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken).ConfigureAwait(false);

            while (!stoppingToken.IsCancellationRequested)
            {
                if (_options.CurrentValue.Enabled)
                {
                    await CheckOnceAsync(stoppingToken).ConfigureAwait(false);
                }

                var interval = TimeSpan.FromHours(Math.Clamp(_options.CurrentValue.IntervalHours, 1, 24 * 30));
                await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Host shutdown.
        }
    }

    private async Task CheckOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.GetAsync(ReleasesApiUrl, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // 404 = no release published yet; anything else is rate limit / outage.
                _logger.LogDebug("Update check: GitHub returned {Status}", (int)response.StatusCode);
                return;
            }

            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var tag = (JsonNode.Parse(text) as JsonObject)?["tag_name"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(tag))
            {
                _logger.LogDebug("Update check: release response carried no tag_name");
                return;
            }

            var current = CurrentVersion;
            var available = IsNewer(tag, current);
            _store.Set(new UpdateSnapshot(
                available,
                current,
                tag.Trim().TrimStart('v', 'V'),
                ReleasesPageUrl,
                Dismissed: false));
            _logger.LogInformation(
                "Update check: running {Current}, latest release {Latest} — update available: {Available}",
                current,
                tag,
                available);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            _logger.LogDebug(ex, "Update check failed (offline or API unavailable)");
        }
    }

    public override void Dispose()
    {
        _http.Dispose();
        base.Dispose();
    }
}
