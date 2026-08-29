using ProsimCompanion.Gsx;
using Xunit;

namespace ProsimCompanion.Core.Tests.Gsx;

public sealed class GsxReconnectBackoffTests
{
    [Theory]
    [InlineData(0, 5000)]
    [InlineData(1, 5000)]
    [InlineData(2, 10000)]
    [InlineData(3, 20000)]
    [InlineData(4, 40000)]
    [InlineData(5, 60000)]
    [InlineData(27, 60000)]
    public void Doubles_FromTheBase_UpToTheCeiling(long failures, int expectedMs)
        => Assert.Equal(TimeSpan.FromMilliseconds(expectedMs), GsxReconnectBackoff.Delay(5000, 60000, failures));

    [Fact]
    public void CeilingBelowBase_UsesTheBase()
        => Assert.Equal(TimeSpan.FromMilliseconds(7000), GsxReconnectBackoff.Delay(7000, 1000, 9));

    [Fact]
    public void TinyBase_IsFlooredAtHalfASecond()
        => Assert.Equal(TimeSpan.FromMilliseconds(500), GsxReconnectBackoff.Delay(10, 60000, 1));
}
