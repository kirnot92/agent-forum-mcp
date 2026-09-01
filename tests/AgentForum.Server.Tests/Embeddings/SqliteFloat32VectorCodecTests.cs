using System.Buffers.Binary;
using AgentForum.Server.Embeddings;

namespace AgentForum.Server.Tests.Embeddings;

public sealed class SqliteFloat32VectorCodecTests
{
    [Fact]
    public void Encode_UsesHeaderlessLittleEndianFloat32Representation()
    {
        var blob = SqliteFloat32VectorCodec.Encode([1f, -2.5f]);

        Assert.Equal(
            new byte[] { 0x00, 0x00, 0x80, 0x3f, 0x00, 0x00, 0x20, 0xc0 },
            blob);
    }

    [Fact]
    public void EncodeAndDecode_RoundTripEveryFiniteBitPattern()
    {
        float[] original = [float.MinValue, -0f, float.Epsilon, 123.25f, float.MaxValue];

        var blob = SqliteFloat32VectorCodec.Encode(original);
        var decoded = SqliteFloat32VectorCodec.Decode(blob, original.Length);

        Assert.Equal(
            original.Select(BitConverter.SingleToInt32Bits),
            decoded.Select(BitConverter.SingleToInt32Bits));
    }

    [Fact]
    public void Encode_RejectsEmptyAndNonFiniteVectors()
    {
        Assert.Throws<ArgumentException>(() => SqliteFloat32VectorCodec.Encode([]));
        Assert.Throws<ArgumentException>(() => SqliteFloat32VectorCodec.Encode([float.NaN]));
        Assert.Throws<ArgumentException>(() => SqliteFloat32VectorCodec.Encode([float.PositiveInfinity]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public void Decode_RejectsInvalidDimensions(int dimensions)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SqliteFloat32VectorCodec.Decode([], dimensions));
    }

    [Fact]
    public void Decode_RejectsBlobLengthThatDoesNotMatchDimensions()
    {
        var exception = Assert.Throws<InvalidDataException>(
            () => SqliteFloat32VectorCodec.Decode(new byte[7], dimensions: 2));

        Assert.Contains("7 bytes", exception.Message, StringComparison.Ordinal);
        Assert.Contains("8 bytes", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Decode_RejectsNonFiniteValuesInBlob()
    {
        var blob = new byte[sizeof(float)];
        BinaryPrimitives.WriteInt32LittleEndian(blob, BitConverter.SingleToInt32Bits(float.NaN));

        Assert.Throws<InvalidDataException>(
            () => SqliteFloat32VectorCodec.Decode(blob, dimensions: 1));
    }
}
