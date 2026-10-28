namespace GitPa;

/// <summary>Merges the latest main into a feature branch in a temporary worktree, without switching to it.</summary>
public static class UpdateOperation
{
    /// <summary>Returns the number of main commits merged in; 0 when the branch was already up to date.</summary>
    public static int Run(GitRunner git, PaConfig config, string branch, bool push, IReporter reporter)
    {
        Branches.EnsureFeature(config, branch);
        if (git.RemoteExists(config.Remote))
        {
            reporter.Step($"Fetching {config.Main} and {branch} from {config.Remote}");
            git.TryRun("fetch", "--quiet", config.Remote, config.Main);
            Branches.TryFetch(git, config, branch);
        }
        Branches.EnsureLocalFeature(git, config, branch);
        var main = Branches.LatestMain(git, config);

        int behind;
        using (var worktree = TempWorktree.Create(git, branch, branch))
        {
            if (git.RemoteBranchExists(config.Remote, branch))
                worktree.FastForwardTo($"{config.Remote}/{branch}");

            behind = int.Parse(worktree.Git.Run("rev-list", "--count", $"HEAD..{main}"));
            if (behind == 0)
                return 0;

            reporter.Step($"Merging {main} into {branch}");
            var merge = worktree.Git.TryRun("merge", "--no-edit", main);
            if (!merge.Success)
            {
                worktree.Git.TryRun("merge", "--abort");
                var folder = $"../{branch.Replace('/', '-')}";
                throw new GitPaException(
                    $"Merging {main} into '{branch}' has conflicts, so nothing was changed. Resolve it in a separate folder:\n" +
                    $"  git worktree add {folder} {branch}\n" +
                    $"  cd {folder}\n" +
                    $"  git merge {main}      (resolve the conflicts, then git commit)\n" +
                    $"  git push\n" +
                    $"  cd -  &&  git worktree remove {folder}\n" +
                    merge.Message);
            }
        }

        if (push && git.RemoteExists(config.Remote))
        {
            reporter.Step($"Pushing {branch} to {config.Remote}");
            if (git.RunAttached("push", "--quiet", "-u", config.Remote, branch) != 0)
                reporter.Warn($"Push failed. The update is safe locally; push later with: git push {config.Remote} {branch}");
        }
        return behind;
    }

    /// <summary>Validates and returns the git commands <see cref="Run"/> would execute.</summary>
    public static IReadOnlyList<string> Plan(GitRunner git, PaConfig config, string branch, bool push)
    {
        Branches.EnsureFeature(config, branch);
        if (!git.LocalBranchExists(branch) && !git.RemoteBranchExists(config.Remote, branch))
            throw new GitPaException($"Branch '{branch}' does not exist.");

        var worktree = TempWorktree.DisplayPathFor(git, branch);
        var hasRemote = git.RemoteExists(config.Remote);
        var steps = new List<string>();
        if (hasRemote)
        {
            steps.Add(GitRunner.Format("fetch", config.Remote, config.Main));
            steps.Add(GitRunner.Format("fetch", config.Remote, branch));
        }
        steps.Add(GitRunner.Format("worktree", "add", worktree, branch));
        if (git.RemoteBranchExists(config.Remote, branch))
            steps.Add("  in worktree: " + GitRunner.Format("merge", "--ff-only", $"{config.Remote}/{branch}"));
        steps.Add("  in worktree: " + GitRunner.Format("merge", "--no-edit", Branches.LatestMain(git, config)) + "   (if behind)");
        steps.Add(GitRunner.Format("worktree", "remove", worktree));
        if (push && hasRemote)
            steps.Add(GitRunner.Format("push", "-u", config.Remote, branch));
        return steps;
    }
}
