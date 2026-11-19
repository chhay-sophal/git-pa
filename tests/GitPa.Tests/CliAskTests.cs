using GitPa.Commands;
using Spectre.Console.Testing;

namespace GitPa.Tests;

/// <summary>Cli.Ask with the terminal's own line editing (Console.ReadLine), as in a real terminal.</summary>
public class CliAskTests
{
    static (Cli Cli, TestConsole Console) WithLines(params string?[] lines)
    {
        var console = new TestConsole().Interactive();
        console.Profile.Width = 200;
        var queue = new Queue<string?>(lines);
        return (new Cli(console, Environment.CurrentDirectory, () => queue.Dequeue()), console);
    }

    [Fact]
    public void ReturnsTheTypedLine()
    {
        var (cli, console) = WithLines("Add login page");

        Assert.Equal("Add login page", cli.Ask("Commit summary:"));
        Assert.Contains("Commit summary:", console.Output);
    }

    [Fact]
    public void EmptyAnswerGivesTheDefault_WhichIsShown()
    {
        var (cli, console) = WithLines("");

        Assert.Equal("Release", cli.Ask("Build configuration:", "Release"));
        Assert.Contains("Build configuration (Release):", console.Output);
    }

    [Fact]
    public void AsksAgainUntilTheAnswerIsValid()
    {
        var (cli, console) = WithLines("", "bad..name", "feature/1");

        var answer = cli.Ask("Branch name:", validate: name => name.Contains("..") ? "Not a valid branch name" : null);

        Assert.Equal("feature/1", answer);
        Assert.Contains("Not a valid branch name", console.Output);
    }

    [Fact]
    public void AllowsAnEmptyAnswer_WhenAsked()
    {
        var (cli, _) = WithLines("");

        Assert.Equal("", cli.Ask("Description:", allowEmpty: true));
    }

    [Fact]
    public void FailsWhenTheInputEnds()
    {
        var (cli, _) = WithLines([null]);

        Assert.Throws<GitPaException>(() => cli.Ask("Branch name:"));
    }

    [Fact]
    public void NewCommand_ReadsTheBranchNameThisWay()
    {
        using var repo = new TestRepo();
        var console = new TestConsole().Interactive();
        var lines = new Queue<string?>(["feature/typed"]);
        var cli = new Cli(console, repo.Git.WorkingDirectory, () => lines.Dequeue());

        var exitCode = GitPaCli.Build(cli).Parse(["new"]).Invoke();

        Assert.Equal(0, exitCode);
        Assert.True(repo.Git.LocalBranchExists("feature/typed"));
    }
}
