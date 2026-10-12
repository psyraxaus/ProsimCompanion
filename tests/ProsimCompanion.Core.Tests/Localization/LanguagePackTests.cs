using System.Text.Json;
using ProsimCompanion.Core.Localization;
using Xunit;

namespace ProsimCompanion.Core.Tests.Localization;

/// <summary>The pack file format (ADR-0014): nested objects flatten to dotted ids, a dotted
/// member name is allowed beside its siblings, the meta member names the language, and a
/// non-string leaf is refused with the id in the message.</summary>
public sealed class LanguagePackTests
{
    [Fact]
    public void Parse_FlattensNestedObjects_AndKeepsDottedMemberNames()
    {
        var pack = LanguagePack.Parse("de", """
            {
              "$language": { "name": "Deutsch" },
              "layout": {
                "nav": { "fuel": "Kraftstoff" },
                "header": { "nextService": "Nächster Dienst", "nextService.complete": "abgeschlossen" }
              },
              "top": "oben"
            }
            """);

        Assert.Equal("de", pack.Code);
        Assert.Equal("Deutsch", pack.NativeName);
        Assert.Equal("Kraftstoff", pack.Strings["layout.nav.fuel"]);
        Assert.Equal("Nächster Dienst", pack.Strings["layout.header.nextService"]);
        Assert.Equal("abgeschlossen", pack.Strings["layout.header.nextService.complete"]);
        Assert.Equal("oben", pack.Strings["top"]);
        Assert.False(pack.Strings.ContainsKey("$language"));
    }

    [Fact]
    public void Parse_WithoutMeta_UsesTheCodeAsName_AndLowerCasesTheCode()
    {
        var pack = LanguagePack.Parse("pt-BR", """{ "a": "b" }""");

        Assert.Equal("pt-br", pack.Code);
        Assert.Equal("pt-BR", pack.NativeName);
    }

    [Fact]
    public void Parse_NonStringLeaf_ThrowsNamingTheId()
    {
        var ex = Assert.Throws<JsonException>(() => LanguagePack.Parse("en", """{ "layout": { "count": 3 } }"""));

        Assert.Contains("layout.count", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_NotAnObject_Throws()
    {
        Assert.Throws<JsonException>(() => LanguagePack.Parse("en", "[1, 2]"));
    }

    [Fact]
    public void Parse_AllowsCommentsAndTrailingCommas_ForHandEditedFiles()
    {
        var pack = LanguagePack.Parse("en", """
            {
              // a translator's note
              "a": "b",
            }
            """);

        Assert.Equal("b", pack.Strings["a"]);
    }
}
