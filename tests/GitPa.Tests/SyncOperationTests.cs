namespace GitPa.Tests;

public class SyncOperationTests
{
    static GitRunner TeammatePushesToDev(TestRepo repo, string file, string content)
    {
        var teammate = repo.Clone("teammate");
        teammate.Run("switch", "--quiet", "dev");
        TestRepo.Write(teammate, file, content);
        TestRepo.Commit(teammate, $"teammate edits {file}");
        teammate.Run("push", "--quiet", "origin", "dev");
        return teammate;
    }

    [Fact]
    public void PullsRemoteDev_KeepingStagedAndUnstagedChanges()
    {
        using var repo = new TestRepo();
        TeammatePushesToDev(repo, "teammate.txt", "teammate\n");
        TestRepo.Write(repo.Git, "staged.txt", "staged\n");
        repo.Git.Run("add", "staged.txt");
        TestRepo.Write(repo.Git, "README.md", "unstaged\n");

        var pulled = SyncOperation.Run(repo.Git, repo.Config, repo.Reporter);

        Assert.Equal(1, pulled);
        Assert.Equal("teammate\n", TestRepo.Read(repo.Git, "teammate.txt"));
        Assert.Equal("staged.txt", repo.Git.Run("diff", "--cached", "--name-only"));
        Assert.Equal("README.md", repo.Git.Run("diff", "--name-only"));
        Assert.Empty(repo.Git.Run("stash", "list"));
    }

    [Fact]
    public void DoesNothingWhenUpToDate()
    {
        using var repo = new TestRepo();
        var before = repo.Git.Run("rev-parse", "HEAD");

        Assert.Equal(0, SyncOperation.Run(repo.Git, repo.Config, repo.Reporter));
        Assert.Equal(before, repo.Git.Run("rev-parse", "HEAD"));
    }

    [Fact]
    public void LeavesConflictOpen_AndKeepsChangesInStash()
    {
        using var repo = new TestRepo();
        TeammatePushesToDev(repo, "README.md", "teammate's readme\n");
        TestRepo.Write(repo.Git, "README.md", "my committed readme\n");
        TestRepo.Commit(repo.Git, "my readme");
        TestRepo.Write(repo.Git, "other.txt", "work in progress\n");
        repo.Git.Run("add", "other.txt");

        var error = Assert.Throws<GitPaException>(() => SyncOperation.Run(repo.Git, repo.Config, repo.Reporter));

        Assert.Contains("git stash pop --index", error.Message);
        Assert.True(repo.Git.Succeeds("rev-parse", "-q", "--verify", "MERGE_HEAD"));
        Assert.Single(repo.Git.Run("stash", "list").Split('\n'));
    }

    [Fact]
    public void RejectsWhenNotOnDev()
    {
        using var repo = new TestRepo();
        repo.Git.Run("switch", "--quiet", "main");

        Assert.Throws<GitPaException>(() => SyncOperation.Run(repo.Git, repo.Config, repo.Reporter));
    }
}
