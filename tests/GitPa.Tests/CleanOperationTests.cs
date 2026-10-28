namespace GitPa.Tests;

public class CleanOperationTests
{
    static void CommitTo(TestRepo repo, string branch, string file)
    {
        TestRepo.Write(repo.Git, file, $"{branch}\n");
        repo.Git.Run("add", file);
        CommitOperation.Run(repo.Git, repo.Config, new CommitRequest(branch, $"work on {branch}", null, Push: true), repo.Reporter);
    }

    [Fact]
    public void FindsMergedAndSquashMergedBranches_KeepsActiveAndUntouchedOnes()
    {
        using var repo = new TestRepo();
        foreach (var branch in new[] { "feat/merged", "feat/squashed", "feat/active", "feat/untouched" })
            NewBranchOperation.Run(repo.Git, repo.Config, branch, repo.Reporter);
        CommitTo(repo, "feat/merged", "merged.txt");
        CommitTo(repo, "feat/squashed", "squashed.txt");
        CommitTo(repo, "feat/active", "active.txt");

        // Pull requests completed on the server: one regular merge, one squash merge.
        var server = repo.Clone("server");
        server.Run("merge", "--quiet", "--no-ff", "--no-edit", "origin/feat/merged");
        server.Run("merge", "--quiet", "--squash", "origin/feat/squashed");
        server.Run("commit", "--quiet", "-m", "Squashed PR");
        server.Run("push", "--quiet", "origin", "main");
        repo.Git.Run("fetch", "--quiet", "origin");

        var candidates = CleanOperation.Find(repo.Git, repo.Config).ToDictionary(c => c.Name, c => c.Reason);

        Assert.Equal(["feat/merged", "feat/squashed"], candidates.Keys.Order());
        Assert.Contains("squash", candidates["feat/squashed"]);

        Assert.Empty(CleanOperation.Delete(repo.Git, candidates.Keys));
        Assert.False(repo.Git.LocalBranchExists("feat/merged"));
        Assert.True(repo.Git.LocalBranchExists("feat/active"));
        Assert.True(repo.Git.LocalBranchExists("feat/untouched"));
    }
}
