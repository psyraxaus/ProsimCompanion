using Microsoft.Extensions.DependencyInjection;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Checklists;
using ProsimCompanion.Core.Commands.Handlers;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Core.Commands;

/// <summary>
/// Single entry point for registering every command handler bundle — called once from the
/// composition root after the container is built. Adding a bundle means adding one line here,
/// so the composition root never grows per-feature command knowledge.
/// <para>
/// Seams are resolved with <c>GetService</c> (not <c>GetRequiredService</c>) on purpose:
/// every pillar is optional (degrade-not-fail), and a missing seam must not fail startup.
/// The bundles still register their commands so the API shape is identical regardless of which
/// pillars are running — an absent seam simply answers <see cref="CommandOutcome.Unavailable"/>.
/// </para>
/// </summary>
public static class CommandsBootstrap
{
    public static void RegisterAll(CommandRegistry registry, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(services);

        GsxCommandHandlers.Register(
            registry,
            services.GetService<IGsxDepartureControl>(),
            services.GetService<IGsxGateControl>());
        ChecklistCommandHandlers.Register(registry, services.GetService<ChecklistService>());
        LoadsheetCommandHandlers.Register(
            registry,
            services.GetService<ILoadsheetControl>(),
            services.GetService<IFmsInitSync>());
        OfpCommandHandlers.Register(registry, services.GetService<ISimbriefImporter>());
        MinimaCommandHandlers.Register(registry, services.GetService<ArrivalMinimaStore>());
        SpeechCommandHandlers.Register(registry, services.GetService<ISpeechControl>());
    }
}
