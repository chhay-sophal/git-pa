using System.Text.RegularExpressions;

namespace GitPa;

public sealed record CommitRequest(string Target, string Summary, string? Description, bool Push);

/// <summary>
/// Commits the staged changes on dev to a feature branch, then merges that branch back into dev.
/// The feature branch is checked out in a temporary worktree, so the user's working tree never
/// switches branches.
/// </summary>
public static class CommitOperation
{
    public static void EnsureOnDev(GitRunner git, PaConfig config)
    {
        var current = git.CurrentBranch();
        if (current != config.Dev)
            throw new GitPaException(
                $"You are on '{current ?? "a detached HEAD"}'. Switch to the dev branch first: git switch {config.Dev}");
    }

    /// <summary>Returns the short hash of the new commit on the target branch.</summary>
    public static string Run(GitRunner git, PaConfig config, CommitRequest request, IReporter reporter)
    {
        var target = request.Target;
        EnsureOnDev(git, config);
        if (target == config.Main || target == config.Dev)
            throw new GitPaException($"Commit to a feature branch, not '{target}'.");
        if (string.IsNullOrWhiteSpace(request.Summary))
            throw new GitPaException("A commit summary is required.");
        if (!git.HasStagedChanges())
            throw new GitPaException("Nothing is staged. Stage the changes for this commit with `git add` (or use --all).");

        EnsureLocalBranch(git, config, target, reporter);

        var sha = CommitInWorktree(git, config, request, reporter);
        reporter.Step($"Committed {sha} to {target}");

        MergeIntoDev(git, config, target, reporter);
        reporter.Step($"Merged {target} into {config.Dev}");

        if (request.Push)
            Push(git, config, target, reporter);
        return sha;
    }

    static void EnsureLocalBranch(GitRunner git, PaConfig config, string target, IReporter reporter)
    {
        if (git.RemoteExists(config.Remote))
        {
            reporter.Step($"Fetching {config.Remote}/{target}");
            // Fails harmlessly when the branch has never been pushed.
            git.TryRun("fetch", "--quiet", config.Remote, target);
        }

        if (git.LocalBranchExists(target))
            return;
        if (!git.RemoteBranchExists(config.Remote, target))
            throw new GitPaException($"Branch '{target}' does not exist. Create it with: git pa new {target}");
        git.Run("branch", "--quiet", "--track", target, $"{config.Remote}/{target}");
    }

    static string CommitInWorktree(GitRunner git, PaConfig config, CommitRequest request, IReporter reporter)
    {
        var target = request.Target;
        var workDir = Path.Combine(git.CommonDir(), "pa-worktrees");
        Directory.CreateDirectory(workDir);
        var path = Path.Combine(workDir, Regex.Replace(target, @"[^\w.-]", "_"));
        var patch = path + ".patch";

        // Clean up after an earlier run that was interrupted.
        git.TryRun("worktree", "prune");
        if (Directory.Exists(path))
        {
            git.TryRun("worktree", "remove", "--force", path);
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }

        // Explicit options so user diff settings (noprefix, external diff, textconv) can't break git apply.
        git.Run("diff", "--cached", "--binary", "--no-color", "--no-ext-diff", "--no-textconv",
            "--src-prefix=a/", "--dst-prefix=b/", $"--output={patch}");

        reporter.Step($"Checking out {target} in a temporary worktree");
        var add = git.TryRun("worktree", "add", "--quiet", path, target);
        if (!add.Success)
            throw new GitPaException(
                $"Could not check out '{target}' in a temporary worktree. Is it checked out somewhere else?\n{add.Message}");

        try
        {
            var worktree = new GitRunner(path);

            if (git.RemoteBranchExists(config.Remote, target))
            {
                var upstream = $"{config.Remote}/{target}";
                var fastForward = worktree.TryRun("merge", "--ff-only", "--quiet", upstream);
                if (!fastForward.Success)
                    throw new GitPaException(
                        $"'{target}' has diverged from {upstream}. Reconcile the two before committing.\n{fastForward.Message}");
            }

            var apply = worktree.TryRun("apply", "--3way", "--index", patch);
            if (!apply.Success)
                throw new GitPaException(
                    $"Your staged changes don't apply cleanly to '{target}'. They probably depend on code that is " +
                    $"on {config.Dev} but not on {target}. Nothing was committed.\n{apply.Message}");

            var commitArgs = new List<string> { "commit", "--quiet", "-m", request.Summary };
            if (!string.IsNullOrWhiteSpace(request.Description))
                commitArgs.AddRange(["-m", request.Description]);
            var commit = worktree.TryRun([.. commitArgs]);
            if (!commit.Success)
                throw new GitPaException($"Committing to '{target}' failed. Nothing was committed.\n{commit.Message}");

            return worktree.Run("rev-parse", "--short", "HEAD");
        }
        finally
        {
            git.TryRun("worktree", "remove", "--force", path);
            File.Delete(patch);
        }
    }

    static void MergeIntoDev(GitRunner git, PaConfig config, string target, IReporter reporter)
    {
        // The staged changes now arrive through the merge, so set the working tree aside first.
        // Unstaged changes come back afterwards.
        var hasUnstaged = !git.Succeeds("diff", "--quiet");
        git.Run("stash", "push", "--quiet", "-m", $"git-pa: changes committed to {target}");

        var merge = git.TryRun("merge", "--no-edit", target);
        if (!merge.Success)
        {
            git.TryRun("merge", "--abort");
            var restored = git.TryRun("stash", "pop", "--index").Success;
            throw new GitPaException(
                $"Committed to '{target}', but merging it into '{config.Dev}' failed:\n{merge.Message}\n" +
                (restored
                    ? "Your working tree is back as it was. Since the commit is already on the feature branch, " +
                      $"discard the staged changes and run: git merge {target}"
                    : "Your changes are saved in the stash (see git stash list)."));
        }

        if (!hasUnstaged)
        {
            git.Run("stash", "drop", "--quiet");
            return;
        }

        if (git.TryRun("stash", "apply", "--quiet").Success)
            git.Run("stash", "drop", "--quiet");
        else
            reporter.Warn("Your unstaged changes could not be restored cleanly. They are saved in the stash (see git stash list).");
    }

    static void Push(GitRunner git, PaConfig config, string target, IReporter reporter)
    {
        if (!git.RemoteExists(config.Remote))
        {
            reporter.Warn($"No remote named '{config.Remote}'; skipping push.");
            return;
        }

        reporter.Step($"Pushing {target} and {config.Dev} to {config.Remote}");
        var failed = new List<string>();
        if (git.RunAttached("push", "--quiet", "-u", config.Remote, target) != 0)
            failed.Add(target);
        if (git.RunAttached("push", "--quiet", config.Remote, config.Dev) != 0)
            failed.Add(config.Dev);

        if (failed.Count > 0)
            reporter.Warn(
                $"Push failed for {string.Join(" and ", failed)}. Your commit is safe locally. " +
                $"Fix the problem (for example with git pull) and then run: git push {config.Remote} {string.Join(' ', failed)}");
    }
}
