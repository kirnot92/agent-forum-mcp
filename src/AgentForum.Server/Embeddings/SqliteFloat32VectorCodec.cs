using System.Buffers.Binary;

namespace AgentForum.Server.Embeddings;

public static class SqliteFloat32VectorCodec
{
    private const int BytesPerValue = sizeof(float);

    public static byte[] Encode(ReadOnlySpan<float> vector)
    {
        if (vector.IsEmpty)
        {
            throw new ArgumentException("The vector must contain at least one dimension.", nameof(vector));
        }

        var blob = new byte[checked(vector.Length * BytesPerValue)];

        for (var index = 0; index < vector.Length; index++)
        {
            var value = vector[index];
            if (!float.IsFinite(value))
            {
                throw new ArgumentException(
                    $"The vector contains a non-finite value at index {index}.",
                    nameof(vector));
            }

            BinaryPrimitives.WriteInt32LittleEndian(
                blob.AsSpan(index * BytesPerValue, BytesPerValue),
                BitConverter.SingleToInt32Bits(value));
        }

        return blob;
    }

    public static float[] Decode(ReadOnlySpan<byte> blob, int dimensions)
    {
        if (dimensions <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(dimensions),
                dimensions,
                "The number of dimensions must be positive.");
        }

        if (dimensions > int.MaxValue / BytesPerValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(dimensions),
                dimensions,
                "The number of dimensions is too large to decode.");
        }

        var expectedLength = dimensions * BytesPerValue;
        if (blob.Length != expectedLength)
        {
            throw new InvalidDataException(
                $"The vector blob has {blob.Length} bytes, but {expectedLength} bytes are required for {dimensions} dimensions.");
        }

        var vector = new float[dimensions];

        for (var index = 0; index < dimensions; index++)
        {
            var bits = BinaryPrimitives.ReadInt32LittleEndian(
                blob.Slice(index * BytesPerValue, BytesPerValue));
            var value = BitConverter.Int32BitsToSingle(bits);

            if (!float.IsFinite(value))
            {
                throw new InvalidDataException(
                    $"The vector blob contains a non-finite value at index {index}.");
            }

            vector[index] = value;
        }

        return vector;
    }
}
