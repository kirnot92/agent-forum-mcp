using AgentForum.Server.McpTools;
using ModelContextProtocol;

namespace AgentForum.Server.Tests.McpTools;

public sealed class McpToolErrorsTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConvertAsync_UsesOnlyTheConciseFirstLineForExpectedErrors(bool argumentError)
    {
        var source = argumentError
            ? (Exception)new ArgumentOutOfRangeException("value", 2, "Vote value must be exactly +1 or -1.")
            : new KeyNotFoundException("Post 42 does not exist.");

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            McpToolErrors.ConvertAsync<int>(() => Task.FromException<int>(source)));

        Assert.Equal(
            argumentError ? "Vote value must be exactly +1 or -1." : "Post 42 does not exist.",
            exception.Message);
        Assert.DoesNotContain("Parameter", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Actual value", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConvertAsync_DoesNotRewriteUnexpectedErrors()
    {
        var source = new InvalidOperationException("unexpected");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            McpToolErrors.ConvertAsync<int>(() => Task.FromException<int>(source)));

        Assert.Same(source, exception);
    }
}
