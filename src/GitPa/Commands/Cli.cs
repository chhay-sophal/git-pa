using System.CommandLine;
using Spectre.Console;

namespace GitPa.Commands;

/// <summary>The console and working directory the command handlers use, injectable for tests.</summary>
/// <param name="readLine">
/// Reads a line of typed text with the terminal's own line editing (arrow keys, Home/End, history), like
/// <see cref="System.Console.ReadLine"/>. Null (in tests) uses Spectre's prompt, which reads scripted input.
/// </param>
public sealed class Cli(IAnsiConsole console, string workingDirectory, Func<string?>? readLine = null)
{
    public IAnsiConsole Console { get; } = console;

    /// <summary>
    /// Asks for a line of text. <paramref name="validate"/> returns an error message, or null when the answer is
    /// fine. An empty answer gives <paramref name="defaultValue"/>, or is asked again unless <paramref name="allowEmpty"/>.
    /// </summary>
    public string Ask(string question, string? defaultValue = null, bool allowEmpty = false, Func<string, string?>? validate = null)
    {
        if (readLine is null)
        {
            var prompt = new TextPrompt<string>(question)
                .Validate(answer => validate?.Invoke(answer) is { } error ? ValidationResult.Error(error) : ValidationResult.Success());
            if (defaultValue is not null)
                prompt.DefaultValue(defaultValue);
            if (allowEmpty)
                prompt.AllowEmpty();
            return Console.Prompt(prompt);
        }

        var shown = defaultValue is null ? question : $"{question.TrimEnd(':', ' ')} [green]({Markup.Escape(defaultValue)})[/]:";
        while (true)
        {
            Console.Markup(shown + " ");
            var answer = readLine() ?? throw new GitPaException("No answer was given (the input ended).");
            if (answer.Length == 0 && defaultValue is not null)
                answer = defaultValue;
            else if (string.IsNullOrWhiteSpace(answer) && !allowEmpty)
                continue;
            if (validate?.Invoke(answer) is { } error)
            {
                Console.MarkupLine($"[red]{Markup.Escape(error)}[/]");
                continue;
            }
            return answer;
        }
    }

    public string WorkingDirectory { get; } = workingDirectory;

    public bool Interactive => Console.Profile.Capabilities.Interactive;

    public IReporter Reporter { get; } = new ConsoleReporter(console);

    public GitRunner OpenRepository() => GitRunner.OpenRepository(WorkingDirectory);

    /// <summary>Opens conflicts in an editor and waits for them to be resolved; null (fail instead) when not interactive.</summary>
    public IConflictResolver? ConflictResolver(GitRunner git) => Interactive ? EditorConflictResolver.For(this, git) : null;

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

    public static Option<bool> DryRunOption(string description = "Show the git commands that would run, without changing anything") =>
        new("--dry-run") { Description = description };

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
