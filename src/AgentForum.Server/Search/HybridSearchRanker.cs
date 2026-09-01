namespace AgentForum.Server.Search;

public static class HybridSearchRanker
{
    public const int CandidateLimit = 50;
    public const int ReciprocalRankConstant = 60;

    private const double VerificationAdjustmentWeight = 0.000006d;
    private const double VoteAdjustmentWeight = 0.000002d;
    private const double ActivityAdjustmentWeight = 0.000002d;

    public static IReadOnlyList<RankedPost> Fuse(
        IEnumerable<long> lexicalRanking,
        IEnumerable<long> vectorRanking,
        IReadOnlyDictionary<long, RankingSignals> signals,
        DateTimeOffset referenceTime,
        int limit)
    {
        ArgumentNullException.ThrowIfNull(lexicalRanking);
        ArgumentNullException.ThrowIfNull(vectorRanking);
        ArgumentNullException.ThrowIfNull(signals);

        var scores = new Dictionary<long, double>();
        AddRanking(scores, lexicalRanking);
        AddRanking(scores, vectorRanking);

        return scores
            .Select(pair => new RankedPost(
                pair.Key,
                pair.Value + CalculateSmallAdjustment(
                    signals.TryGetValue(pair.Key, out var value) ? value : RankingSignals.Empty,
                    referenceTime)))
            .OrderByDescending(result => result.Score)
            .ThenBy(result => result.PostId)
            .Take(Math.Clamp(limit, 1, CandidateLimit))
            .ToArray();
    }

    private static void AddRanking(Dictionary<long, double> scores, IEnumerable<long> ranking)
    {
        var seen = new HashSet<long>();
        var rank = 0;

        foreach (var postId in ranking)
        {
            if (rank >= CandidateLimit)
            {
                break;
            }

            if (postId <= 0 || !seen.Add(postId))
            {
                continue;
            }

            rank++;
            scores.TryGetValue(postId, out var score);
            scores[postId] = score + (1d / (ReciprocalRankConstant + rank));
        }
    }

    private static double CalculateSmallAdjustment(RankingSignals signals, DateTimeOffset referenceTime)
    {
        var verificationBalance =
            (signals.WorkedAsWrittenCount * 2L) +
            signals.WorkedWithChangesCount -
            (signals.DidNotWorkCount * 2L);
        var verification = NormalizeCapped(verificationBalance, 20) * VerificationAdjustmentWeight;

        var voteBalance = (long)signals.Upvotes - signals.Downvotes;
        var votes = NormalizeCapped(voteBalance, 20) * VoteAdjustmentWeight;

        var age = referenceTime - signals.LastActivityAt;
        var ageDays = Math.Max(0d, age.TotalDays);
        var activity = Math.Exp(-ageDays / 365d) * ActivityAdjustmentWeight;

        return verification + votes + activity;
    }

    private static double NormalizeCapped(long value, long cap) =>
        Math.Clamp(value, -cap, cap) / (double)cap;
}

public sealed record RankedPost(long PostId, double Score);

public sealed record RankingSignals(
    int Upvotes,
    int Downvotes,
    int WorkedAsWrittenCount,
    int WorkedWithChangesCount,
    int DidNotWorkCount,
    DateTimeOffset LastActivityAt)
{
    public static RankingSignals Empty { get; } = new(0, 0, 0, 0, 0, DateTimeOffset.MinValue);
}
