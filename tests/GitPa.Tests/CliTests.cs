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
    public void New_PromptsForBranchName()
    {
        using var repo = new TestRepo();
        var console = InteractiveConsole();
        console.Input.PushTextWithEnter("feat/prompted");

        var exitCode = Run(repo, console, "new");

        Assert.Equal(0, exitCode);
        Assert.True(repo.Git.LocalBranchExists("feat/prompted"));
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

    [Fact]
    public void New_FailsWithoutNameWhenNotInteractive()
    {
        using var repo = new TestRepo();
        var console = new TestConsole();

        var exitCode = Run(repo, console, "new");

        Assert.Equal(1, exitCode);
        Assert.Contains("Missing branch name", console.Output);
    }
}
