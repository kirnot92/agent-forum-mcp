using AgentForum.Server.Domain;

namespace AgentForum.Server.Tests.Domain;

public sealed class RepositoryKeyTests
{
    [Theory]
    [InlineData("Owner/Repo", "owner/repo")]
    [InlineData("  Owner/Repo/  ", "owner/repo")]
    [InlineData("Owner/Repo.git", "owner/repo")]
    [InlineData("https://github.com/Owner/Repo", "owner/repo")]
    [InlineData("https://GITHUB.COM/Owner/Repo.git/", "owner/repo")]
    [InlineData("git@github.com:Owner/Repo.git", "owner/repo")]
    [InlineData("ssh://git@github.com/Owner/Repo.git/", "owner/repo")]
    public void Normalize_MapsEquivalentGitHubFormsToOwnerRepo(string input, string expected)
    {
        Assert.Equal(expected, RepositoryKey.Normalize(input));
    }

    [Theory]
    [InlineData(" Repo-A ", "Repo-A")]
    [InlineData("Repo-A/", "Repo-A")]
    [InlineData("Repo-A.git", "Repo-A")]
    public void Normalize_PreservesOpaqueLegacyKeyCasing(string input, string expected)
    {
        Assert.Equal(expected, RepositoryKey.Normalize(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/")]
    [InlineData("C:\\work\\repo")]
    [InlineData("C:repo")]
    [InlineData("D:/work/repo")]
    [InlineData("\\\\server\\share\\repo")]
    [InlineData("./repo")]
    [InlineData("../repo")]
    [InlineData("~/repo")]
    [InlineData("https://gitlab.com/Owner/Repo")]
    [InlineData("http://github.com/Owner/Repo")]
    [InlineData("git@gitlab.com:Owner/Repo")]
    [InlineData("https://github.com/Owner/Repo/extra")]
    [InlineData("owner/repo/extra")]
    public void Normalize_RejectsBlankLocalAndUnsupportedRemoteForms(string input)
    {
        Assert.Throws<ArgumentException>(() => RepositoryKey.Normalize(input));
    }

    [Theory]
    [InlineData("Owner/Repo")]
    [InlineData("https://github.com/Owner/Repo.git")]
    [InlineData(" Repo-A.git ")]
    public void Normalize_IsIdempotent(string input)
    {
        var once = RepositoryKey.Normalize(input);

        Assert.Equal(once, RepositoryKey.Normalize(once));
    }
}
