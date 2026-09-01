using AgentForum.Server.Persistence;

namespace AgentForum.Server.Services;

internal static class EmbeddingModelCompatibility
{
    internal static async Task EnsureCompatibleAsync(
        IForumRepository repository,
        string configuredModelId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        if (string.IsNullOrWhiteSpace(configuredModelId))
        {
            throw new ArgumentException(
                "A non-empty embedding model ID is required.",
                nameof(configuredModelId));
        }

        var storedModelIds = await repository
            .ReadDistinctEmbeddingModelIdsAsync(cancellationToken)
            .ConfigureAwait(false);
        if (storedModelIds.Count == 0 ||
            storedModelIds.All(modelId =>
                string.Equals(modelId, configuredModelId, StringComparison.Ordinal)))
        {
            return;
        }

        var formattedStoredIds = string.Join(
            ", ",
            storedModelIds
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .Select(modelId => $"'{modelId}'"));

        throw new InvalidOperationException(
            $"Existing embeddings use a different model (stored model IDs: {formattedStoredIds}); " +
            $"the configured model ID is '{configuredModelId}'. Use the original embedding model, " +
            "or explicitly rebuild/reindex the embeddings offline before starting the server.");
    }
}
