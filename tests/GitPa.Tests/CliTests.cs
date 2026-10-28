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
    public void New_FailsWithoutNameWhenNotInteractive()
    {
        using var repo = new TestRepo();
        var console = new TestConsole();

        var exitCode = Run(repo, console, "new");

        Assert.Equal(1, exitCode);
        Assert.Contains("Missing branch name", console.Output);
    }
}
