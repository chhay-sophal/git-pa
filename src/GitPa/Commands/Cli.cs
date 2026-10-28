using Spectre.Console;

namespace GitPa.Commands;

/// <summary>Shared console helpers for the command handlers.</summary>
static class Cli
{
    public static bool Interactive =>
        AnsiConsole.Profile.Capabilities.Interactive && !Console.IsInputRedirected;

    public static IReporter Reporter { get; } = new ConsoleReporter();

    /// <summary>Runs a handler, turning expected failures into a red message and exit code 1.</summary>
    public static int Execute(Func<int> handler)
    {
        try
        {
            return handler();
        }
        catch (GitPaException e)
        {
            AnsiConsole.MarkupLine($"[red]error:[/] {Markup.Escape(e.Message)}");
            return 1;
        }
    }

    /// <summary>Uses the given value, or asks for it when running interactively.</summary>
    public static string Require(string? value, string option, Func<string> prompt)
    {
        if (!string.IsNullOrWhiteSpace(value))
            return value;
        if (!Interactive)
            throw new GitPaException($"Missing {option}.");
        return prompt();
    }

    public static void Success(string message) =>
        AnsiConsole.MarkupLine($"[green]✓[/] {Markup.Escape(message)}");

    sealed class ConsoleReporter : IReporter
    {
        public void Step(string message) => AnsiConsole.MarkupLine($"[grey]› {Markup.Escape(message)}[/]");

        public void Warn(string message) => AnsiConsole.MarkupLine($"[yellow]warning:[/] {Markup.Escape(message)}");
    }
}
