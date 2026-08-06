using System.Buffers.Binary;
using System.Text;
using ProsimCompanion.Speech.Tts;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

public sealed class WavRepairTests
{
    /// <summary>Builds RIFF/WAVE + "fmt " (16 bytes) + "data" with the given size fields.</summary>
    private static byte[] Wav(uint riffSize, uint dataSize, int dataBytes)
    {
        var payload = new byte[dataBytes];
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(Encoding.ASCII.GetBytes("RIFF"));
        w.Write(riffSize);
        w.Write(Encoding.ASCII.GetBytes("WAVE"));
        w.Write(Encoding.ASCII.GetBytes("fmt "));
        w.Write(16u);
        w.Write(new byte[16]);
        w.Write(Encoding.ASCII.GetBytes("data"));
        w.Write(dataSize);
        w.Write(payload);
        return ms.ToArray();
    }

    private static uint RiffSize(byte[] wav) => BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(4));
    private static uint DataSize(byte[] wav) => BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(40));

    [Theory]
    [InlineData(0u, 0u)]                     // streaming placeholders
    [InlineData(0xFFFFFFFFu, 0xFFFFFFFFu)]   // the other placeholder convention
    public void PlaceholderSizes_AreRewrittenToRealLengths(uint riff, uint data)
    {
        var wav = Wav(riff, data, dataBytes: 100);

        WavRepair.NormalizeSizes(wav);

        Assert.Equal((uint)(wav.Length - 8), RiffSize(wav));
        Assert.Equal(100u, DataSize(wav));
    }

    [Fact]
    public void GoodWav_IsUntouched_Idempotent()
    {
        var wav = Wav(riffSize: 0, dataSize: 0, dataBytes: 64);
        WavRepair.NormalizeSizes(wav);
        var healed = (byte[])wav.Clone();

        WavRepair.NormalizeSizes(wav);

        Assert.Equal(healed, wav);
    }

    [Fact]
    public void OverrunningDataSize_IsClampedToBuffer()
    {
        var wav = Wav(riffSize: 0, dataSize: 1_000_000, dataBytes: 50);

        WavRepair.NormalizeSizes(wav);

        Assert.Equal(50u, DataSize(wav));
    }

    [Fact]
    public void NonWav_IsLeftAlone()
    {
        var bytes = Encoding.ASCII.GetBytes(new string('x', 100));
        var before = (byte[])bytes.Clone();

        WavRepair.NormalizeSizes(bytes);

        Assert.Equal(before, bytes);
    }

    [Fact]
    public void TooShortBuffer_DoesNotThrow()
        => WavRepair.NormalizeSizes([1, 2, 3]);
}
