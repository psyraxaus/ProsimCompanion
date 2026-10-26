using Microsoft.Extensions.DependencyInjection;
using ProsimCompanion.Core.Configuration;
using Xunit;

namespace ProsimCompanion.Core.Tests.Web;

public sealed partial class SettingsPagesRenderTests
{
    [Theory]
    [InlineData("defaults")]
    [InlineData("configured")]
    public async Task DiagnosticsSettings_Renders_WithoutLeakingTextIds(string fixture)
    {
        var configured = fixture == "configured";
        var services = new ServiceCollection();
        var diagnostics = new DiagnosticsOptions();
        if (configured)
        {
            diagnostics.EcamHostLabels["10.0.1.30"] = "Mac mini";
        }

        Options(services, diagnostics);
        Options(services, new SpeechOptions { AsrBaseUrl = configured ? "http://10.0.1.30:8000" : "", KokoroBaseUrl = configured ? "http://10.0.1.30:8880" : "" });
        Options(services, new BriefingOptions { LlmBaseUrl = configured ? "http://10.0.1.23:11434" : "" });

        var html = await RenderPageAsync<ProsimCompanion.Web.Pages.DiagnosticsSettings>(services, "settings/diagnostics", new Dictionary<string, object?>());

        Assert.Contains("<section", html, StringComparison.Ordinal);
        Assert.DoesNotMatch(LeakedTextId(), html);
        if (configured)
        {
            Assert.Contains("10.0.1.30", html, StringComparison.Ordinal);
            Assert.Contains("10.0.1.23", html, StringComparison.Ordinal);
            Assert.Contains("Mac mini", html, StringComparison.Ordinal);
        }

        Dump($"diagnostics-{fixture}", html);
    }
}
