using AgentForum.Server.Embeddings;

namespace AgentForum.Server.Tests.Embeddings;

public sealed class VectorMathTests
{
    [Fact]
    public void Normalize_ReturnsUnitVectorWithoutChangingDirection()
    {
        var result = VectorMath.Normalize([3f, 4f]);

        Assert.Equal(0.6f, result[0], precision: 6);
        Assert.Equal(0.8f, result[1], precision: 6);
        Assert.Equal(1d, Math.Sqrt(result.Sum(value => value * value)), precision: 6);
    }

    [Fact]
    public void Normalize_DoesNotModifyInput()
    {
        float[] input = [3f, 4f];

        _ = VectorMath.Normalize(input);

        Assert.Equal([3f, 4f], input);
    }

    [Fact]
    public void Normalize_RejectsEmptyZeroAndNonFiniteVectors()
    {
        Assert.Throws<ArgumentException>(() => VectorMath.Normalize([]));
        Assert.Throws<ArgumentException>(() => VectorMath.Normalize([0f, 0f]));
        Assert.Throws<ArgumentException>(() => VectorMath.Normalize([1f, float.NaN]));
        Assert.Throws<ArgumentException>(() => VectorMath.Normalize([1f, float.PositiveInfinity]));
    }

    [Fact]
    public void CosineSimilarity_ComputesExpectedValues()
    {
        Assert.Equal(1d, VectorMath.CosineSimilarity([2f, -1f], [4f, -2f]), precision: 12);
        Assert.Equal(0d, VectorMath.CosineSimilarity([1f, 0f], [0f, 1f]), precision: 12);
        Assert.Equal(-1d, VectorMath.CosineSimilarity([1f, 2f], [-1f, -2f]), precision: 12);
    }

    [Fact]
    public void CosineSimilarity_RejectsMalformedVectors()
    {
        Assert.Throws<ArgumentException>(() => VectorMath.CosineSimilarity([], []));
        Assert.Throws<ArgumentException>(() => VectorMath.CosineSimilarity([1f], [1f, 2f]));
        Assert.Throws<ArgumentException>(() => VectorMath.CosineSimilarity([0f], [1f]));
        Assert.Throws<ArgumentException>(() => VectorMath.CosineSimilarity([1f], [0f]));
        Assert.Throws<ArgumentException>(() => VectorMath.CosineSimilarity([float.NaN], [1f]));
        Assert.Throws<ArgumentException>(() => VectorMath.CosineSimilarity([1f], [float.NegativeInfinity]));
    }
}
