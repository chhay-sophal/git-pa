namespace GitPa;

/// <summary>Operations on the checked-out dev branch that must not lose the user's work in progress.</summary>
public static class DevBranch
{
    /// <summary>What to put back in the working tree after merging.</summary>
    public enum Restore
    {
        /// <summary>All uncommitted changes, with staged changes still staged.</summary>
        Everything,

        /// <summary>Only unstaged changes; the staged ones arrive through the merge.</summary>
        UnstagedOnly,
    }

    const string StashNote = "Your uncommitted changes are saved in the stash (see git stash list).";

    public static void EnsureCheckedOut(GitRunner git, PaConfig config)
    {
        var current = git.CurrentBranch();
        if (current != config.Dev)
            throw new GitPaException(
                $"You are on '{current ?? "a detached HEAD"}'. Switch to the dev branch first: git switch {config.Dev}");
        if (git.Succeeds("rev-parse", "-q", "--verify", "MERGE_HEAD"))
            throw new GitPaException("A merge is in progress. Finish it with git commit, or cancel it with git merge --abort.");
    }

    /// <summary>Marks the first commit of a dev branch rebuilt by reset-dev, so teammates' git-pa can recognize it.</summary>
    public const string ResetTrailer = "Git-Pa-Reset";

    /// <summary>
    /// Throws if merging <paramref name="refs"/> (or, with <paramref name="replacing"/>, switching HEAD to them)
    /// would overwrite untracked files.
    /// </summary>
    public static void CheckUntrackedCollisions(GitRunner git, PaConfig config, IEnumerable<string> refs, bool replacing = false)
    {
        var untracked = git.RunNul("ls-files", "--others", "--exclude-standard", "-z").ToHashSet();
        if (untracked.Count == 0)
            return;

        var collisions = refs
            .SelectMany(r => git.RunNul(replacing
                ? ["diff", "--name-only", "-z", "--no-renames", "--diff-filter=A", "HEAD", r]
                : ["diff", "--name-only", "-z", "--no-renames", "--diff-filter=A", $"HEAD...{r}"]))
            .Where(untracked.Contains)
            .Distinct()
            .ToList();
        if (collisions.Count > 0)
            throw new GitPaException(
                $"These untracked files would be overwritten by merging into {config.Dev}:\n  {string.Join("\n  ", collisions)}\n" +
                "Move, delete, or commit them first. Nothing was changed.");
    }

    /// <summary>
    /// Sets uncommitted changes aside, merges each ref that isn't already in HEAD, then restores the changes.
    /// On conflicts the merge is left open, and the exception explains how to finish.
    /// </summary>
    public static void Merge(GitRunner git, IReadOnlyList<string> refs, Restore restore, string failurePrefix, IReporter reporter)
    {
        var pending = refs.Where(r => !git.Succeeds("merge-base", "--is-ancestor", r, "HEAD")).ToList();
        if (pending.Count == 0)
            return;

        var dirty = git.Run("status", "--porcelain", "--untracked-files=no").Length > 0;
        var keepInStash = restore == Restore.Everything ? dirty : !git.Succeeds("diff", "--quiet");
        if (dirty)
            git.Run("stash", "push", "--quiet", "-m", "git-pa: work in progress");

        for (var i = 0; i < pending.Count; i++)
        {
            reporter.Step($"Merging {pending[i]}");
            var merge = git.TryRun("merge", "--no-edit", pending[i]);
            if (merge.Success)
                continue;

            var conflicts = git.Run("diff", "--name-only", "--diff-filter=U");
            if (conflicts.Length == 0)
            {
                git.TryRun("merge", "--abort");
                var restored = !dirty || git.TryRun("stash", "pop", "--index").Success;
                throw new GitPaException(
                    $"{failurePrefix}Merging {pending[i]} failed:\n{merge.Message}\n" +
                    (restored ? "Your working tree is back as it was." : StashNote));
            }

            if (dirty && !keepInStash)
                git.Run("stash", "drop", "--quiet");

            var steps = new List<string> { "Resolve the conflicts, then run: git add <files> && git commit --no-edit" };
            steps.AddRange(pending.Skip(i + 1).Select(r => GitRunner.Format("merge", "--no-edit", r)));
            if (keepInStash)
                steps.Add(restore == Restore.Everything
                    ? "git stash pop --index   (brings back your uncommitted changes)"
                    : "git stash pop   (brings back your unstaged changes)");
            throw new GitPaException(
                $"{failurePrefix}Merging {pending[i]} stopped with conflicts in:\n  {conflicts.Replace("\n", "\n  ")}\n" +
                "The merge is left open so you can resolve it. Then:\n" +
                string.Join('\n', steps.Select((s, n) => $"  {n + 1}. {s}")));
        }

        if (!dirty)
            return;
        if (keepInStash)
            RestoreStash(git, restore, reporter);
        else
            git.Run("stash", "drop", "--quiet");
    }

    /// <summary>True when the remote dev was rebuilt by reset-dev since this dev last synced with it.</summary>
    public static bool WasReset(GitRunner git, string upstream) =>
        !git.Succeeds("merge-base", "--is-ancestor", "HEAD", upstream)
        && git.Run("log", "--format=%H", $"--grep=^{ResetTrailer}:", $"HEAD..{upstream}").Length > 0;

    /// <summary>Commits on the current dev that are on no other branch, local or remote.</summary>
    public static IReadOnlyList<string> LocalOnlyCommits(GitRunner git, PaConfig config, string newTip)
    {
        var others = git.LocalBranches().Where(b => b != config.Dev).Select(b => $"refs/heads/{b}");
        var log = git.Run(["log", "--no-merges", "--format=%h %s", "HEAD", "--not", newTip, "--remotes", .. others]);
        return log.Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>
    /// Points the current dev at <paramref name="newTip"/>, keeping uncommitted changes, and saves the
    /// old dev under refs/pa-backup. Returns the backup ref.
    /// </summary>
    public static string MoveTo(GitRunner git, PaConfig config, string newTip, IReporter reporter)
    {
        CheckUntrackedCollisions(git, config, [newTip], replacing: true);
        var backup = $"refs/pa-backup/{config.Dev}-{DateTime.Now:yyyyMMdd-HHmmss}";
        git.Run("update-ref", backup, "HEAD");

        var dirty = git.Run("status", "--porcelain", "--untracked-files=no").Length > 0;
        if (dirty)
            git.Run("stash", "push", "--quiet", "-m", "git-pa: work in progress");
        git.Run("reset", "--quiet", "--hard", newTip);
        if (dirty)
            RestoreStash(git, Restore.Everything, reporter);
        return backup;
    }

    /// <summary>
    /// If the remote dev was rebuilt by reset-dev, moves the local dev onto it.
    /// Returns the backup ref of the old dev, or null when nothing was reset.
    /// </summary>
    public static string? FollowReset(GitRunner git, PaConfig config, IReporter reporter)
    {
        var upstream = $"{config.Remote}/{config.Dev}";
        if (!git.RemoteBranchExists(config.Remote, config.Dev) || !WasReset(git, upstream))
            return null;

        var lost = LocalOnlyCommits(git, config, upstream);
        var backup = MoveTo(git, config, upstream, reporter);
        reporter.Warn(
            $"{upstream} was rebuilt with git pa reset-dev, so your {config.Dev} now matches it. " +
            $"The old one is saved as {backup}." +
            (lost.Count == 0 ? "" :
                $"\nThese commits were only on your old {config.Dev} and are not in the new one:\n  {string.Join("\n  ", lost)}"));
        return backup;
    }

    static void RestoreStash(GitRunner git, Restore restore, IReporter reporter)
    {
        var apply = restore == Restore.Everything
            ? git.TryRun("stash", "apply", "--index")
            : git.TryRun("stash", "apply");
        if (!apply.Success && restore == Restore.Everything && git.Run("diff", "--name-only", "--diff-filter=U").Length == 0)
            apply = git.TryRun("stash", "apply"); // staging couldn't be restored; keep the changes at least

        if (apply.Success)
            git.Run("stash", "drop", "--quiet");
        else
            reporter.Warn($"Your uncommitted changes could not be restored cleanly. {StashNote}");
    }
}
