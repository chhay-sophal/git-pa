using System.Text.RegularExpressions;

namespace GitPa;

/// <summary>A temporary worktree under .git/pa-worktrees, so a branch can be changed without switching to it.</summary>
public sealed class TempWorktree : IDisposable
{
    readonly GitRunner owner;

    TempWorktree(GitRunner owner, string path)
    {
        this.owner = owner;
        Path = path;
        Git = new GitRunner(path);
    }

    public string Path { get; }

    /// <summary>Runs git inside the worktree.</summary>
    public GitRunner Git { get; }

    public static string PathFor(GitRunner git, string name) =>
        System.IO.Path.Combine(git.CommonDir(), "pa-worktrees", Regex.Replace(name, @"[^\w.-]", "_"));

    /// <summary>The worktree path relative to the repository, for display.</summary>
    public static string DisplayPathFor(GitRunner git, string name) =>
        System.IO.Path.GetRelativePath(git.WorkingDirectory, PathFor(git, name)).Replace('\\', '/');

    /// <summary>Checks out <paramref name="reference"/>: a branch, or any commit when <paramref name="detach"/> is set.</summary>
    public static TempWorktree Create(GitRunner git, string name, string reference, bool detach = false)
    {
        var path = PathFor(git, name);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);

        // Clean up after an earlier run that was interrupted.
        git.TryRun("worktree", "prune");
        if (Directory.Exists(path))
        {
            git.TryRun("worktree", "remove", "--force", path);
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }

        var add = git.TryRun(detach
            ? ["worktree", "add", "--quiet", "--detach", path, reference]
            : ["worktree", "add", "--quiet", path, reference]);
        if (!add.Success)
            throw new GitPaException(
                $"Could not check out '{reference}' in a temporary worktree. Is it checked out somewhere else?\n{add.Message}");
        return new TempWorktree(git, path);
    }

    /// <summary>Fast-forwards the checked-out branch to <paramref name="upstream"/>, failing if they have diverged.</summary>
    public void FastForwardTo(string upstream)
    {
        var fastForward = Git.TryRun("merge", "--ff-only", "--quiet", upstream);
        if (!fastForward.Success)
            throw new GitPaException(
                $"The branch has diverged from {upstream}. Reconcile the two before continuing.\n{fastForward.Message}");
    }

    public void Dispose() => owner.TryRun("worktree", "remove", "--force", Path);
}
