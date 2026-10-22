using System.Text.Json.Nodes;
using ProsimCompanion.Core.Configuration;
using Xunit;

namespace ProsimCompanion.Core.Tests.Configuration;

/// <summary>#178: every agent's token is a registered secret, next to the legacy flat one.</summary>
public sealed class MixerSecretPathTests
{
    [Fact]
    public void SecretPaths_NameTheLegacyToken_AndEveryAgentToken()
    {
        Assert.Contains("mixer:token", SecretProtector.SecretPaths);
        Assert.Contains("mixer:agents:*:token", SecretProtector.SecretPaths);
    }

    [Fact]
    public void ProtectKnownSecrets_WrapsEveryAgentToken_AndLeavesHostsPlain()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = (JsonObject)JsonNode.Parse("""
            {
              "mixer": {
                "enabled": true,
                "token": "legacy-plain",
                "agents": [
                  { "name": "PC1", "host": "pc1", "token": "plain-1" },
                  { "name": "PC2", "host": "pc2", "token": "" },
                  { "name": "PC3", "host": "pc3", "token": "dpapi:untouched" }
                ]
              }
            }
            """)!;

        Assert.True(SecretProtector.ProtectKnownSecrets(root));

        var agents = (JsonArray)root["mixer"]!["agents"]!;
        Assert.True(SecretProtector.IsProtected((string?)root["mixer"]!["token"]));
        Assert.True(SecretProtector.IsProtected((string?)agents[0]!["token"]));
        Assert.True(SecretProtector.TryUnprotect((string?)agents[0]!["token"], out var plain));
        Assert.Equal("plain-1", plain);
        Assert.Equal("", (string?)agents[1]!["token"]);
        Assert.Equal("dpapi:untouched", (string?)agents[2]!["token"]);
        Assert.Equal("pc1", (string?)agents[0]!["host"]);
    }

    [Fact]
    public void ExpandSecretKeys_ListsEachLoadedAgentToken()
    {
        var loaded = new[] { "mixer:enabled", "mixer:agents:0:host", "mixer:agents:0:token", "mixer:agents:1:token", "mixer:agents:1:name" };

        var expanded = SecretProtector.ExpandSecretKeys(loaded).ToList();

        Assert.Contains("mixer:agents:0:token", expanded);
        Assert.Contains("mixer:agents:1:token", expanded);
        Assert.DoesNotContain("mixer:agents:0:host", expanded);
        Assert.Contains("mixer:token", expanded); // the flat path returns itself
    }
}
