using ProsimCompanion.Core.Theming;
using Xunit;

namespace ProsimCompanion.Core.Tests.Theming;

public sealed class AirlineLogoStoreTests
{
    [Theory]
    [InlineData("KLM", "klm")]
    [InlineData(" baw ", "baw")]
    [InlineData("A3", "a3")]
    [InlineData("QFA1", "qfa1")]
    public void Slug_NormalisesPlausibleCodes(string code, string expected)
        => Assert.Equal(expected, AirlineLogoStore.Slug(code));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("K")]
    [InlineData("TOOLONG")]
    [InlineData("../x")]
    [InlineData("K L")]
    public void Slug_RejectsAnythingElse(string? code)
        => Assert.Null(AirlineLogoStore.Slug(code));

    [Fact]
    public async Task SaveListFindRemove_RoundTrip()
    {
        var dir = Path.Combine(Path.GetTempPath(), "prosimcompanion-tests-airline-logos-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new AirlineLogoStore(dir);
            Assert.Null(store.FindFile("KLM"));
            Assert.Empty(store.Files.ListSlugs());

            await using (var content = new MemoryStream(new byte[] { 1, 2, 3 }))
            {
                await store.Files.SaveAsync("KLM", "logo.png", content, CancellationToken.None);
            }

            Assert.NotNull(store.FindFile("klm"));
            Assert.Equal(["klm"], store.Files.ListSlugs());
            Assert.Null(store.FindFile("BAW"));

            Assert.True(store.Files.Remove("KLM"));
            Assert.Null(store.FindFile("KLM"));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
