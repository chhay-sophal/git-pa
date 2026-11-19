namespace GitPa.Tests;

public class ConflictTests
{
    /// <summary>Resolves a conflict by writing files in the worktree, like someone would in their editor.</summary>
    sealed class FakeResolver(Func<TempWorktree, bool> resolve) : IConflictResolver
    {
        public List<Conflict> Conflicts { get; } = [];

        public bool Resolve(TempWorktree worktree, Conflict conflict)
        {
            Conflicts.Add(conflict);
            if (!resolve(worktree))
                return false;
            return GitPa.Conflicts.StageResolved(worktree.Git, conflict.Files).Count == 0;
        }
    }

    static string Readme(string line5) =>
        string.Join('\n', Enumerable.Range(1, 10).Select(i => i == 5 ? line5 : $"line {i}")) + "\n";

    static FakeResolver WritesReadme(string line5) => new(worktree =>
    {
        TestRepo.Write(worktree.Git, "README.md", Readme(line5));
        return true;
    });

    static void CommitTo(TestRepo repo, string branch, string readme)
    {
        TestRepo.Write(repo.Git, "README.md", readme);
        repo.Git.Run("add", "README.md");
        CommitOperation.Run(repo.Git, repo.Config, new CommitRequest(branch, $"change {branch}", null, Push: true), repo.Reporter);
    }

    /// <summary>
    /// feature/a changed line 5 and is in dev; feature/b starts from main, and you change the same line on dev
    /// (on top of feature/a's version) and stage it for feature/b.
    /// </summary>
    static TestRepo ChangeOnTopOfAnotherFeature()
    {
        var repo = new TestRepo();
        NewBranchOperation.Run(repo.Git, repo.Config, "feature/a", repo.Reporter);
        CommitTo(repo, "feature/a", Readme("line 5 from a"));
        NewBranchOperation.Run(repo.Git, repo.Config, "feature/b", repo.Reporter);
        TestRepo.Write(repo.Git, "README.md", Readme("line 5 from a and b"));
        repo.Git.Run("add", "README.md");
        return repo;
    }

    static CommitRequest ForB => new("feature/b", "change b", null, Push: true);

    [Fact]
    public void Commit_WithoutResolver_FailsAndNamesTheOtherFeature()
    {
        using var repo = ChangeOnTopOfAnotherFeature();

        var error = Assert.Throws<GitPaException>(() => CommitOperation.Run(repo.Git, repo.Config, ForB, repo.Reporter));

        Assert.Contains("feature/a changed it on dev", error.Message);
        Assert.Equal(repo.Git.Run("rev-parse", "main"), repo.Git.Run("rev-parse", "feature/b"));
        Assert.True(repo.Git.HasStagedChanges());
    }

    [Fact]
    public void Commit_ResolvedConflict_CommitsToTheFeatureAndKeepsYourVersionOnDev()
    {
        using var repo = ChangeOnTopOfAnotherFeature();
        var resolver = WritesReadme("line 5 from b");

        CommitOperation.Run(repo.Git, repo.Config, ForB, repo.Reporter, resolver);

        Assert.Equal(["README.md"], resolver.Conflicts.Single().Files);
        Assert.Equal(Readme("line 5 from b"), repo.Git.Run("show", "origin/feature/b:README.md") + "\n");
        Assert.Equal(Readme("line 5 from a and b"), TestRepo.Read(repo.Git, "README.md"));
        Assert.Equal("", repo.Git.Run("status", "--porcelain", "--untracked-files=no"));
        Assert.True(repo.Git.Succeeds("merge-base", "--is-ancestor", "feature/b", "origin/dev"));
        Assert.Equal(repo.Git.Run("rev-parse", "dev"), repo.Git.Run("rev-parse", "origin/dev"));
    }

    [Fact]
    public void Commit_CancelledResolution_CommitsNothing()
    {
        using var repo = ChangeOnTopOfAnotherFeature();

        Assert.Throws<GitPaException>(() =>
            CommitOperation.Run(repo.Git, repo.Config, ForB, repo.Reporter, new FakeResolver(_ => false)));

        Assert.Equal(repo.Git.Run("rev-parse", "main"), repo.Git.Run("rev-parse", "feature/b"));
        Assert.True(repo.Git.HasStagedChanges());
    }

    [Fact]
    public void StageResolved_LeavesFilesThatStillHaveMarkers()
    {
        using var repo = ChangeOnTopOfAnotherFeature();
        var resolver = new FakeResolver(_ => true); // saves nothing, so the markers stay

        Assert.Throws<GitPaException>(() => CommitOperation.Run(repo.Git, repo.Config, ForB, repo.Reporter, resolver));

        Assert.Single(resolver.Conflicts);
        Assert.Equal(repo.Git.Run("rev-parse", "main"), repo.Git.Run("rev-parse", "feature/b"));
    }

    [Fact]
    public void Update_ResolvedConflict_FinishesTheMerge()
    {
        using var repo = new TestRepo();
        NewBranchOperation.Run(repo.Git, repo.Config, "feature/b", repo.Reporter);
        CommitTo(repo, "feature/b", Readme("line 5 from b"));
        var other = repo.Clone("other");
        TestRepo.Write(other, "README.md", Readme("line 5 from main"));
        TestRepo.Commit(other, "main change");
        other.Run("push", "--quiet", "origin", "main");

        var result = UpdateOperation.Run(repo.Git, repo.Config, "feature/b", push: true, repo.Reporter,
            WritesReadme("line 5 from main and b"));

        Assert.Equal(1, result.FromMain);
        Assert.Equal(Readme("line 5 from main and b"), repo.Git.Run("show", "origin/feature/b:README.md") + "\n");
        Assert.True(repo.Git.Succeeds("merge-base", "--is-ancestor", "origin/main", "origin/feature/b"));
    }

    [Fact]
    public void Update_CancelledResolution_ChangesNothing()
    {
        using var repo = new TestRepo();
        NewBranchOperation.Run(repo.Git, repo.Config, "feature/b", repo.Reporter);
        CommitTo(repo, "feature/b", Readme("line 5 from b"));
        var before = repo.Git.Run("rev-parse", "feature/b");
        var other = repo.Clone("other");
        TestRepo.Write(other, "README.md", Readme("line 5 from main"));
        TestRepo.Commit(other, "main change");
        other.Run("push", "--quiet", "origin", "main");

        Assert.Throws<GitPaException>(() => UpdateOperation.Run(repo.Git, repo.Config, "feature/b", push: true,
            repo.Reporter, new FakeResolver(_ => false)));

        Assert.Equal(before, repo.Git.Run("rev-parse", "feature/b"));
    }

    [Fact]
    public void Promote_ResolvedConflict_MergesIntoTheStageAndNamesTheOtherFeature()
    {
        using var repo = new TestRepo();
        repo.Git.Run("branch", "--quiet", "uat", "origin/main");
        repo.Git.Run("push", "--quiet", "-u", "origin", "uat");
        repo.Git.Run("config", PaConfig.StagesKey, "uat");
        NewBranchOperation.Run(repo.Git, repo.Config, "feature/a", repo.Reporter);
        CommitTo(repo, "feature/a", Readme("line 5 from a"));
        PromoteOperation.Run(repo.Git, repo.Config, "feature/a", null, push: true, repo.Reporter);
        var teammate = repo.Clone("teammate");
        teammate.Run("switch", "--quiet", "-c", "feature/b", "origin/main");
        TestRepo.Write(teammate, "README.md", Readme("line 5 from b"));
        TestRepo.Commit(teammate, "change b");
        teammate.Run("push", "--quiet", "origin", "feature/b");
        var resolver = WritesReadme("line 5 from a and b");

        PromoteOperation.Run(repo.Git, repo.Config, "feature/b", null, push: true, repo.Reporter, resolver);

        Assert.Contains("feature/a changed it on uat", resolver.Conflicts.Single().Summary);
        Assert.Equal(Readme("line 5 from a and b"), repo.Git.Run("show", "origin/uat:README.md") + "\n");
    }
}
