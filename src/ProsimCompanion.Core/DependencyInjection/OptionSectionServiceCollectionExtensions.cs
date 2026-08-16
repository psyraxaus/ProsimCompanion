using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ProsimCompanion.Core.Configuration;

namespace ProsimCompanion.Core.DependencyInjection;

/// <summary>
/// The single way an option section joins the application (campaign #84): one call performs the
/// configuration binding (with the binder list-append fix every section needs), and records the
/// section in the <see cref="OptionSectionRegistry"/> that drives the settings-defaults writer —
/// so binding a section and self-documenting it in settings.json can never drift apart.
/// </summary>
public static class OptionSectionServiceCollectionExtensions
{
    public static IServiceCollection AddOptionSection<TOptions>(
        this IServiceCollection services,
        IConfiguration configuration)
        where TOptions : class, IOptionSection, new()
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        GetOrAddRegistry(services).Add(new OptionSectionDescriptor(
            TOptions.SectionName,
            typeof(TOptions),
            static () => new TOptions()));

        // Registration order matters: the clear must be configured before the section bind.
        services.Configure<TOptions>(static o => OptionsListBinding.ClearLists(o));
        services.Configure<TOptions>(configuration.GetSection(TOptions.SectionName));
        services.PostConfigure<TOptions>(static o => OptionsListBinding.RestoreEmptyLists(o, new TOptions()));
        return services;
    }

    private static OptionSectionRegistry GetOrAddRegistry(IServiceCollection services)
    {
        var existing = services
            .FirstOrDefault(d => d.ServiceType == typeof(OptionSectionRegistry))
            ?.ImplementationInstance as OptionSectionRegistry;
        if (existing is not null)
        {
            return existing;
        }

        var registry = new OptionSectionRegistry();
        services.AddSingleton(registry);
        return registry;
    }
}
