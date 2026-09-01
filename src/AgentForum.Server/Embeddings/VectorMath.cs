namespace AgentForum.Server.Embeddings;

public static class VectorMath
{
    public static float[] Normalize(ReadOnlySpan<float> vector)
    {
        EnsureNotEmpty(vector, nameof(vector));

        var sumOfSquares = SumOfSquares(vector, nameof(vector));
        if (sumOfSquares == 0d)
        {
            throw new ArgumentException("The vector must have a non-zero magnitude.", nameof(vector));
        }

        var magnitude = Math.Sqrt(sumOfSquares);
        var normalized = new float[vector.Length];

        for (var index = 0; index < vector.Length; index++)
        {
            normalized[index] = (float)(vector[index] / magnitude);
        }

        return normalized;
    }

    public static double CosineSimilarity(
        ReadOnlySpan<float> left,
        ReadOnlySpan<float> right)
    {
        EnsureNotEmpty(left, nameof(left));

        if (left.Length != right.Length)
        {
            throw new ArgumentException(
                "Vectors must have the same number of dimensions.",
                nameof(right));
        }

        double dotProduct = 0d;
        double leftSumOfSquares = 0d;
        double rightSumOfSquares = 0d;

        for (var index = 0; index < left.Length; index++)
        {
            var leftValue = left[index];
            var rightValue = right[index];

            if (!float.IsFinite(leftValue))
            {
                throw new ArgumentException(
                    $"The vector contains a non-finite value at index {index}.",
                    nameof(left));
            }

            if (!float.IsFinite(rightValue))
            {
                throw new ArgumentException(
                    $"The vector contains a non-finite value at index {index}.",
                    nameof(right));
            }

            dotProduct += (double)leftValue * rightValue;
            leftSumOfSquares += (double)leftValue * leftValue;
            rightSumOfSquares += (double)rightValue * rightValue;
        }

        if (leftSumOfSquares == 0d)
        {
            throw new ArgumentException("The vector must have a non-zero magnitude.", nameof(left));
        }

        if (rightSumOfSquares == 0d)
        {
            throw new ArgumentException("The vector must have a non-zero magnitude.", nameof(right));
        }

        var similarity = dotProduct / Math.Sqrt(leftSumOfSquares * rightSumOfSquares);

        // Floating-point rounding can put mathematically valid cosine values just
        // outside their defined range.
        return Math.Clamp(similarity, -1d, 1d);
    }

    private static double SumOfSquares(ReadOnlySpan<float> vector, string parameterName)
    {
        double sumOfSquares = 0d;

        for (var index = 0; index < vector.Length; index++)
        {
            var value = vector[index];
            if (!float.IsFinite(value))
            {
                throw new ArgumentException(
                    $"The vector contains a non-finite value at index {index}.",
                    parameterName);
            }

            sumOfSquares += (double)value * value;
        }

        return sumOfSquares;
    }

    private static void EnsureNotEmpty(ReadOnlySpan<float> vector, string parameterName)
    {
        if (vector.IsEmpty)
        {
            throw new ArgumentException("The vector must contain at least one dimension.", parameterName);
        }
    }
}
