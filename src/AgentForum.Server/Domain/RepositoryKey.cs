namespace AgentForum.Server.Domain;

public static class RepositoryKey
{
    public static string Normalize(string repo)
    {
        if (string.IsNullOrWhiteSpace(repo))
        {
            throw new ArgumentException("A non-empty repository key is required.", nameof(repo));
        }

        var candidate = TrimSuffixes(repo.Trim());
        if (candidate.Length == 0)
        {
            throw new ArgumentException("A non-empty repository key is required.", nameof(repo));
        }

        if (LooksLikeLocalPath(candidate))
        {
            throw new ArgumentException(
                "Repository key must not be a local path; use owner/repo for GitHub repositories.",
                nameof(repo));
        }

        if (TryNormalizeGitHubRemote(candidate, out var normalized))
        {
            return normalized;
        }

        if (LooksLikeRemote(candidate))
        {
            throw new ArgumentException(
                "Only GitHub repository URLs are supported; use the canonical owner/repo key.",
                nameof(repo));
        }

        if (TryNormalizeOwnerRepo(candidate, out normalized))
        {
            return normalized;
        }

        if (candidate.Contains('/') || candidate.Contains('\\'))
        {
            throw new ArgumentException(
                "Repository key must be owner/repo for GitHub repositories, not a local path or URL.",
                nameof(repo));
        }

        return candidate;
    }

    private static bool TryNormalizeGitHubRemote(string candidate, out string normalized)
    {
        const string scpPrefix = "git@github.com:";
        if (candidate.StartsWith(scpPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return TryNormalizeOwnerRepo(candidate[scpPrefix.Length..], out normalized);
        }

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ||
            !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
            !uri.IsDefaultPort ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            normalized = string.Empty;
            return false;
        }

        var supportedScheme = uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
                string.IsNullOrEmpty(uri.UserInfo) ||
            uri.Scheme.Equals(Uri.UriSchemeSsh, StringComparison.OrdinalIgnoreCase) &&
                uri.UserInfo.Equals("git", StringComparison.Ordinal);
        if (!supportedScheme)
        {
            normalized = string.Empty;
            return false;
        }

        return TryNormalizeOwnerRepo(uri.AbsolutePath, out normalized);
    }

    private static bool TryNormalizeOwnerRepo(string candidate, out string normalized)
    {
        var key = TrimSuffixes(candidate.Trim().TrimStart('/'));
        var parts = key.Split('/', StringSplitOptions.None);
        if (parts.Length == 2 && parts.All(IsGitHubName))
        {
            normalized = $"{parts[0].ToLowerInvariant()}/{parts[1].ToLowerInvariant()}";
            return true;
        }

        normalized = string.Empty;
        return false;
    }

    private static bool IsGitHubName(string value) =>
        value.Length > 0 &&
        value is not "." and not ".." &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

    private static string TrimSuffixes(string value)
    {
        var result = value.TrimEnd('/');
        if (result.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            result = result[..^4].TrimEnd('/');
        }

        return result;
    }

    private static bool LooksLikeLocalPath(string candidate) =>
        candidate.StartsWith('\\') ||
        candidate.StartsWith('/') ||
        candidate.StartsWith("./", StringComparison.Ordinal) ||
        candidate.StartsWith("../", StringComparison.Ordinal) ||
        candidate.StartsWith("~/", StringComparison.Ordinal) ||
        candidate.Length >= 2 && char.IsAsciiLetter(candidate[0]) && candidate[1] == ':';

    private static bool LooksLikeRemote(string candidate) =>
        candidate.Contains("://", StringComparison.Ordinal) ||
        candidate.Contains('@') && candidate.Contains(':');
}
