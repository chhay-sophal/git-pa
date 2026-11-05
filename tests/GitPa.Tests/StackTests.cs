namespace GitPa.Tests;

public class StackTests
{
    /// <summary>Commits a file to the branch the way git pa commit does (and merges it into dev).</summary>
    static void CommitTo(TestRepo repo, string branch, string file, string? content = null)
    {
        TestRepo.Write(repo.Git, file, content ?? $"{branch}: {file}\n");
        repo.Git.Run("add", file);
        CommitOperation.Run(repo.Git, repo.Config, new CommitRequest(branch, $"add {file}", null, Push: true), repo.Reporter);
    }

    /// <summary>feature/1 with one commit, and feature/2 started from it with one more.</summary>
    static TestRepo Stacked()
    {
        var repo = new TestRepo();
        NewBranchOperation.Run(repo.Git, repo.Config, "feature/1", repo.Reporter);
        CommitTo(repo, "feature/1", "one.txt");
        NewBranchOperation.Run(repo.Git, repo.Config, "feature/2", repo.Reporter, from: "feature/1");
        CommitTo(repo, "feature/2", "two.txt");
        return repo;
    }

    static GitRunner PushToMain(TestRepo repo, Action<GitRunner> change)
    {
        var other = repo.Clone($"main-{Guid.NewGuid():N}"[..12]);
        change(other);
        other.Run("push", "--quiet", "origin", "main");
        repo.Git.Run("fetch", "--quiet", "origin");
        return other;
    }

    static bool Has(TestRepo repo, string reference, string file) => repo.Git.Succeeds("cat-file", "-e", $"{reference}:{file}");

    [Fact]
    public void NewFrom_StartsFromTheBaseAndRemembersIt()
    {
        using var repo = Stacked();

        Assert.True(Has(repo, "feature/2", "one.txt"));
        Assert.Equal("feature/1", Stack.SavedBase(repo.Git, "feature/2"));
        Assert.Equal("feature/1", Stack.Base(repo.Git, repo.Config, "feature/2"));
        Assert.Null(Stack.Base(repo.Git, repo.Config, "feature/1"));
    }

    [Theory]
    [InlineData("dev")]
    [InlineData("feature/missing")]
    public void NewFrom_RejectsBranchesItCantBuildOn(string from)
    {
        using var repo = new TestRepo();

        Assert.Throws<GitPaException>(() => NewBranchOperation.Run(repo.Git, repo.Config, "feature/2", repo.Reporter, from));
        Assert.False(repo.Git.LocalBranchExists("feature/2"));
    }

    [Fact]
    public void NewFromMain_IsNotStacked()
    {
        using var repo = new TestRepo();

        NewBranchOperation.Run(repo.Git, repo.Config, "feature/2", repo.Reporter, from: "main");

        Assert.Null(Stack.SavedBase(repo.Git, "feature/2"));
    }

    [Fact]
    public void GuessesTheBaseForATeammatesBranch()
    {
        using var repo = Stacked();
        NewBranchOperation.Run(repo.Git, repo.Config, "feature/3", repo.Reporter, from: "feature/2");
        CommitTo(repo, "feature/3", "three.txt");
        var teammate = repo.Clone("teammate");
        teammate.Run("config", PaConfig.MainKey, "main");
        teammate.Run("config", PaConfig.DevKey, "dev");
        var config = PaConfig.Load(teammate);

        Assert.Null(Stack.SavedBase(teammate, "feature/3"));
        Assert.Equal("feature/2", Stack.Base(teammate, config, "feature/3")); // the nearest, not feature/1
        Assert.Equal("feature/1", Stack.Base(teammate, config, "feature/2"));
        Assert.Null(Stack.Base(teammate, config, "feature/1"));
    }

    [Fact]
    public void ABranchWithoutCommitsIsNoBase()
    {
        using var repo = Stacked();
        NewBranchOperation.Run(repo.Git, repo.Config, "feature/empty", repo.Reporter);
        repo.Git.Run("config", "--unset", Stack.Key("feature/2"));

        Assert.Equal("feature/1", Stack.Base(repo.Git, repo.Config, "feature/2"));
    }

    [Fact]
    public void BaseIsGoneOnceItsMergedIntoMain()
    {
        using var repo = Stacked();
        PushToMain(repo, main => main.Run("merge", "--quiet", "--no-edit", "origin/feature/1"));

        Assert.Null(Stack.Base(repo.Git, repo.Config, "feature/2"));
    }

    [Fact]
    public void BaseIsGoneOnceItsSquashMergedIntoMain()
    {
        using var repo = Stacked();
        PushToMain(repo, main =>
        {
            main.Run("merge", "--quiet", "--squash", "origin/feature/1");
            main.Run("commit", "--quiet", "-m", "feature 1 (squashed)");
        });

        Assert.Null(Stack.Base(repo.Git, repo.Config, "feature/2"));
    }

    [Fact]
    public void BaseIsGoneOnceItsDeleted()
    {
        using var repo = Stacked();
        repo.Git.Run("branch", "-D", "feature/1");
        repo.Git.Run("push", "--quiet", "origin", "--delete", "feature/1");

        Assert.Null(Stack.Base(repo.Git, repo.Config, "feature/2"));
    }

    [Fact]
    public void UpdateBringsInTheBasesNewCommits()
    {
        using var repo = Stacked();
        CommitTo(repo, "feature/1", "review-fix.txt");
        PushToMain(repo, main =>
        {
            TestRepo.Write(main, "approved.txt", "approved\n");
            TestRepo.Commit(main, "approved");
        });

        var result = UpdateOperation.Run(repo.Git, repo.Config, "feature/2", push: true, repo.Reporter);

        Assert.Equal(new UpdateResult("feature/1", FromBase: 1, FromMain: 1), result);
        Assert.True(Has(repo, "origin/feature/2", "review-fix.txt"));
        Assert.True(Has(repo, "origin/feature/2", "approved.txt"));
        Assert.Equal("dev", repo.Git.CurrentBranch());
    }

    [Fact]
    public void UpdateConflictUndoesTheBaseMergeToo()
    {
        using var repo = Stacked();
        CommitTo(repo, "feature/2", "README.md", "feature 2 version\n");
        CommitTo(repo, "feature/1", "review-fix.txt");
        PushToMain(repo, main =>
        {
            TestRepo.Write(main, "README.md", "main version\n");
            TestRepo.Commit(main, "main change");
        });
        var before = repo.Git.Run("rev-parse", "feature/2");

        var error = Assert.Throws<GitPaException>(() =>
            UpdateOperation.Run(repo.Git, repo.Config, "feature/2", push: true, repo.Reporter));

        Assert.Contains("conflicts", error.Message);
        Assert.Equal(before, repo.Git.Run("rev-parse", "feature/2"));
    }

    [Fact]
    public void StatusShowsTheBase()
    {
        using var repo = Stacked();

        var branches = StatusOperation.Run(repo.Git, repo.Config).Branches;

        Assert.Equal("feature/1", branches.Single(b => b.Name == "feature/2").Base);
        Assert.Null(branches.Single(b => b.Name == "feature/1").Base);
    }

    [Fact]
    public void PromoteWarnsWhenTheBaseIsntInTheStage()
    {
        using var repo = Stacked();
        repo.Git.Run("branch", "--quiet", "uat", "origin/main");
        repo.Git.Run("push", "--quiet", "-u", "origin", "uat");
        repo.Git.Run("config", PaConfig.StagesKey, "uat");

        PromoteOperation.Run(repo.Git, repo.Config, "feature/2", null, push: true, repo.Reporter);

        Assert.Contains(repo.Reporter.Warnings, w => w.Contains("git pa promote feature/1"));
        Assert.True(Has(repo, "origin/uat", "one.txt")); // it can't work without its base
    }

    [Fact]
    public void PromotingTheBaseFirstAvoidsTheWarning()
    {
        using var repo = Stacked();
        repo.Git.Run("branch", "--quiet", "uat", "origin/main");
        repo.Git.Run("push", "--quiet", "-u", "origin", "uat");
        repo.Git.Run("config", PaConfig.StagesKey, "uat");

        PromoteOperation.Run(repo.Git, repo.Config, "feature/1", null, push: true, repo.Reporter);
        PromoteOperation.Run(repo.Git, repo.Config, "feature/2", null, push: true, repo.Reporter);

        Assert.Empty(repo.Reporter.Warnings);
    }
}
