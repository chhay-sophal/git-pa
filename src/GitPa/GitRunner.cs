using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace GitPa;

public sealed record GitResult(int ExitCode, string Output, string Error)
{
    public bool Success => ExitCode == 0;

    /// <summary>The most useful text to show the user when the command failed.</summary>
    public string Message => string.IsNullOrEmpty(Error) ? Output : Error;
}

/// <summary>Runs git commands in a fixed working directory.</summary>
public sealed class GitRunner(string workingDirectory)
{
    public string WorkingDirectory { get; } = workingDirectory;

    /// <summary>Returns a runner for the top level of the repository containing <paramref name="directory"/>.</summary>
    public static GitRunner OpenRepository(string directory)
    {
        var result = new GitRunner(directory).TryRun("rev-parse", "--show-toplevel");
        if (!result.Success)
            throw new GitPaException("Not inside a git repository.");
        return new GitRunner(Path.GetFullPath(result.Output));
    }

    /// <summary>Runs git and captures its trimmed output, without throwing on failure.</summary>
    public GitResult TryRun(params string[] args) => Capture(args, trim: true);

    /// <summary>Runs a git command that prints NUL-separated (-z) output and returns the entries.</summary>
    public IReadOnlyList<string> RunNul(params string[] args)
    {
        var result = Capture(args, trim: false);
        if (!result.Success)
            throw new GitPaException($"git {string.Join(' ', args)} failed:\n{result.Message.Trim()}");
        return result.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>Formats a git command for display, quoting arguments that need it.</summary>
    public static string Format(params string[] args) =>
        "git " + string.Join(' ', args.Select(a =>
            a.Length > 0 && !a.Any(c => char.IsWhiteSpace(c) || c is '"' or '\'') ? a : $"\"{a.Replace("\"", "\\\"")}\""));

    /// <summary>Runs git and returns its trimmed output, throwing if it fails.</summary>
    public string Run(params string[] args)
    {
        var result = TryRun(args);
        if (!result.Success)
            throw new GitPaException($"git {string.Join(' ', args)} failed:\n{result.Message}");
        return result.Output;
    }

    /// <summary>Runs git attached to the console, so the user sees progress and credential prompts.</summary>
    public int RunAttached(params string[] args)
    {
        using var process = Start(args, capture: false);
        process.WaitForExit();
        return process.ExitCode;
    }

    public bool Succeeds(params string[] args) => TryRun(args).Success;

    /// <summary>The checked-out branch, or null when HEAD is detached.</summary>
    public string? CurrentBranch()
    {
        var result = TryRun("symbolic-ref", "--short", "-q", "HEAD");
        return result.Success ? result.Output : null;
    }

    public bool LocalBranchExists(string name) =>
        Succeeds("show-ref", "--verify", "--quiet", $"refs/heads/{name}");

    public bool RemoteBranchExists(string remote, string name) =>
        Succeeds("show-ref", "--verify", "--quiet", $"refs/remotes/{remote}/{name}");

    public bool RemoteExists(string remote) => Succeeds("remote", "get-url", remote);

    public bool IsValidBranchName(string name) => Succeeds("check-ref-format", "--branch", name);

    public bool HasStagedChanges() => !Succeeds("diff", "--cached", "--quiet");

    /// <summary>Local branch names, most recently committed first.</summary>
    public IReadOnlyList<string> LocalBranches() =>
        Run("for-each-ref", "--sort=-committerdate", "--format=%(refname:short)", "refs/heads")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>The .git directory shared by all worktrees of this repository.</summary>
    public string CommonDir() =>
        Path.GetFullPath(Path.Combine(WorkingDirectory, Run("rev-parse", "--git-common-dir")));

    GitResult Capture(string[] args, bool trim)
    {
        using var process = Start(args, capture: true);
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return trim
            ? new GitResult(process.ExitCode, output.Result.Trim(), error.Result.Trim())
            : new GitResult(process.ExitCode, output.Result, error.Result);
    }

    Process Start(string[] args, bool capture)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = capture,
            RedirectStandardError = capture,
        };
        if (capture)
        {
            info.StandardOutputEncoding = Encoding.UTF8;
            info.StandardErrorEncoding = Encoding.UTF8;
        }
        foreach (var arg in args)
            info.ArgumentList.Add(arg);

        try
        {
            return Process.Start(info)!;
        }
        catch (Win32Exception)
        {
            throw new GitPaException("git was not found. Install git and make sure it is on your PATH.");
        }
    }
}
