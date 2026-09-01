using AgentForum.Server.Search;

namespace AgentForum.Server.Tests.Search;

public sealed class HybridSearchRankerTests
{
    private static readonly DateTimeOffset ReferenceTime = DateTimeOffset.Parse("2026-09-01T00:00:00Z");

    [Fact]
    public void Fuse_UsesOneBasedReciprocalRanksAndRewardsOverlap()
    {
        var result = HybridSearchRanker.Fuse(
            [2, 1],
            [1, 3],
            EmptySignals(),
            ReferenceTime,
            limit: 10);

        Assert.Equal([1L, 2L, 3L], result.Select(item => item.PostId));
        Assert.Equal(
            (1d / 62d) + (1d / 61d),
            result[0].Score,
            precision: 12);
    }

    [Fact]
    public void Fuse_IsDeterministicAndUsesOrdinalPostIdForExactTies()
    {
        var first = HybridSearchRanker.Fuse(
            [2],
            [1],
            EmptySignals(),
            ReferenceTime,
            limit: 10);
        var second = HybridSearchRanker.Fuse(
            [2],
            [1],
            EmptySignals(),
            ReferenceTime,
            limit: 10);

        Assert.Equal(first, second);
        Assert.Equal([1L, 2L], first.Select(item => item.PostId));
    }

    [Fact]
    public void Fuse_CountsEachSourceOnlyOncePerPost()
    {
        var duplicated = HybridSearchRanker.Fuse(
            [1, 1, 1],
            [],
            EmptySignals(),
            ReferenceTime,
            limit: 10);

        Assert.Single(duplicated);
        Assert.Equal(1d / 61d, duplicated[0].Score, precision: 12);
    }

    [Fact]
    public void Fuse_KeepsRelevanceAheadOfMaximumPopularityHints()
    {
        var signals = new Dictionary<long, RankingSignals>
        {
            [1] = new(0, 100, 0, 0, 100, DateTimeOffset.MinValue),
            [2] = new(100, 0, 100, 100, 0, ReferenceTime),
        };

        var result = HybridSearchRanker.Fuse(
            [1, 2],
            [],
            signals,
            ReferenceTime,
            limit: 10);

        Assert.Equal([1L, 2L], result.Select(item => item.PostId));
    }

    [Fact]
    public void Fuse_WeightsVerificationMoreThanVotesForTiedRelevance()
    {
        var signals = new Dictionary<long, RankingSignals>
        {
            [1] = new(0, 0, 1, 0, 0, ReferenceTime),
            [2] = new(1, 0, 0, 0, 0, ReferenceTime),
        };

        var result = HybridSearchRanker.Fuse(
            [1],
            [2],
            signals,
            ReferenceTime,
            limit: 10);

        Assert.Equal([1L, 2L], result.Select(item => item.PostId));
    }

    [Fact]
    public void Fuse_HandlesEmptyRankingsAndClampsLimit()
    {
        Assert.Empty(HybridSearchRanker.Fuse([], [], EmptySignals(), ReferenceTime, limit: 10));

        var result = HybridSearchRanker.Fuse(
            Enumerable.Range(1, 100).Select(index => (long)index),
            [],
            EmptySignals(),
            ReferenceTime,
            limit: 1000);

        Assert.Equal(HybridSearchRanker.CandidateLimit, result.Count);
    }

    private static IReadOnlyDictionary<long, RankingSignals> EmptySignals() =>
        new Dictionary<long, RankingSignals>();
}
