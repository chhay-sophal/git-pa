using System.CommandLine;
using Spectre.Console;

namespace GitPa.Commands;

/// <summary>The console and working directory the command handlers use, injectable for tests.</summary>
public sealed class Cli(IAnsiConsole console, string workingDirectory)
{
    public IAnsiConsole Console { get; } = console;

    public string WorkingDirectory { get; } = workingDirectory;

    public bool Interactive => Console.Profile.Capabilities.Interactive;

    public IReporter Reporter { get; } = new ConsoleReporter(console);

    public GitRunner OpenRepository() => GitRunner.OpenRepository(WorkingDirectory);

    /// <summary>Runs a handler, turning expected failures into a red message and exit code 1.</summary>
    public int Execute(Func<int> handler)
    {
        try
        {
            return handler();
        }
        catch (GitPaException e)
        {
            Console.MarkupLine($"[red]error:[/] {Markup.Escape(e.Message)}");
            return 1;
        }
    }

    /// <summary>Uses the given value, or asks for it when running interactively.</summary>
    public string Require(string? value, string option, Func<string> prompt)
    {
        if (!string.IsNullOrWhiteSpace(value))
            return value;
        if (!Interactive)
            throw new GitPaException($"Missing {option}.");
        return prompt();
    }

    public static Option<bool> DryRunOption() =>
        new("--dry-run") { Description = "Show the git commands that would run, without changing anything" };

    public int PrintPlan(IReadOnlyList<string> steps)
    {
        Console.MarkupLine("[yellow]Dry run[/], nothing was changed. These steps would run:");
        for (var i = 0; i < steps.Count; i++)
            Console.MarkupLine($"  [grey]{i + 1}.[/] {Markup.Escape(steps[i])}");
        return 0;
    }

    public void Success(string message) =>
        Console.MarkupLine($"[green]✓[/] {Markup.Escape(message)}");

    sealed class ConsoleReporter(IAnsiConsole console) : IReporter
    {
        public void Step(string message) => console.MarkupLine($"[grey]› {Markup.Escape(message)}[/]");

        public void Warn(string message) => console.MarkupLine($"[yellow]warning:[/] {Markup.Escape(message)}");
    }
}
