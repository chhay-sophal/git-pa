namespace GitPa;

/// <param name="Base">The feature branch this one builds on, or null when it's based on main.</param>
/// <param name="FromBase">Commits merged in from the base.</param>
/// <param name="FromMain">Commits merged in from main.</param>
public sealed record UpdateResult(string? Base, int FromBase, int FromMain)
{
    public bool UpToDate => FromBase == 0 && FromMain == 0;
}

/// <summary>
/// Merges the latest main into a feature branch in a temporary worktree, without switching to it. A stacked
/// branch first gets the latest of the branch it builds on, so it keeps up with that branch's changes.
/// </summary>
public static class UpdateOperation
{
    public static UpdateResult Run(GitRunner git, PaConfig config, string branch, bool push, IReporter reporter)
    {
        Branches.EnsureFeature(config, branch);
        var savedBase = Stack.SavedBase(git, branch);
        if (git.RemoteExists(config.Remote))
        {
            reporter.Step($"Fetching {config.Main} and {branch} from {config.Remote}");
            git.TryRun("fetch", "--quiet", config.Remote, config.Main);
            Branches.TryFetch(git, config, branch);
            if (savedBase is not null)
                Branches.TryFetch(git, config, savedBase);
        }
        Branches.EnsureLocalFeature(git, config, branch);
        var main = Branches.LatestMain(git, config);
        var baseBranch = Stack.Base(git, config, branch);
        var baseRef = baseBranch is null ? null : Branches.Newest(git, config, baseBranch);

        int fromBase = 0, fromMain;
        using (var worktree = TempWorktree.Create(git, branch, branch))
        {
            if (git.RemoteBranchExists(config.Remote, branch))
                worktree.FastForwardTo($"{config.Remote}/{branch}");

            var start = worktree.Git.Run("rev-parse", "HEAD");
            if (baseRef is not null)
                fromBase = Merge(worktree, branch, baseRef, start, reporter);
            fromMain = Merge(worktree, branch, main, start, reporter);
        }

        var result = new UpdateResult(baseBranch, fromBase, fromMain);
        if (!result.UpToDate && push && git.RemoteExists(config.Remote))
        {
            reporter.Step($"Pushing {branch} to {config.Remote}");
            if (git.RunAttached("push", "--quiet", "-u", config.Remote, branch) != 0)
                reporter.Warn($"Push failed. The update is safe locally; push later with: git push {config.Remote} {branch}");
        }
        return result;
    }

    /// <summary>
    /// Merges <paramref name="source"/> into the worktree's branch; returns how many commits it was behind.
    /// On conflicts, puts the branch back at <paramref name="start"/>, undoing earlier merges of this run too.
    /// </summary>
    static int Merge(TempWorktree worktree, string branch, string source, string start, IReporter reporter)
    {
        var behind = int.Parse(worktree.Git.Run("rev-list", "--count", $"HEAD..{source}"));
        if (behind == 0)
            return 0;

        reporter.Step($"Merging {source} into {branch}");
        var merge = worktree.Git.TryRun("merge", "--no-edit", source);
        if (merge.Success)
            return behind;

        worktree.Git.TryRun("merge", "--abort");
        worktree.Git.TryRun("reset", "--hard", "--quiet", start);
        var folder = $"../{branch.Replace('/', '-')}";
        throw new GitPaException(
            $"Merging {source} into '{branch}' has conflicts, so nothing was changed. Resolve it in a separate folder:\n" +
            $"  git worktree add {folder} {branch}\n" +
            $"  cd {folder}\n" +
            $"  git merge {source}      (resolve the conflicts, then git commit)\n" +
            $"  git push\n" +
            $"  cd -  &&  git worktree remove {folder}\n" +
            merge.Message);
    }

    /// <summary>Validates and returns the git commands <see cref="Run"/> would execute.</summary>
    public static IReadOnlyList<string> Plan(GitRunner git, PaConfig config, string branch, bool push)
    {
        Branches.EnsureFeature(config, branch);
        if (!git.LocalBranchExists(branch) && !git.RemoteBranchExists(config.Remote, branch))
            throw new GitPaException($"Branch '{branch}' does not exist.");

        var baseBranch = Stack.Base(git, config, branch);
        var worktree = TempWorktree.DisplayPathFor(git, branch);
        var hasRemote = git.RemoteExists(config.Remote);
        var steps = new List<string>();
        if (hasRemote)
        {
            steps.Add(GitRunner.Format("fetch", config.Remote, config.Main));
            steps.Add(GitRunner.Format("fetch", config.Remote, branch));
            if (baseBranch is not null)
                steps.Add(GitRunner.Format("fetch", config.Remote, baseBranch));
        }
        steps.Add(GitRunner.Format("worktree", "add", worktree, branch));
        if (git.RemoteBranchExists(config.Remote, branch))
            steps.Add("  in worktree: " + GitRunner.Format("merge", "--ff-only", $"{config.Remote}/{branch}"));
        if (baseBranch is not null)
            steps.Add("  in worktree: " + GitRunner.Format("merge", "--no-edit", Branches.Newest(git, config, baseBranch)!) +
                $"   (if behind; {branch} builds on it)");
        steps.Add("  in worktree: " + GitRunner.Format("merge", "--no-edit", Branches.LatestMain(git, config)) + "   (if behind)");
        steps.Add(GitRunner.Format("worktree", "remove", worktree));
        if (push && hasRemote)
            steps.Add(GitRunner.Format("push", "-u", config.Remote, branch));
        return steps;
    }
}
