namespace GitPa.Tests;

public class CommitOperationTests
{
    static readonly string Readme = string.Join('\n', Enumerable.Range(1, 10).Select(i => $"line {i}")) + "\n";

    static TestRepo RepoWithFeature()
    {
        var repo = new TestRepo();
        NewBranchOperation.Run(repo.Git, repo.Config, "feat/a", repo.Reporter);
        return repo;
    }

    static string Commit(TestRepo repo, bool push = true, string? description = null) =>
        CommitOperation.Run(repo.Git, repo.Config, new CommitRequest("feat/a", "Change line 1", description, push), repo.Reporter);

    [Fact]
    public void CommitsStagedChangesToTarget_MergesIntoDev_AndPushes()
    {
        using var repo = RepoWithFeature();
        TestRepo.Write(repo.Git, "README.md", Readme.Replace("line 1\n", "line one\n"));
        repo.Git.Run("add", "README.md");

        var sha = Commit(repo, description: "Longer explanation.");

        Assert.Equal(sha, repo.Git.Run("rev-parse", "--short", "feat/a"));
        Assert.Equal("Change line 1", repo.Git.Run("log", "-1", "--format=%s", "feat/a"));
        Assert.Equal("Longer explanation.", repo.Git.Run("log", "-1", "--format=%b", "feat/a"));
        Assert.StartsWith("line one\n", repo.Git.Run("show", "feat/a:README.md") + "\n");
        Assert.Equal("dev", repo.Git.CurrentBranch());
        Assert.True(repo.Git.Succeeds("merge-base", "--is-ancestor", "feat/a", "dev"));
        Assert.Empty(repo.Git.Run("status", "--porcelain"));
        Assert.Equal(repo.Git.Run("rev-parse", "feat/a"), repo.Git.Run("rev-parse", "origin/feat/a"));
        Assert.Equal(repo.Git.Run("rev-parse", "dev"), repo.Git.Run("rev-parse", "origin/dev"));
        Assert.Empty(repo.Git.Run("stash", "list"));
        Assert.Single(repo.Git.Run("worktree", "list").Split('\n'));
    }

    [Fact]
    public void KeepsUnstagedChanges_IncludingInTheSameFile()
    {
        using var repo = RepoWithFeature();
        TestRepo.Write(repo.Git, "README.md", Readme.Replace("line 1\n", "line one\n"));
        repo.Git.Run("add", "README.md");
        TestRepo.Write(repo.Git, "README.md", Readme.Replace("line 1\n", "line one\n").Replace("line 10\n", "line ten\n"));

        Commit(repo);

        Assert.DoesNotContain("line ten", repo.Git.Run("show", "feat/a:README.md"));
        Assert.Contains("line ten", TestRepo.Read(repo.Git, "README.md"));
        Assert.Contains("line one", TestRepo.Read(repo.Git, "README.md"));
        Assert.Equal("M README.md", repo.Git.Run("status", "--porcelain"));
        Assert.Empty(repo.Git.Run("stash", "list"));
    }

    [Fact]
    public void FailsWithoutChanges_WhenStagedChangesDependOnDevOnlyCode()
    {
        using var repo = RepoWithFeature();
        TestRepo.Write(repo.Git, "shared.txt", "from another feature\n");
        TestRepo.Commit(repo.Git, "another feature, only on dev");
        TestRepo.Write(repo.Git, "shared.txt", "from another feature, edited\n");
        repo.Git.Run("add", "shared.txt");
        var featureBefore = repo.Git.Run("rev-parse", "feat/a");
        var devBefore = repo.Git.Run("rev-parse", "dev");

        var error = Assert.Throws<GitPaException>(() => Commit(repo));

        Assert.Contains("don't apply cleanly", error.Message);
        Assert.Equal(featureBefore, repo.Git.Run("rev-parse", "feat/a"));
        Assert.Equal(devBefore, repo.Git.Run("rev-parse", "dev"));
        Assert.Equal("shared.txt", repo.Git.Run("diff", "--cached", "--name-only"));
        Assert.Single(repo.Git.Run("worktree", "list").Split('\n'));
    }

    [Fact]
    public void WorksWithWindowsLineEndingConversion()
    {
        using var repo = RepoWithFeature();
        repo.Git.Run("config", "core.autocrlf", "true");
        TestRepo.Write(repo.Git, "README.md", Readme.Replace("line 1\n", "line one\n").Replace("\n", "\r\n"));
        repo.Git.Run("add", "README.md");

        Commit(repo);

        Assert.StartsWith("line one\nline 2\n", repo.Git.Run("show", "feat/a:README.md") + "\n");
        Assert.Empty(repo.Git.Run("status", "--porcelain"));
    }

    [Fact]
    public void DoesNotPush_WhenPushIsOff()
    {
        using var repo = RepoWithFeature();
        TestRepo.Write(repo.Git, "README.md", "changed\n");
        repo.Git.Run("add", "README.md");

        Commit(repo, push: false);

        Assert.Empty(repo.Git.Run("ls-remote", "--heads", "origin", "feat/a"));
    }

    [Fact]
    public void CommitsOnTopOfTeammatesPushedWork()
    {
        using var repo = RepoWithFeature();
        repo.Git.Run("push", "--quiet", "origin", "feat/a");
        var teammate = repo.Clone("teammate");
        teammate.Run("switch", "--quiet", "feat/a");
        TestRepo.Write(teammate, "teammate.txt", "teammate's work\n");
        TestRepo.Commit(teammate, "teammate change");
        teammate.Run("push", "--quiet", "origin", "feat/a");
        TestRepo.Write(repo.Git, "README.md", "changed\n");
        repo.Git.Run("add", "README.md");

        Commit(repo);

        Assert.Equal(teammate.Run("rev-parse", "HEAD"), repo.Git.Run("rev-parse", "feat/a~1"));
        Assert.Empty(repo.Reporter.Warnings);
    }

    [Fact]
    public void RejectsWhenNotOnDev()
    {
        using var repo = RepoWithFeature();
        repo.Git.Run("switch", "--quiet", "main");

        Assert.Throws<GitPaException>(() => Commit(repo));
    }

    [Fact]
    public void RejectsWhenNothingIsStaged()
    {
        using var repo = RepoWithFeature();
        TestRepo.Write(repo.Git, "README.md", "unstaged\n");

        Assert.Throws<GitPaException>(() => Commit(repo));
    }

    [Theory]
    [InlineData("main")]
    [InlineData("dev")]
    [InlineData("does-not-exist")]
    public void RejectsInvalidTargets(string target)
    {
        using var repo = RepoWithFeature();
        TestRepo.Write(repo.Git, "README.md", "changed\n");
        repo.Git.Run("add", "README.md");

        Assert.Throws<GitPaException>(() =>
            CommitOperation.Run(repo.Git, repo.Config, new CommitRequest(target, "summary", null, false), repo.Reporter));
    }
}
