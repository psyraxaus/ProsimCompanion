using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Hosting;

namespace ProsimCompanion.Speech.Llm;

/// <summary>Wakes the LLM host at startup (fire-and-forget; readiness is confirmed by
/// actually reaching the endpoint, never by the send). Its own startup module now — this
/// used to hide inside the retired speech bootstrap (campaign #87).</summary>
public sealed class LlmWakeOnLanStartup : IStartupModule
{
    private readonly IOptionsMonitor<BriefingOptions> _options;
    private readonly ILogger<LlmWakeOnLanStartup> _logger;

    public LlmWakeOnLanStartup(
        IOptionsMonitor<BriefingOptions> options,
        ILogger<LlmWakeOnLanStartup> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _logger = logger;
    }

    public void Start()
    {
        var wol = _options.CurrentValue.LlmWakeOnLan;
        if (wol.Enabled)
        {
            WakeOnLan.Send(wol.MacAddress, wol.BroadcastAddress, wol.Port, _logger);
        }
    }
}
