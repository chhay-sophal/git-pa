namespace GitPa.Tests;

public class PullRequestUrlTests
{
    [Theory]
    [InlineData("https://github.com/acme/plugins.git",
        "https://github.com/acme/plugins/compare/main...feat/login?expand=1")]
    [InlineData("git@github.com:acme/plugins.git",
        "https://github.com/acme/plugins/compare/main...feat/login?expand=1")]
    [InlineData("https://bitbucket.org/acme/plugins.git",
        "https://bitbucket.org/acme/plugins/pull-requests/new?source=feat%2Flogin&dest=main")]
    [InlineData("git@bitbucket.org:acme/plugins.git",
        "https://bitbucket.org/acme/plugins/pull-requests/new?source=feat%2Flogin&dest=main")]
    [InlineData("https://acme@dev.azure.com/acme/Power%20Apps/_git/plugins",
        "https://dev.azure.com/acme/Power%20Apps/_git/plugins/pullrequestcreate?sourceRef=feat%2Flogin&targetRef=main")]
    [InlineData("git@ssh.dev.azure.com:v3/acme/PowerApps/plugins",
        "https://dev.azure.com/acme/PowerApps/_git/plugins/pullrequestcreate?sourceRef=feat%2Flogin&targetRef=main")]
    [InlineData("https://acme.visualstudio.com/PowerApps/_git/plugins",
        "https://acme.visualstudio.com/PowerApps/_git/plugins/pullrequestcreate?sourceRef=feat%2Flogin&targetRef=main")]
    [InlineData("https://bitbucket.acme.com/scm/PA/plugins.git",
        "https://bitbucket.acme.com/projects/PA/repos/plugins/pull-requests?create&sourceBranch=refs%2Fheads%2Ffeat%2Flogin&targetBranch=refs%2Fheads%2Fmain")]
    [InlineData("ssh://git@bitbucket.acme.com:7999/pa/plugins.git",
        "https://bitbucket.acme.com/projects/pa/repos/plugins/pull-requests?create&sourceBranch=refs%2Fheads%2Ffeat%2Flogin&targetBranch=refs%2Fheads%2Fmain")]
    public void BuildsCreatePageUrl(string remote, string expected)
    {
        Assert.Equal(expected, PullRequestUrl.Build(remote, "feat/login", "main"));
    }

    [Fact]
    public void ResolvesSshHostAliases()
    {
        var url = PullRequestUrl.Build("git@github-work:acme/plugins.git", "feat/login", "main",
            alias => alias == "github-work" ? "github.com" : null);

        Assert.Equal("https://github.com/acme/plugins/compare/main...feat/login?expand=1", url);
    }

    [Fact]
    public void ReturnsNullForUnknownHosts()
    {
        Assert.Null(PullRequestUrl.Build("https://git.example.com/acme/plugins.git", "feat/login", "main"));
    }

    [Fact]
    public void FillsTemplate()
    {
        Assert.Equal("https://example.com/pr?from=feat%2Flogin&to=main",
            PullRequestUrl.FromTemplate("https://example.com/pr?from={branch}&to={target}", "feat/login", "main"));
    }
}
