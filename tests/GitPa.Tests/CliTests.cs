using GitPa.Commands;
using Spectre.Console.Testing;

namespace GitPa.Tests;

public class CliTests
{
    static int Run(TestRepo repo, TestConsole console, params string[] args) =>
        GitPaCli.Build(new Cli(console, repo.Git.WorkingDirectory)).Parse(args).Invoke();

    static TestConsole InteractiveConsole()
    {
        var console = new TestConsole().Interactive();
        console.Profile.Width = 200;
        return console;
    }

    [Fact]
    public void New_PromptsForTheBranchName()
    {
        using var repo = new TestRepo();
        var console = InteractiveConsole();
        console.Input.PushTextWithEnter("bad..name"); // rejected, asked again
        console.Input.PushTextWithEnter("story/PA-77_Add-Login");

        var exitCode = Run(repo, console, "new");

        Assert.Equal(0, exitCode);
        Assert.Contains("Not a valid branch name", console.Output);
        Assert.True(repo.Git.LocalBranchExists("story/PA-77_Add-Login")); // kept as typed
    }

    [Fact]
    public void New_TakesTheNameAsIs_AndRequiresIt_WhenNotInteractive()
    {
        using var repo = new TestRepo();

        Assert.Equal(0, Run(repo, new TestConsole(), "new", "JIRA-1234"));
        var missing = new TestConsole();
        Assert.NotEqual(0, Run(repo, missing, "new"));
        Assert.NotEqual(0, Run(repo, new TestConsole(), "new", "bad..name"));

        Assert.True(repo.Git.LocalBranchExists("JIRA-1234"));
        Assert.Contains("Missing branch name", missing.Output);
    }

    [Fact]
    public void Init_Shared_WritesTeamFileThatTeammatesUse()
    {
        using var repo = new TestRepo();
        var console = new TestConsole();

        Assert.Equal(0, Run(repo, console, "init", "--shared", "--main", "main", "--dev", "dev", "--stages", "uat"));
        Assert.Contains("commit it", console.Output);
        Assert.NotEqual(0, Run(repo, new TestConsole(), "init", "--shared", "--global", "--main", "main", "--dev", "dev"));

        TestRepo.Commit(repo.Git, "share git-pa settings");
        repo.Git.Run("push", "--quiet", "origin", "dev");
        var teammate = repo.Clone("teammate");
        teammate.Run("switch", "--quiet", "dev");

        Assert.Equal(("main", "dev"), (PaConfig.Load(teammate).Main, PaConfig.Load(teammate).Dev));
        Assert.Equal(["uat"], PaConfig.Load(teammate).Stages);
    }

    [Fact]
    public void Init_SavesStages_AndPromoteUsesThem()
    {
        using var repo = new TestRepo();
        var noStages = new TestConsole();
        Assert.NotEqual(0, Run(repo, noStages, "promote", "feature/1"));
        Assert.Contains("--stages uat", noStages.Output);

        Assert.Equal(0, Run(repo, new TestConsole(), "init", "--main", "main", "--dev", "dev", "--stages", "uat"));
        Assert.Equal("uat", PaConfig.Get(repo.Git, PaConfig.StagesKey));
        Assert.NotEqual(0, Run(repo, new TestConsole(), "init", "--main", "main", "--dev", "dev", "--stages", "dev"));

        repo.Git.Run("branch", "--quiet", "uat", "origin/main");
        repo.Git.Run("push", "--quiet", "-u", "origin", "uat");
        Assert.Equal(0, Run(repo, new TestConsole(), "new", "feature/1"));
        TestRepo.Write(repo.Git, "a.txt", "a\n");
        Assert.Equal(0, Run(repo, new TestConsole(), "commit", "-b", "feature/1", "-m", "add a", "--all"));

        var dryRun = new TestConsole();
        Assert.Equal(0, Run(repo, dryRun, "promote", "feature/1", "--dry-run"));
        Assert.Contains("merge --no-ff --no-edit feature/1", dryRun.Output);

        var promote = new TestConsole();
        Assert.Equal(0, Run(repo, promote, "promote", "feature/1"));
        Assert.Contains("Merged feature/1 into uat", promote.Output);
        Assert.True(repo.Git.Succeeds("cat-file", "-e", "origin/uat:a.txt"));

        var again = new TestConsole();
        again.Profile.Width = 200;
        Assert.Equal(0, Run(repo, again, "promote", "feature/1"));
        Assert.Contains("git pa pr feature/1", again.Output);

        Assert.Equal(0, Run(repo, new TestConsole(), "init", "--main", "main", "--dev", "dev", "--stages", "none"));
        Assert.Null(PaConfig.Get(repo.Git, PaConfig.StagesKey));
    }

    [Fact]
    public void Pr_WarnsWhenFeatureIsNotInTheLastStage()
    {
        using var repo = new TestRepo();
        repo.Git.Run("config", PaConfig.StagesKey, "uat");
        repo.Git.Run("config", PullRequestUrl.TemplateKey, "https://example.com/pr?from={branch}&to={target}");
        repo.Git.Run("branch", "--quiet", "uat", "origin/main");
        repo.Git.Run("push", "--quiet", "-u", "origin", "uat");
        Assert.Equal(0, Run(repo, new TestConsole(), "new", "feature/1"));
        TestRepo.Write(repo.Git, "a.txt", "a\n");
        Assert.Equal(0, Run(repo, new TestConsole(), "commit", "-b", "feature/1", "-m", "add a", "--all"));

        var before = new TestConsole();
        before.Profile.Width = 200;
        Assert.Equal(0, Run(repo, before, "pr", "feature/1", "--print"));
        Assert.Contains("git pa promote feature/1", before.Output);

        Assert.Equal(0, Run(repo, new TestConsole(), "promote", "feature/1"));
        var status = new TestConsole();
        status.Profile.Width = 200;
        Assert.Equal(0, Run(repo, status, "status"));
        Assert.Contains("In uat", status.Output);
        var after = new TestConsole();
        Assert.Equal(0, Run(repo, after, "pr", "feature/1", "--print"));
        Assert.DoesNotContain("promote", after.Output);
    }

    [Fact]
    public void New_From_StacksOnAFeatureBranch()
    {
        using var repo = new TestRepo();
        Assert.Equal(0, Run(repo, new TestConsole(), "new", "feature/1"));
        var console = new TestConsole();
        console.Profile.Width = 200;

        Assert.Equal(0, Run(repo, console, "new", "feature/2", "--from", "feature/1"));

        Assert.Equal("feature/1", Stack.SavedBase(repo.Git, "feature/2"));
        Assert.Contains("It builds on feature/1", console.Output);
    }

    [Fact]
    public void New_AsksWhatToStartFrom_WhenThereAreFeatureBranches()
    {
        using var repo = new TestRepo();
        Assert.Equal(0, Run(repo, new TestConsole(), "new", "feature/1"));
        var console = InteractiveConsole();
        console.Input.PushKey(ConsoleKey.DownArrow); // main -> feature/1
        console.Input.PushKey(ConsoleKey.Enter);

        Assert.Equal(0, Run(repo, console, "new", "feature/2-next"));

        Assert.Contains("Start from:", console.Output);
        Assert.Equal("feature/1", Stack.SavedBase(repo.Git, "feature/2-next"));
    }

    [Fact]
    public void Pr_WarnsAboutAnUnmergedBase_ThenAsksForAnUpdate()
    {
        using var repo = new TestRepo();
        repo.Git.Run("config", PullRequestUrl.TemplateKey, "https://example.com/pr?from={branch}&to={target}");
        Assert.Equal(0, Run(repo, new TestConsole(), "new", "feature/1"));
        TestRepo.Write(repo.Git, "one.txt", "one\n");
        Assert.Equal(0, Run(repo, new TestConsole(), "commit", "-b", "feature/1", "-m", "one", "--all"));
        Assert.Equal(0, Run(repo, new TestConsole(), "new", "feature/2", "--from", "feature/1"));
        TestRepo.Write(repo.Git, "two.txt", "two\n");
        Assert.Equal(0, Run(repo, new TestConsole(), "commit", "-b", "feature/2", "-m", "two", "--all"));

        var unmerged = new TestConsole();
        unmerged.Profile.Width = 300;
        Assert.Equal(0, Run(repo, unmerged, "pr", "feature/2", "--print"));
        Assert.Contains("Merge feature/1's pull request first", unmerged.Output);

        var other = repo.Clone("reviewer");
        other.Run("merge", "--quiet", "--squash", "origin/feature/1");
        other.Run("commit", "--quiet", "-m", "feature 1 (squashed)");
        other.Run("push", "--quiet", "origin", "main");
        repo.Git.Run("fetch", "--quiet", "origin");

        var merged = new TestConsole();
        merged.Profile.Width = 300;
        Assert.Equal(0, Run(repo, merged, "pr", "feature/2", "--print"));
        Assert.Contains("Run git pa update feature/2 first", merged.Output);

        Assert.Equal(0, Run(repo, new TestConsole(), "update", "feature/2"));
        var updated = new TestConsole();
        updated.Profile.Width = 300;
        Assert.Equal(0, Run(repo, updated, "pr", "feature/2", "--print"));
        Assert.DoesNotContain("warning", updated.Output);
    }

    [Fact]
    public void Commit_PicksFilesBranchAndMessageInteractively()
    {
        using var repo = new TestRepo();
        NewBranchOperation.Run(repo.Git, repo.Config, "feat/a", repo.Reporter);
        TestRepo.Write(repo.Git, "README.md", "picked\n");
        TestRepo.Write(repo.Git, "scratch.txt", "not picked\n");
        var console = InteractiveConsole();
        console.Input.PushKey(ConsoleKey.Spacebar); // toggle README.md, the first file
        console.Input.PushKey(ConsoleKey.Enter);
        console.Input.PushKey(ConsoleKey.Enter); // feat/a, the only feature branch
        console.Input.PushTextWithEnter("Interactive commit");
        console.Input.PushKey(ConsoleKey.Enter); // no description

        var exitCode = Run(repo, console, "commit", "--no-push");

        Assert.Equal(0, exitCode);
        Assert.Equal("Interactive commit", repo.Git.Run("log", "-1", "--format=%s", "feat/a"));
        Assert.Equal("README.md", repo.Git.Run("diff-tree", "--no-commit-id", "--name-only", "-r", "feat/a"));
        Assert.Equal("?? scratch.txt", repo.Git.Run("status", "--porcelain"));
    }

    [Fact]
    public void Commit_ReadsMultiLineDescriptionUntilEmptyLine()
    {
        using var repo = new TestRepo();
        NewBranchOperation.Run(repo.Git, repo.Config, "feat/a", repo.Reporter);
        TestRepo.Write(repo.Git, "README.md", "changed\n");
        var console = InteractiveConsole();
        console.Input.PushTextWithEnter("First line");
        console.Input.PushTextWithEnter("Second line");
        console.Input.PushKey(ConsoleKey.Enter);

        var exitCode = Run(repo, console, "commit", "-a", "-b", "feat/a", "-m", "Summary", "--no-push");

        Assert.Equal(0, exitCode);
        Assert.Equal("First line\nSecond line", repo.Git.Run("log", "-1", "--format=%b", "feat/a"));
    }

    [Fact]
    public void Commit_RepeatedDescriptionOptionsBecomeParagraphs()
    {
        using var repo = new TestRepo();
        NewBranchOperation.Run(repo.Git, repo.Config, "feat/a", repo.Reporter);
        TestRepo.Write(repo.Git, "README.md", "changed\n");

        var exitCode = Run(repo, new TestConsole(),
            "commit", "-a", "-b", "feat/a", "-m", "Summary", "-d", "First paragraph", "-d", "Second paragraph", "--no-push");

        Assert.Equal(0, exitCode);
        Assert.Equal("First paragraph\n\nSecond paragraph", repo.Git.Run("log", "-1", "--format=%b", "feat/a"));
    }

    [Fact]
    public void Commit_KeepsTheSummaryAsWritten()
    {
        using var repo = new TestRepo();
        NewBranchOperation.Run(repo.Git, repo.Config, "feature/PA-77-add-login", repo.Reporter);
        TestRepo.Write(repo.Git, "README.md", "changed\n");

        var exitCode = Run(repo, new TestConsole(),
            "commit", "-a", "-b", "feature/PA-77-add-login", "-m", "Add login page", "--no-push");

        Assert.Equal(0, exitCode);
        Assert.Equal("Add login page", repo.Git.Run("log", "-1", "--format=%s", "feature/PA-77-add-login"));
    }

    [Fact]
    public void Commit_ChecksOptionsBeforeStaging_WhenNotInteractive()
    {
        using var repo = new TestRepo();
        NewBranchOperation.Run(repo.Git, repo.Config, "feat/a", repo.Reporter);
        TestRepo.Write(repo.Git, "README.md", "changed\n");
        var console = new TestConsole();

        var exitCode = Run(repo, console, "commit", "-a", "-b", "feat/a");

        Assert.Equal(1, exitCode);
        Assert.Contains("Missing --summary", console.Output);
        Assert.False(repo.Git.HasStagedChanges());
    }

    [Fact]
    public void DryRuns_ChangeNothing()
    {
        using var repo = new TestRepo();
        NewBranchOperation.Run(repo.Git, repo.Config, "feat/a", repo.Reporter);
        TestRepo.Write(repo.Git, "README.md", "changed\n");
        var refsBefore = repo.Git.Run("show-ref");

        var commit = new TestConsole();
        Assert.Equal(0, Run(repo, commit, "commit", "-a", "-b", "feat/a", "-m", "Dry summary", "--dry-run"));
        var newBranch = new TestConsole();
        Assert.Equal(0, Run(repo, newBranch, "new", "feat/b", "--dry-run"));
        var sync = new TestConsole();
        Assert.Equal(0, Run(repo, sync, "sync", "--dry-run"));

        Assert.Contains("git add -A", commit.Output);
        Assert.Contains("git commit --quiet -m \"Dry summary\"", commit.Output);
        Assert.Contains("git merge --no-edit feat/a", commit.Output);
        Assert.Contains("git branch --no-track feat/b origin/main", newBranch.Output);
        Assert.Contains("git merge --no-edit origin/dev", sync.Output);
        Assert.Equal(refsBefore, repo.Git.Run("show-ref"));
        Assert.False(repo.Git.HasStagedChanges());
        Assert.Equal("README.md", repo.Git.Run("diff", "--name-only"));
    }

    [Fact]
    public void Status_RendersBranchTable()
    {
        using var repo = new TestRepo();
        NewBranchOperation.Run(repo.Git, repo.Config, "feat/a", repo.Reporter);
        var console = new TestConsole();
        console.Profile.Width = 200;

        var exitCode = Run(repo, console, "status");

        Assert.Equal(0, exitCode);
        Assert.Contains("up to date with origin/dev", console.Output);
        Assert.Contains("feat/a", console.Output);
        Assert.Contains("not pushed", console.Output);
    }

    [Fact]
    public void Pr_PushesBranchAndPrintsUrlFromTemplate()
    {
        using var repo = new TestRepo();
        NewBranchOperation.Run(repo.Git, repo.Config, "feat/a", repo.Reporter);
        repo.Git.Run("config", PullRequestUrl.TemplateKey, "https://example.com/pr?from={branch}&to={target}");
        var console = new TestConsole();
        console.Profile.Width = 200;

        var exitCode = Run(repo, console, "pr", "feat/a", "--print");

        Assert.Equal(0, exitCode);
        Assert.Contains("https://example.com/pr?from=feat%2Fa&to=main", console.Output);
        Assert.True(repo.Git.RemoteBranchExists("origin", "feat/a"));
    }
}
