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

    /// <summary>Throws if merging <paramref name="refs"/> would overwrite untracked files.</summary>
    public static void CheckUntrackedCollisions(GitRunner git, PaConfig config, IEnumerable<string> refs)
    {
        var untracked = git.RunNul("ls-files", "--others", "--exclude-standard", "-z").ToHashSet();
        if (untracked.Count == 0)
            return;

        var collisions = refs
            .SelectMany(r => git.RunNul("diff", "--name-only", "-z", "--no-renames", "--diff-filter=A", $"HEAD...{r}"))
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
        if (!keepInStash)
        {
            git.Run("stash", "drop", "--quiet");
            return;
        }

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
