namespace GitPa.Tests;

public class ResetDevOperationTests
{
    /// <summary>Three features committed and merged into dev: feat/a, feat/b, and abandoned feat/old.</summary>
    static TestRepo RepoWithFeaturesInDev()
    {
        var repo = new TestRepo();
        foreach (var branch in new[] { "feat/a", "feat/b", "feat/old" })
        {
            NewBranchOperation.Run(repo.Git, repo.Config, branch, repo.Reporter);
            var file = branch.Replace('/', '-') + ".txt";
            TestRepo.Write(repo.Git, file, $"{branch}\n");
            repo.Git.Run("add", file);
            CommitOperation.Run(repo.Git, repo.Config, new CommitRequest(branch, $"work on {branch}", null, Push: true), repo.Reporter);
        }
        return repo;
    }

    static string Reset(TestRepo repo, params string[] keep)
    {
        var candidates = ResetDevOperation.Candidates(repo.Git, repo.Config);
        return ResetDevOperation.Run(repo.Git, repo.Config, candidates.Where(c => keep.Contains(c.Name)).ToList(), repo.Reporter);
    }

    static bool Exists(GitRunner git, string file) => File.Exists(Path.Combine(git.WorkingDirectory, file));

    [Fact]
    public void RebuildsDevFromMainAndKeptBranches_AndForcePushes()
    {
        using var repo = RepoWithFeaturesInDev();
        var oldDev = repo.Git.Run("rev-parse", "dev");

        var candidates = ResetDevOperation.Candidates(repo.Git, repo.Config);
        Assert.Equal(["feat/a", "feat/b", "feat/old"], candidates.Select(c => c.Name).Order());
        Assert.All(candidates, c => Assert.True(c.InDev));

        var backup = Reset(repo, "feat/a", "feat/b");

        Assert.Equal(oldDev, repo.Git.Run("rev-parse", backup));
        Assert.Equal(repo.Git.Run("rev-parse", "dev"), repo.Git.Run("rev-parse", "origin/dev"));
        Assert.True(Exists(repo.Git, "feat-a.txt"));
        Assert.True(Exists(repo.Git, "feat-b.txt"));
        Assert.False(Exists(repo.Git, "feat-old.txt"));
        Assert.False(repo.Git.Succeeds("merge-base", "--is-ancestor", "feat/old", "dev"));
        Assert.Contains(DevBranch.ResetTrailer, repo.Git.Run("log", "--format=%B", "origin/main..dev"));
    }

    [Fact]
    public void TeammateSyncFollowsTheReset_KeepingWorkAndReportingLocalOnlyCommits()
    {
        using var repo = RepoWithFeaturesInDev();
        var teammate = repo.Clone("teammate");
        teammate.Run("switch", "--quiet", "dev");
        PaConfig.Save(teammate, "main", "dev", global: false);
        TestRepo.Write(teammate, "direct.txt", "committed straight to dev\n");
        TestRepo.Commit(teammate, "direct dev commit");
        TestRepo.Write(teammate, "wip.txt", "work in progress\n");
        teammate.Run("add", "wip.txt");

        Reset(repo, "feat/a");
        teammate.Run("fetch", "--quiet", "origin");
        Assert.True(StatusOperation.Run(teammate, PaConfig.Load(teammate)).DevWasReset);

        var reporter = new RecordingReporter();
        var result = SyncOperation.Run(teammate, PaConfig.Load(teammate), reporter);

        Assert.NotNull(result.ResetBackup);
        Assert.Equal(teammate.Run("rev-parse", "origin/dev"), teammate.Run("rev-parse", "dev"));
        Assert.Equal("wip.txt", teammate.Run("diff", "--cached", "--name-only"));
        Assert.False(Exists(teammate, "feat-b.txt"));
        Assert.Contains("direct dev commit", Assert.Single(reporter.Warnings));
        Assert.Equal(0, SyncOperation.Run(teammate, PaConfig.Load(teammate), reporter).Pulled);
    }

    [Fact]
    public void TeammateCommitFollowsTheReset()
    {
        using var repo = RepoWithFeaturesInDev();
        var teammate = repo.Clone("teammate");
        teammate.Run("switch", "--quiet", "dev");
        PaConfig.Save(teammate, "main", "dev", global: false);
        Reset(repo, "feat/a");
        TestRepo.Write(teammate, "feat-a.txt", "teammate improves feat/a\n");
        teammate.Run("add", "feat-a.txt");

        CommitOperation.Run(teammate, PaConfig.Load(teammate),
            new CommitRequest("feat/a", "improve", null, Push: true), new RecordingReporter());

        Assert.False(Exists(teammate, "feat-old.txt"));
        Assert.Equal(teammate.Run("rev-parse", "dev"), teammate.Run("rev-parse", "origin/dev"));
        Assert.True(teammate.Succeeds("merge-base", "--is-ancestor", repo.Git.Run("rev-parse", "origin/dev"), "dev"));
    }

    [Fact]
    public void FailsWithoutChanges_WhenSomeonePushedToDevMeanwhile()
    {
        using var repo = RepoWithFeaturesInDev();
        var candidates = ResetDevOperation.Candidates(repo.Git, repo.Config);
        var teammate = repo.Clone("teammate");
        teammate.Run("switch", "--quiet", "dev");
        TestRepo.Write(teammate, "late.txt", "late push\n");
        TestRepo.Commit(teammate, "late push");
        teammate.Run("push", "--quiet", "origin", "dev");
        var devBefore = repo.Git.Run("rev-parse", "dev");

        Assert.Throws<GitPaException>(() => ResetDevOperation.Run(repo.Git, repo.Config, candidates, repo.Reporter));

        Assert.Equal(devBefore, repo.Git.Run("rev-parse", "dev"));
        Assert.Equal(teammate.Run("rev-parse", "HEAD"), teammate.Run("ls-remote", "origin", "dev").Split('\t')[0]);
    }
}
