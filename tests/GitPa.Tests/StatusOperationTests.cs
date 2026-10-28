namespace GitPa.Tests;

public class StatusOperationTests
{
    [Fact]
    public void ReportsBranchesAgainstMainDevAndRemote()
    {
        using var repo = new TestRepo();
        NewBranchOperation.Run(repo.Git, repo.Config, "feat/committed", repo.Reporter);
        NewBranchOperation.Run(repo.Git, repo.Config, "feat/local", repo.Reporter);
        TestRepo.Write(repo.Git, "README.md", "changed\n");
        repo.Git.Run("add", "README.md");
        CommitOperation.Run(repo.Git, repo.Config, new CommitRequest("feat/committed", "change", null, Push: true), repo.Reporter);
        var other = repo.Clone("other");
        TestRepo.Write(other, "main.txt", "new on main\n");
        TestRepo.Commit(other, "main moves on");
        other.Run("push", "--quiet", "origin", "main");
        repo.Git.Run("fetch", "--quiet", "origin");

        var status = StatusOperation.Run(repo.Git, repo.Config);

        Assert.Equal("origin/main", status.MainRef);
        Assert.Equal("origin/dev", status.DevUpstream);
        Assert.Equal((0, 0), (status.DevToPush, status.DevToPull));
        var committed = status.Branches.Single(b => b.Name == "feat/committed");
        Assert.Equal((1, 1, 0), (committed.AheadOfMain, committed.BehindMain, committed.NotInDev));
        Assert.Equal((0, 0), (committed.ToPush, committed.ToPull));
        var local = status.Branches.Single(b => b.Name == "feat/local");
        Assert.Equal((0, 1), (local.AheadOfMain, local.BehindMain));
        Assert.Null(local.ToPush);
        Assert.DoesNotContain(status.Branches, b => b.Name is "main" or "dev");
    }
}
