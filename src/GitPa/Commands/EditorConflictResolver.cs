using System.ComponentModel;
using System.Diagnostics;
using Spectre.Console;

namespace GitPa.Commands;

/// <summary>
/// Opens the conflicted files in an editor (VS Code by default, or pa.editor) and waits in the terminal
/// until they are resolved, or the user cancels.
/// </summary>
public sealed class EditorConflictResolver(Cli cli, string editor) : IConflictResolver
{
    public const string EditorKey = "pa.editor";
    public const string DefaultEditor = "code";

    public static EditorConflictResolver For(Cli cli, GitRunner git) =>
        new(cli, PaConfig.Get(git, EditorKey) is { Length: > 0 } configured ? configured : DefaultEditor);

    public bool Resolve(TempWorktree worktree, Conflict conflict)
    {
        var console = cli.Console;
        console.MarkupLine($"[yellow]conflict:[/] {Markup.Escape(conflict.Summary)}");
        foreach (var file in conflict.Files)
            console.MarkupLine($"  [bold]{Markup.Escape(file)}[/]");
        console.MarkupLine(Markup.Escape(conflict.Guidance));

        var opened = Open(worktree.Path, conflict.Files);
        console.MarkupLine(opened
            ? $"Opened them in a new [bold]{Markup.Escape(editor)}[/] window. Resolve them, save, and close that window."
            : $"Could not start '{Markup.Escape(editor)}' (set another editor with git config {EditorKey} <command>). " +
              $"Open this folder to resolve them:\n  {Markup.Escape(worktree.Path)}");

        var remaining = conflict.Files;
        while (true)
        {
            var answer = console.Prompt(new TextPrompt<string>(
                    "Press [bold]Enter[/] when they're resolved, or type [bold]a[/] to cancel:")
                .AllowEmpty());
            if (answer.Trim().ToLowerInvariant() is "a" or "abort" or "cancel")
                return false;

            remaining = Conflicts.StageResolved(worktree.Git, remaining);
            if (remaining.Count == 0)
            {
                cli.Success("Conflicts resolved.");
                return true;
            }
            console.MarkupLine("[yellow]Still has conflict markers (<<<<<<< or >>>>>>>):[/]");
            foreach (var file in remaining)
                console.MarkupLine($"  [bold]{Markup.Escape(file)}[/]");
        }
    }

    /// <summary>Opens the worktree folder and the files in a new editor window; false when the editor can't start.</summary>
    bool Open(string folder, IEnumerable<string> files)
    {
        var parts = editor.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string[] args = [.. parts.Skip(1), .. IsVsCodeLike(parts[0]) ? ["--new-window"] : Array.Empty<string>(),
            folder, .. files.Select(f => Path.Combine(folder, f))];

        // On Windows, `code` is a .cmd script, which only cmd.exe can start.
        var start = OperatingSystem.IsWindows() ? new ProcessStartInfo("cmd.exe") : new ProcessStartInfo(parts[0]);
        if (OperatingSystem.IsWindows())
        {
            start.ArgumentList.Add("/c");
            start.ArgumentList.Add(parts[0]);
        }
        foreach (var arg in args)
            start.ArgumentList.Add(arg);
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;

        try
        {
            using var process = Process.Start(start);
            if (process is null)
                return false;
            // Editor launchers like code return right away; still running after that counts as started.
            return !process.WaitForExit(TimeSpan.FromSeconds(10)) || process.ExitCode == 0;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    static bool IsVsCodeLike(string command) =>
        Path.GetFileNameWithoutExtension(command).ToLowerInvariant() is "code" or "code-insiders" or "cursor" or "codium" or "windsurf";
}
