using ProsimCompanion.Core.Aircraft;
using Xunit;

namespace ProsimCompanion.Core.Tests.Aircraft;

public sealed class DataRefCoercionTests
{
    [Fact]
    public void Coerce_Null_ReturnsFallback()
    {
        Assert.Equal(42.5, DataRefCoercion.Coerce<double>(null, 42.5));
        Assert.Equal("fallback", DataRefCoercion.Coerce<string>(null, "fallback"));
    }

    [Fact]
    public void Coerce_ExactType_PassesThrough()
    {
        Assert.Equal(7, DataRefCoercion.Coerce(7, 0));
        Assert.True(DataRefCoercion.Coerce(true, fallback: false));
    }

    [Theory]
    [InlineData(1024, 1024.0)]
    [InlineData(3.7f, 3.7)]
    public void Coerce_NumericWidening_Converts(object raw, double expected)
        => Assert.Equal(expected, DataRefCoercion.Coerce(raw, 0.0), precision: 5);

    [Fact]
    public void Coerce_DoubleToInt_Converts()
        => Assert.Equal(5, DataRefCoercion.Coerce(5.0, 0));

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2.0, true)]
    [InlineData("true", true)]
    [InlineData("0", false)]
    [InlineData("1", true)]
    public void Coerce_ToBool_FollowsNonZeroConvention(object raw, bool expected)
        => Assert.Equal(expected, DataRefCoercion.Coerce(raw, fallback: !expected));

    [Fact]
    public void Coerce_UnparseableString_ReturnsFallback()
    {
        Assert.Equal(9, DataRefCoercion.Coerce("not a number", 9));
        Assert.False(DataRefCoercion.Coerce("not a bool", fallback: false));
    }

    [Fact]
    public void Coerce_ToString_UsesInvariantCulture()
        => Assert.Equal("3.5", DataRefCoercion.Coerce(3.5, ""));
}
