using ProsimCompanion.Gsx.Transport;
using Xunit;

namespace ProsimCompanion.Core.Tests.Gsx;

public sealed class CouatlPortLocatorTests
{
    [Fact]
    public void ParsePort_TypicalIni_ReadsPort()
    {
        const string ini = """
            [couatl]
            some_key = 1

            [gsx]
            remote_server_port = 9100
            """;

        Assert.Equal(9100, CouatlPortLocator.ParsePort(ini));
    }

    [Fact]
    public void ParsePort_KeyOutsideGsxSection_IsIgnored()
    {
        const string ini = """
            [couatl]
            remote_server_port = 9999
            """;

        Assert.Equal(CouatlPortLocator.DefaultPort, CouatlPortLocator.ParsePort(ini));
    }

    [Fact]
    public void ParsePort_SectionAndKeyAreCaseInsensitive()
    {
        const string ini = """
            [GSX]
            Remote_Server_Port = 8800
            """;

        Assert.Equal(8800, CouatlPortLocator.ParsePort(ini));
    }

    [Fact]
    public void ParsePort_InlineCommentAndWhitespace_AreStripped()
    {
        const string ini = """
            [gsx]
            remote_server_port =  8801  ; the port
            """;

        Assert.Equal(8801, CouatlPortLocator.ParsePort(ini));
    }

    [Fact]
    public void ParsePort_DuplicateKeys_LastWins()
    {
        const string ini = """
            [gsx]
            remote_server_port = 8801
            remote_server_port = 8802
            """;

        Assert.Equal(8802, CouatlPortLocator.ParsePort(ini));
    }

    [Theory]
    [InlineData("")]
    [InlineData("[gsx]\nremote_server_port = notanumber")]
    [InlineData("[gsx]\nremote_server_port = 0")]
    [InlineData("[gsx]\nremote_server_port = 70000")]
    [InlineData("[gsx]\n; remote_server_port = 9000")]
    public void ParsePort_InvalidInputs_FallBackToDefault(string ini)
        => Assert.Equal(CouatlPortLocator.DefaultPort, CouatlPortLocator.ParsePort(ini));
}
