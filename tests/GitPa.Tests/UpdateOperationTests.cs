namespace GitPa.Tests;

public class UpdateOperationTests
{
    static GitRunner PushToMain(TestRepo repo, string file, string content)
    {
        var other = repo.Clone("other");
        TestRepo.Write(other, file, content);
        TestRepo.Commit(other, $"main: {file}");
        other.Run("push", "--quiet", "origin", "main");
        return other;
    }

    [Fact]
    public void MergesLatestMainIntoFeature_WithoutSwitching()
    {
        using var repo = new TestRepo();
        NewBranchOperation.Run(repo.Git, repo.Config, "feat/a", repo.Reporter);
        var other = PushToMain(repo, "approved.txt", "approved\n");
        TestRepo.Write(repo.Git, "README.md", "uncommitted on dev\n");

        var merged = UpdateOperation.Run(repo.Git, repo.Config, "feat/a", push: true, repo.Reporter);

        Assert.Equal(1, merged);
        Assert.True(repo.Git.Succeeds("merge-base", "--is-ancestor", other.Run("rev-parse", "HEAD"), "feat/a"));
        Assert.Equal(repo.Git.Run("rev-parse", "feat/a"), repo.Git.Run("rev-parse", "origin/feat/a"));
        Assert.Equal("dev", repo.Git.CurrentBranch());
        Assert.Equal("uncommitted on dev\n", TestRepo.Read(repo.Git, "README.md"));
        Assert.Equal(0, UpdateOperation.Run(repo.Git, repo.Config, "feat/a", push: false, repo.Reporter));
    }

    [Fact]
    public void ConflictChangesNothing_AndExplainsHowToResolve()
    {
        using var repo = new TestRepo();
        NewBranchOperation.Run(repo.Git, repo.Config, "feat/a", repo.Reporter);
        TestRepo.Write(repo.Git, "README.md", "feature version\n");
        repo.Git.Run("add", "README.md");
        CommitOperation.Run(repo.Git, repo.Config, new CommitRequest("feat/a", "feature", null, Push: false), repo.Reporter);
        PushToMain(repo, "README.md", "main version\n");
        var before = repo.Git.Run("rev-parse", "feat/a");

        var error = Assert.Throws<GitPaException>(() =>
            UpdateOperation.Run(repo.Git, repo.Config, "feat/a", push: false, repo.Reporter));

        Assert.Contains("git worktree add ../feat-a feat/a", error.Message);
        Assert.Equal(before, repo.Git.Run("rev-parse", "feat/a"));
        Assert.Single(repo.Git.Run("worktree", "list").Split('\n'));
    }

    [Theory]
    [InlineData("main")]
    [InlineData("dev")]
    public void RejectsMainAndDev(string branch)
    {
        using var repo = new TestRepo();

        Assert.Throws<GitPaException>(() => UpdateOperation.Run(repo.Git, repo.Config, branch, false, repo.Reporter));
    }
}
