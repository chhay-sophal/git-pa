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
    public void New_FailsWithoutNameWhenNotInteractive()
    {
        using var repo = new TestRepo();
        var console = new TestConsole();

        var exitCode = Run(repo, console, "new");

        Assert.Equal(1, exitCode);
        Assert.Contains("Missing branch name", console.Output);
    }
}
