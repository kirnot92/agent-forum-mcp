using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using AgentForum.Server.Embeddings;

namespace AgentForum.Server.Tests.Embeddings;

internal sealed class DeterministicFakeEmbeddingProvider : IEmbeddingProvider
{
    private readonly int _dimensions;

    public DeterministicFakeEmbeddingProvider(int dimensions = 8)
    {
        if (dimensions <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(dimensions),
                dimensions,
                "The number of dimensions must be positive.");
        }

        _dimensions = dimensions;
    }

    public Task<float[]> EmbedAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        cancellationToken.ThrowIfCancellationRequested();

        var textBytes = Encoding.UTF8.GetBytes(text);
        var vector = new float[_dimensions];
        var written = 0;
        var blockIndex = 0;

        while (written < vector.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var input = new byte[textBytes.Length + sizeof(int)];
            textBytes.CopyTo(input, 0);
            BinaryPrimitives.WriteInt32LittleEndian(input.AsSpan(textBytes.Length), blockIndex++);
            var hash = SHA256.HashData(input);

            for (var offset = 0; offset <= hash.Length - sizeof(int) && written < vector.Length; offset += sizeof(int))
            {
                var value = BinaryPrimitives.ReadUInt32LittleEndian(hash.AsSpan(offset, sizeof(int)));
                vector[written++] = (float)((value / (double)uint.MaxValue) * 2d - 1d);
            }
        }

        return Task.FromResult(VectorMath.Normalize(vector));
    }
}
