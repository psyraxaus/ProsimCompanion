using System.Text.Json.Nodes;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Diagnostics;
using Xunit;

namespace ProsimCompanion.Core.Tests.Diagnostics;

/// <summary>
/// The redacted settings copy that goes into a support bundle. The fixture carries every
/// secret-bearing option the codebase has today (prosim.apiKey, sayIntentions.manualApiKey,
/// webUi.accessToken, briefing.llmApiKey — the SecretProtector.SecretPaths list) plus the
/// look-alike values the value rule must catch regardless of key.
/// </summary>
public sealed class SettingsRedactorTests
{
    private const string Fixture = """
        {
          "configVersion": 2,
          "webUi": { "port": 5320, "bindToAllInterfaces": true, "accessToken": "0123456789ABCDEF0123456789ABCDEF" },
          "prosim": { "host": "192.168.1.20", "apiKey": "plain-prosim-key-value" },
          "sayIntentions": { "apiKeySource": "manual", "manualApiKey": "dpapi:AQAAANCMnd8BFdERjHoAwE/Cl+sBAAAA" },
          "briefing": { "llmEnabled": true, "llmApiKey": "sk-proj-abcdefghijklmnopqrstuvwxyz0123456789", "llmModel": "gpt-4o" },
          "telemetryApi": { "enabled": true, "requireTokenOnLoopback": false },
          "gsx": { "operatorPreferences": ["Swissport", "dnata"], "pushbackPreference": "auto" },
          "custom": {
            "bearerHeader": "Bearer eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.abc",
            "jwtLikeValue": "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.SflKxwRJSMeKKF2QT4fwpMeJf36POk6yJV_adQssw5c",
            "hexLikeValue": "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08",
            "googleLikeValue": "AIzaSyD-EXAMPLE1234567890abcdefGHIJKLMNO",
            "nothingSpecial": "Left seat, engines off",
            "aPath": "C:\\Users\\pilot\\AppData\\Local\\ProsimCompanion\\sessions\\session-20260926-120000.jsonl",
            "aUrl": "http://127.0.0.1:8744/",
            "emptyToken": ""
          }
        }
        """;

    [Fact]
    public void Redact_MasksEveryRegisteredSecretPath()
    {
        var root = JsonNode.Parse(SettingsRedactor.Redact(Fixture))!;

        foreach (var path in SecretProtector.SecretPaths)
        {
            var (section, key) = (path.Split(':')[0], path.Split(':')[1]);
            Assert.Equal(SettingsRedactor.Mask, (string?)root[section]![key]);
        }
    }

    [Fact]
    public void Redact_MasksLookAlikeValuesRegardlessOfKey_AndKeepsOrdinaryStrings()
    {
        var root = JsonNode.Parse(SettingsRedactor.Redact(Fixture))!;
        var custom = root["custom"]!;

        Assert.Equal(SettingsRedactor.Mask, (string?)custom["bearerHeader"]);
        Assert.Equal(SettingsRedactor.Mask, (string?)custom["jwtLikeValue"]);
        Assert.Equal(SettingsRedactor.Mask, (string?)custom["hexLikeValue"]);
        Assert.Equal(SettingsRedactor.Mask, (string?)custom["googleLikeValue"]);
        Assert.Equal("Left seat, engines off", (string?)custom["nothingSpecial"]);
        Assert.EndsWith("session-20260926-120000.jsonl", (string?)custom["aPath"], StringComparison.Ordinal);
        Assert.Equal("http://127.0.0.1:8744/", (string?)custom["aUrl"]);
        Assert.Equal("", (string?)custom["emptyToken"]); // an empty secret says "not configured"
    }

    [Fact]
    public void Redact_KeepsStructureAndNonStringValues()
    {
        var root = JsonNode.Parse(SettingsRedactor.Redact(Fixture))!;

        Assert.Equal(2, (int?)root["configVersion"]);
        Assert.Equal(5320, (int?)root["webUi"]!["port"]);
        Assert.True((bool?)root["briefing"]!["llmEnabled"]);
        // A boolean named "...Token..." is a feature switch, not a secret — support needs it.
        Assert.False((bool?)root["telemetryApi"]!["requireTokenOnLoopback"]);
        Assert.Equal("gpt-4o", (string?)root["briefing"]!["llmModel"]);
        Assert.Equal(2, root["gsx"]!["operatorPreferences"]!.AsArray().Count);
        Assert.Equal("Swissport", (string?)root["gsx"]!["operatorPreferences"]![0]);
    }

    [Fact]
    public void Redact_NeverLeaksTheOriginalSecretText()
    {
        var redacted = SettingsRedactor.Redact(Fixture);

        Assert.DoesNotContain("0123456789ABCDEF0123456789ABCDEF", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("plain-prosim-key-value", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("AQAAANCMnd8B", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-proj-", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Redact_UnparseableInput_WithholdsContent()
    {
        var redacted = SettingsRedactor.Redact("{ \"apiKey\": \"leaky\" ");

        Assert.DoesNotContain("leaky", redacted, StringComparison.Ordinal);
        Assert.Contains("did not parse", redacted, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("token", true)]
    [InlineData("accessToken", true)]
    [InlineData("llmApiKey", true)]
    [InlineData("api_key", true)]
    [InlineData("clientSecret", true)]
    [InlineData("Password", true)]
    [InlineData("credentials", true)]
    [InlineData("bearer", true)]
    [InlineData("host", false)]
    [InlineData("port", false)]
    public void IsSecretName_MatchesTheFragmentList(string name, bool expected)
        => Assert.Equal(expected, SettingsRedactor.IsSecretName(name));
}
