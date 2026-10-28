namespace GitPa;

public sealed record CommitRequest(string Target, string Summary, string? Description, bool Push);

/// <summary>
/// Commits the staged changes on dev to a feature branch, then merges that branch back into dev.
/// The feature branch is checked out in a temporary worktree, so the user's working tree never
/// switches branches.
/// </summary>
public static class CommitOperation
{
    public static void EnsureOnDev(GitRunner git, PaConfig config) => DevBranch.EnsureCheckedOut(git, config);

    /// <summary>Returns the short hash of the new commit on the target branch.</summary>
    public static string Run(GitRunner git, PaConfig config, CommitRequest request, IReporter reporter)
    {
        var target = request.Target;
        Validate(git, config, request);
        Fetch(git, config, target, reporter);
        Branches.EnsureLocalFeature(git, config, target);

        // Merge the latest remote dev too, so the push afterwards isn't rejected.
        var incoming = new List<string> { target };
        if (git.RemoteBranchExists(config.Remote, config.Dev))
            incoming.Insert(0, $"{config.Remote}/{config.Dev}");
        DevBranch.CheckUntrackedCollisions(git, config, incoming);

        var sha = CommitInWorktree(git, config, request, reporter);
        reporter.Step($"Committed {sha} to {target}");

        DevBranch.Merge(git, incoming, DevBranch.Restore.UnstagedOnly,
            $"Committed {sha} to '{target}' (that part is done). ", reporter);
        reporter.Step($"Merged {target} into {config.Dev}");

        if (request.Push)
            Push(git, config, target, reporter);
        return sha;
    }

    /// <summary>
    /// Validates the request and returns the git commands <see cref="Run"/> would execute.
    /// With <paramref name="stageAll"/>, everything is staged first, so nothing needs to be staged yet.
    /// </summary>
    public static IReadOnlyList<string> Plan(GitRunner git, PaConfig config, CommitRequest request, bool stageAll)
    {
        Validate(git, config, request, requireStaged: !stageAll);
        var (target, remote, dev) = (request.Target, config.Remote, config.Dev);
        if (!git.LocalBranchExists(target) && !git.RemoteBranchExists(remote, target))
            throw new GitPaException($"Branch '{target}' does not exist. Create it with: git pa new {target}");

        var worktree = TempWorktree.DisplayPathFor(git, target);
        var hasRemote = git.RemoteExists(remote);
        var steps = new List<string>();
        if (stageAll)
            steps.Add(GitRunner.Format("add", "-A"));
        if (hasRemote)
        {
            steps.Add(GitRunner.Format("fetch", remote, target));
            steps.Add(GitRunner.Format("fetch", remote, dev));
        }
        steps.Add(GitRunner.Format("worktree", "add", worktree, target));
        if (git.RemoteBranchExists(remote, target))
            steps.Add("  in worktree: " + GitRunner.Format("merge", "--ff-only", $"{remote}/{target}"));
        steps.Add("  in worktree: git apply --3way --index <your staged changes>");
        steps.Add("  in worktree: " + GitRunner.Format(CommitArgs(request)));
        steps.Add(GitRunner.Format("worktree", "remove", worktree));
        steps.Add("git stash push");
        if (git.RemoteBranchExists(remote, dev))
            steps.Add(GitRunner.Format("merge", "--no-edit", $"{remote}/{dev}") + "   (if behind)");
        steps.Add(GitRunner.Format("merge", "--no-edit", target));
        steps.Add("git stash apply && git stash drop   (brings back unstaged changes, if any)");
        if (request.Push && hasRemote)
        {
            steps.Add(GitRunner.Format("push", "-u", remote, target));
            steps.Add(GitRunner.Format("push", remote, dev));
        }
        return steps;
    }

    static void Validate(GitRunner git, PaConfig config, CommitRequest request, bool requireStaged = true)
    {
        DevBranch.EnsureCheckedOut(git, config);
        if (request.Target == config.Main || request.Target == config.Dev)
            throw new GitPaException($"Commit to a feature branch, not '{request.Target}'.");
        if (string.IsNullOrWhiteSpace(request.Summary))
            throw new GitPaException("A commit summary is required.");
        if (requireStaged && !git.HasStagedChanges())
            throw new GitPaException("Nothing is staged. Stage the changes for this commit with `git add` (or use --all).");
    }

    static void Fetch(GitRunner git, PaConfig config, string target, IReporter reporter)
    {
        if (!git.RemoteExists(config.Remote))
            return;

        reporter.Step($"Fetching {target} and {config.Dev} from {config.Remote}");
        Branches.TryFetch(git, config, target);
        var dev = git.TryRun("fetch", "--quiet", config.Remote, config.Dev);
        if (!dev.Success)
            reporter.Warn($"Could not fetch {config.Remote}/{config.Dev}; using the last known version.\n{dev.Message}");
    }

    static string[] CommitArgs(CommitRequest request) =>
        string.IsNullOrWhiteSpace(request.Description)
            ? ["commit", "--quiet", "-m", request.Summary]
            : ["commit", "--quiet", "-m", request.Summary, "-m", request.Description];

    static string CommitInWorktree(GitRunner git, PaConfig config, CommitRequest request, IReporter reporter)
    {
        var target = request.Target;
        var patch = TempWorktree.PathFor(git, target) + ".patch";
        Directory.CreateDirectory(Path.GetDirectoryName(patch)!);

        // Explicit options so user diff settings (noprefix, external diff, textconv) can't break git apply.
        git.Run("diff", "--cached", "--binary", "--no-color", "--no-ext-diff", "--no-textconv",
            "--src-prefix=a/", "--dst-prefix=b/", $"--output={patch}");

        try
        {
            reporter.Step($"Checking out {target} in a temporary worktree");
            using var worktree = TempWorktree.Create(git, target, target);

            if (git.RemoteBranchExists(config.Remote, target))
                worktree.FastForwardTo($"{config.Remote}/{target}");

            var apply = worktree.Git.TryRun("apply", "--3way", "--index", patch);
            if (!apply.Success)
                throw new GitPaException(
                    $"Your staged changes don't apply cleanly to '{target}'. They probably depend on code that is " +
                    $"on {config.Dev} but not on {target}. Nothing was committed.\n{apply.Message}");

            var commit = worktree.Git.TryRun(CommitArgs(request));
            if (!commit.Success)
                throw new GitPaException($"Committing to '{target}' failed. Nothing was committed.\n{commit.Message}");

            return worktree.Git.Run("rev-parse", "--short", "HEAD");
        }
        finally
        {
            File.Delete(patch);
        }
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
                $"Fix the problem (for example with git pa sync) and then run: git push {config.Remote} {string.Join(' ', failed)}");
    }
}
