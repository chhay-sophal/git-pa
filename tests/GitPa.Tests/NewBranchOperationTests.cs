namespace GitPa.Tests;

public class NewBranchOperationTests
{
    [Fact]
    public void CreatesBranchFromLatestRemoteMain_WithoutSwitching()
    {
        using var repo = new TestRepo();
        var teammate = repo.Clone("teammate");
        TestRepo.Write(teammate, "approved.txt", "merged to main by someone else\n");
        TestRepo.Commit(teammate, "approved change");
        teammate.Run("push", "--quiet", "origin", "main");

        var baseRef = NewBranchOperation.Run(repo.Git, repo.Config, "feat/a", repo.Reporter);

        Assert.Equal("origin/main", baseRef);
        Assert.Equal(teammate.Run("rev-parse", "HEAD"), repo.Git.Run("rev-parse", "feat/a"));
        Assert.Equal("dev", repo.Git.CurrentBranch());
        Assert.False(repo.Git.Succeeds("rev-parse", "--abbrev-ref", "feat/a@{upstream}"));
    }

    [Fact]
    public void WorksWithUncommittedChangesOnDev()
    {
        using var repo = new TestRepo();
        TestRepo.Write(repo.Git, "README.md", "work in progress\n");

        NewBranchOperation.Run(repo.Git, repo.Config, "feat/a", repo.Reporter);

        Assert.Equal("work in progress\n", TestRepo.Read(repo.Git, "README.md"));
    }

    [Theory]
    [InlineData("dev")]
    [InlineData("bad..name")]
    public void RejectsExistingOrInvalidNames(string name)
    {
        using var repo = new TestRepo();

        Assert.Throws<GitPaException>(() => NewBranchOperation.Run(repo.Git, repo.Config, name, repo.Reporter));
    }
}
