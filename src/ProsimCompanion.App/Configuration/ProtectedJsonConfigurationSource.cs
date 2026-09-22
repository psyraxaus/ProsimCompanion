using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;

namespace ProsimCompanion.App.Configuration;

/// <summary>
/// The settings.json configuration source: a stock JSON source whose provider decrypts the
/// DPAPI-protected secrets (<see cref="Core.Configuration.SecretProtector"/>) as the file is
/// loaded, so <c>IOptionsMonitor&lt;T&gt;</c> consumers bind plain values and never learn that
/// the file holds ciphertext. Lives in the composition root, not Core, because the provider
/// reports through the static Serilog logger — DI is not up while configuration loads.
/// </summary>
public sealed class ProtectedJsonConfigurationSource : JsonConfigurationSource
{
    /// <inheritdoc />
    public override IConfigurationProvider Build(IConfigurationBuilder builder)
    {
        // What AddJsonFile(path) does before adding the source: an absolute Path becomes a
        // PhysicalFileProvider rooted at its directory. Without it EnsureDefaults falls back
        // to the builder's content-root provider and an absolute path is never found.
        ResolveFileProvider();
        EnsureDefaults(builder);
        return new ProtectedJsonConfigurationProvider(this);
    }
}
