namespace GitPa.Tests;

public class PromoteOperationTests
{
    /// <summary>A repo with a uat stage (pushed, branched from main) and the given stages configured.</summary>
    static TestRepo WithStages(string stages = "uat")
    {
        var repo = new TestRepo();
        foreach (var stage in stages.Split(' '))
        {
            repo.Git.Run("branch", "--quiet", stage, "origin/main");
            repo.Git.Run("push", "--quiet", "-u", "origin", stage);
        }
        repo.Git.Run("config", PaConfig.StagesKey, stages);
        return repo;
    }

    /// <summary>Creates a feature with one commit, merged into dev the way git pa commit does.</summary>
    static void Feature(TestRepo repo, string branch, string file, bool push = true)
    {
        NewBranchOperation.Run(repo.Git, repo.Config, branch, repo.Reporter);
        TestRepo.Write(repo.Git, file, $"{branch}\n");
        repo.Git.Run("add", file);
        CommitOperation.Run(repo.Git, repo.Config, new CommitRequest(branch, $"add {file}", null, push), repo.Reporter);
    }

    static bool Contains(TestRepo repo, string branch, string file) =>
        repo.Git.Succeeds("cat-file", "-e", $"{branch}:{file}");

    [Fact]
    public void MergesOnlyThatFeature_WithoutSwitching()
    {
        using var repo = WithStages();
        Feature(repo, "feature/1-a", "a.txt");
        Feature(repo, "feature/2-b", "b.txt");
        TestRepo.Write(repo.Git, "README.md", "uncommitted on dev\n");

        var result = PromoteOperation.Run(repo.Git, repo.Config, "feature/1-a", null, push: true, repo.Reporter);

        Assert.Equal(new PromoteResult("uat", AlreadyThere: false, Commits: 1), result);
        Assert.True(Contains(repo, "origin/uat", "a.txt"));
        Assert.False(Contains(repo, "origin/uat", "b.txt")); // still only in dev
        Assert.Equal("dev", repo.Git.CurrentBranch());
        Assert.Equal("uncommitted on dev\n", TestRepo.Read(repo.Git, "README.md"));
        Assert.Empty(repo.Reporter.Warnings);
    }

    [Fact]
    public void ReportsAFeatureAlreadyInTheStage()
    {
        using var repo = WithStages();
        Feature(repo, "feature/1-a", "a.txt");
        PromoteOperation.Run(repo.Git, repo.Config, "feature/1-a", null, push: true, repo.Reporter);

        var again = PromoteOperation.Run(repo.Git, repo.Config, "feature/1-a", null, push: true, repo.Reporter);

        Assert.True(again.AlreadyThere);
        Assert.Null(PromoteOperation.NextStage(repo.Git, repo.Config, "feature/1-a"));
    }

    [Fact]
    public void WarnsWhenTheFeatureSkippedDev()
    {
        using var repo = WithStages();
        NewBranchOperation.Run(repo.Git, repo.Config, "feature/1-a", repo.Reporter);
        var worktree = repo.Clone("elsewhere");
        worktree.Run("switch", "--quiet", "-c", "feature/1-a", "origin/main");
        TestRepo.Write(worktree, "a.txt", "never in dev\n");
        TestRepo.Commit(worktree, "add a");
        worktree.Run("push", "--quiet", "-u", "origin", "feature/1-a");
        repo.Git.Run("fetch", "--quiet", "origin");

        PromoteOperation.Run(repo.Git, repo.Config, "feature/1-a", null, push: false, repo.Reporter);

        Assert.Contains(repo.Reporter.Warnings, w => w.Contains("skipped testing there"));
        Assert.True(Contains(repo, "uat", "a.txt")); // the newer remote copy was promoted
    }

    [Fact]
    public void PicksTheNextStageInOrder()
    {
        using var repo = WithStages("uat staging");
        Feature(repo, "feature/1-a", "a.txt");

        Assert.Equal("uat", PromoteOperation.NextStage(repo.Git, repo.Config, "feature/1-a"));
        PromoteOperation.Run(repo.Git, repo.Config, "feature/1-a", "uat", push: true, repo.Reporter);
        Assert.Equal("staging", PromoteOperation.NextStage(repo.Git, repo.Config, "feature/1-a"));

        var result = PromoteOperation.Run(repo.Git, repo.Config, "feature/1-a", "staging", push: true, repo.Reporter);

        Assert.Equal("staging", result.Stage);
        Assert.Empty(repo.Reporter.Warnings); // it came through uat
    }

    [Fact]
    public void ConflictChangesNothing()
    {
        using var repo = WithStages();
        Feature(repo, "feature/1-a", "README.md");
        var other = repo.Clone("other");
        other.Run("switch", "--quiet", "uat");
        TestRepo.Write(other, "README.md", "changed in uat\n");
        TestRepo.Commit(other, "uat hotfix");
        other.Run("push", "--quiet", "origin", "uat");
        var before = other.Run("rev-parse", "HEAD");

        var error = Assert.Throws<GitPaException>(() =>
            PromoteOperation.Run(repo.Git, repo.Config, "feature/1-a", null, push: true, repo.Reporter));

        Assert.Contains("conflicts", error.Message);
        Assert.Equal(before, repo.Git.Run("rev-parse", "uat"));
        Assert.Equal(before, repo.Git.Run("rev-parse", "origin/uat"));
    }

    [Theory]
    [InlineData("", "No stages are set up")]
    [InlineData("uat", "'qa' is not a stage")]
    public void RejectsMissingStages(string stages, string message)
    {
        using var repo = stages.Length == 0 ? new TestRepo() : WithStages(stages);

        var error = Assert.Throws<GitPaException>(() =>
            PromoteOperation.Run(repo.Git, repo.Config, "feature/1-a", stages.Length == 0 ? null : "qa", push: false, repo.Reporter));

        Assert.Contains(message, error.Message);
    }

    [Fact]
    public void ExplainsHowToCreateAMissingStageBranch()
    {
        using var repo = new TestRepo();
        repo.Git.Run("config", PaConfig.StagesKey, "uat");
        Feature(repo, "feature/1-a", "a.txt");

        var error = Assert.Throws<GitPaException>(() =>
            PromoteOperation.Run(repo.Git, repo.Config, "feature/1-a", null, push: false, repo.Reporter));

        Assert.Contains("git branch uat origin/main", error.Message);
    }

    [Fact]
    public void StagesAreNotFeatureBranches()
    {
        using var repo = WithStages();
        Feature(repo, "feature/1-a", "a.txt");

        Assert.Equal(["feature/1-a"], Branches.LocalFeatures(repo.Git, repo.Config));
        Assert.Throws<GitPaException>(() => PromoteOperation.Run(repo.Git, repo.Config, "uat", null, push: false, repo.Reporter));
        Assert.DoesNotContain(CleanOperation.Find(repo.Git, repo.Config), c => c.Name == "uat");
        Assert.DoesNotContain(ResetDevOperation.Candidates(repo.Git, repo.Config), c => c.Name == "uat");
    }

    [Fact]
    public void StatusShowsEachStage()
    {
        using var repo = WithStages();
        Feature(repo, "feature/1-a", "a.txt");
        Feature(repo, "feature/2-b", "b.txt");
        PromoteOperation.Run(repo.Git, repo.Config, "feature/1-a", null, push: true, repo.Reporter);

        var status = StatusOperation.Run(repo.Git, repo.Config);

        Assert.Equal(["uat"], status.Stages);
        Assert.DoesNotContain(status.Branches, b => b.Name == "uat");
        Assert.Equal([0], status.Branches.Single(b => b.Name == "feature/1-a").NotInStages);
        Assert.Equal([1], status.Branches.Single(b => b.Name == "feature/2-b").NotInStages);
    }

    [Theory]
    [InlineData("uat", new[] { "uat" })]
    [InlineData(" uat, staging ", new[] { "uat", "staging" })]
    [InlineData("none", new string[0])]
    public void ParsesStages(string value, string[] expected)
    {
        using var repo = new TestRepo();

        Assert.Equal(expected, PaConfig.ParseStages(repo.Git, value, "main", "dev"));
    }

    [Theory]
    [InlineData("dev")]
    [InlineData("main")]
    [InlineData("uat uat")]
    [InlineData("bad..name")]
    public void RejectsInvalidStages(string value)
    {
        using var repo = new TestRepo();

        Assert.Throws<GitPaException>(() => PaConfig.ParseStages(repo.Git, value, "main", "dev"));
    }
}
