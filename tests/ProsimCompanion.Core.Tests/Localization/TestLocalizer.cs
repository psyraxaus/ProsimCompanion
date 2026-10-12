using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Localization;
using ProsimCompanion.Web.Localization;

namespace ProsimCompanion.Core.Tests.Localization;

/// <summary>
/// Registers the real localizer stack over the shipped packs for a page render test
/// (ADR-0014): every page now injects <see cref="IStringLocalizer"/>, so a test's service
/// collection needs the catalog, the language store, the browser context and
/// <see cref="UiText"/>. English by default — a render test asserts English text.
/// </summary>
public static class TestLocalizer
{
    /// <summary>Adds the localizer stack rendering in <paramref name="language"/>; any
    /// <c>IOptionsMonitor&lt;WebUiOptions&gt;</c> or <c>SettingsWriter</c> the test registered
    /// before this call is kept.</summary>
    public static IServiceCollection AddTestLocalizer(this IServiceCollection services, string language = "en")
    {
        ArgumentNullException.ThrowIfNull(services);

        if (!services.Any(d => d.ServiceType == typeof(IOptionsMonitor<WebUiOptions>)))
        {
            var options = new Mock<IOptionsMonitor<WebUiOptions>>();
            options.SetupGet(o => o.CurrentValue).Returns(new WebUiOptions { Language = language });
            options.Setup(o => o.OnChange(It.IsAny<Action<WebUiOptions, string?>>())).Returns((IDisposable?)null);
            services.AddSingleton(options.Object);
        }

        if (!services.Any(d => d.ServiceType == typeof(SettingsWriter)))
        {
            var settingsFile = Path.Combine(Path.GetTempPath(), "ProsimCompanionTests", Guid.NewGuid().ToString("N"), "settings.json");
            Directory.CreateDirectory(Path.GetDirectoryName(settingsFile)!);
            services.AddSingleton(new SettingsWriter(new JsonSettingsFile(settingsFile)));
        }

        services.AddSingleton(new LanguagePackCatalog([ShippedPackRoot()], NullLogger<LanguagePackCatalog>.Instance));
        services.AddSingleton<UiLanguageStore>();
        services.AddScoped(_ => new BrowserLanguageContext());
        services.AddScoped<IStringLocalizer, UiText>();
        return services;
    }

    /// <summary>The shipped pack root, <c>src/ProsimCompanion.App/config/lang</c>, found above the test assembly.</summary>
    public static string ShippedPackRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ProsimCompanion.slnx")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(
            directory?.FullName ?? throw new InvalidOperationException("Repo root not found above the test assembly."),
            "src", "ProsimCompanion.App", "config", "lang");
    }
}
