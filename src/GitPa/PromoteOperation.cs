namespace GitPa;

/// <param name="Stage">The stage branch the feature was merged into.</param>
/// <param name="AlreadyThere">Whether the stage already had all of the feature's work, so nothing changed.</param>
/// <param name="Commits">The feature's own commits (not merges) that were new to the stage.</param>
public sealed record PromoteResult(string Stage, bool AlreadyThere, int Commits);

/// <summary>
/// Merges one feature branch into a stage branch (like uat) in a temporary worktree, so you stay on dev,
/// and only that feature's work reaches the stage, not everything else in dev.
/// </summary>
public static class PromoteOperation
{
    public static PromoteResult Run(GitRunner git, PaConfig config, string branch, string? stage, bool push, IReporter reporter)
    {
        Branches.EnsureFeature(config, branch);
        stage = ResolveStage(config, stage);
        if (git.CurrentBranch() == stage)
            throw new GitPaException($"You are on '{stage}'. Switch to {config.Dev} first: git switch {config.Dev}");

        if (git.RemoteExists(config.Remote))
        {
            reporter.Step($"Fetching {stage}, {Previous(config, stage)} and {branch} from {config.Remote}");
            foreach (var name in new[] { stage, Previous(config, stage), branch })
                Branches.TryFetch(git, config, name);
        }
        var feature = Branches.NewestOrThrow(git, config, branch);
        EnsureLocalStage(git, config, stage);
        WarnIfUntested(git, config, branch, feature, stage, reporter);
        WarnIfBaseMissing(git, config, branch, stage, reporter);

        int commits;
        using (var worktree = TempWorktree.Create(git, stage, stage))
        {
            if (git.RemoteBranchExists(config.Remote, stage))
                worktree.FastForwardTo($"{config.Remote}/{stage}");

            if (worktree.Git.Succeeds("merge-base", "--is-ancestor", feature, "HEAD"))
                return new PromoteResult(stage, AlreadyThere: true, 0);
            commits = int.Parse(worktree.Git.Run("rev-list", "--count", "--no-merges", $"HEAD..{feature}"));

            reporter.Step($"Merging {branch} into {stage}");
            var merge = worktree.Git.TryRun("merge", "--no-ff", "--no-edit", "-m", $"Merge branch '{branch}' into {stage}", feature);
            if (!merge.Success)
            {
                worktree.Git.TryRun("merge", "--abort");
                var folder = $"../{stage.Replace('/', '-')}";
                throw new GitPaException(
                    $"Merging '{branch}' into {stage} has conflicts, so nothing was changed. Resolve it in a separate folder:\n" +
                    $"  git worktree add {folder} {stage}\n" +
                    $"  cd {folder}\n" +
                    $"  git merge {feature}      (resolve the conflicts, then git commit)\n" +
                    $"  git push\n" +
                    $"  cd -  &&  git worktree remove {folder}\n" +
                    merge.Message);
            }
        }

        if (push && git.RemoteExists(config.Remote))
        {
            reporter.Step($"Pushing {stage} to {config.Remote}");
            if (git.RunAttached("push", "--quiet", config.Remote, stage) != 0)
                reporter.Warn($"Push failed. {branch} is merged into your local {stage}; push later with: git push {config.Remote} {stage}");
        }
        return new PromoteResult(stage, AlreadyThere: false, commits);
    }

    /// <summary>Validates and returns the git commands <see cref="Run"/> would execute.</summary>
    public static IReadOnlyList<string> Plan(GitRunner git, PaConfig config, string branch, string? stage, bool push)
    {
        Branches.EnsureFeature(config, branch);
        stage = ResolveStage(config, stage);
        var feature = Branches.NewestOrThrow(git, config, branch);
        if (Branches.Latest(git, config, stage) is null)
            throw MissingStage(config, stage);

        var worktree = TempWorktree.DisplayPathFor(git, stage);
        var hasRemote = git.RemoteExists(config.Remote);
        var steps = new List<string>();
        if (hasRemote)
            steps.AddRange(new[] { stage, Previous(config, stage), branch }.Select(b => GitRunner.Format("fetch", config.Remote, b)));
        steps.Add(GitRunner.Format("worktree", "add", worktree, stage));
        if (git.RemoteBranchExists(config.Remote, stage))
            steps.Add("  in worktree: " + GitRunner.Format("merge", "--ff-only", $"{config.Remote}/{stage}"));
        steps.Add("  in worktree: " + GitRunner.Format("merge", "--no-ff", "--no-edit", feature) + "   (if not there yet)");
        steps.Add(GitRunner.Format("worktree", "remove", worktree));
        if (push && hasRemote)
            steps.Add(GitRunner.Format("push", config.Remote, stage));
        return steps;
    }

    /// <summary>
    /// The first stage that doesn't have all of the branch's work yet, or null when every stage has it
    /// (or the branch has nothing beyond main).
    /// </summary>
    public static string? NextStage(GitRunner git, PaConfig config, string branch)
    {
        var feature = Branches.NewestOrThrow(git, config, branch);
        return config.Stages.FirstOrDefault(s =>
            Branches.Latest(git, config, s) is not { } stageRef || !git.Succeeds("merge-base", "--is-ancestor", feature, stageRef));
    }

    /// <summary>The branch a stage receives features from: dev for the first stage, else the stage before it.</summary>
    public static string Previous(PaConfig config, string stage)
    {
        var index = config.Stages.ToList().IndexOf(stage);
        return index <= 0 ? config.Dev : config.Stages[index - 1];
    }

    /// <summary>The given stage, or the first one when null. Throws when there are no stages or it isn't one.</summary>
    public static string ResolveStage(PaConfig config, string? stage)
    {
        if (config.Stages.Count == 0)
            throw new GitPaException(
                $"No stages are set up, so features go from {config.Dev} straight to {config.Main}. To add one, like uat:\n" +
                $"  git pa init --shared --main {config.Main} --dev {config.Dev} --stages uat");
        stage ??= config.Stages[0];
        if (!config.Stages.Contains(stage))
            throw new GitPaException($"'{stage}' is not a stage. The stages are: {string.Join(", ", config.Stages)} ({PaConfig.StagesKey}).");
        return stage;
    }

    static void EnsureLocalStage(GitRunner git, PaConfig config, string stage)
    {
        if (git.LocalBranchExists(stage))
            return;
        if (!git.RemoteBranchExists(config.Remote, stage))
            throw MissingStage(config, stage);
        git.Run("branch", "--quiet", "--track", stage, $"{config.Remote}/{stage}");
    }

    static GitPaException MissingStage(PaConfig config, string stage) => new(
        $"Stage branch '{stage}' does not exist locally or on {config.Remote}. Create it from {config.Main} first:\n" +
        $"  git branch {stage} {config.Remote}/{config.Main}  &&  git push -u {config.Remote} {stage}");

    /// <summary>
    /// A stacked branch can't go anywhere without the branch it builds on, so promoting it brings that
    /// branch's changes along. Says so when that branch isn't in the stage yet.
    /// </summary>
    static void WarnIfBaseMissing(GitRunner git, PaConfig config, string branch, string stage, IReporter reporter)
    {
        if (Stack.Base(git, config, branch) is not { } baseBranch
            || Branches.Newest(git, config, baseBranch) is not { } baseRef
            || Branches.Latest(git, config, stage) is not { } stageRef
            || git.Succeeds("merge-base", "--is-ancestor", baseRef, stageRef))
            return;
        reporter.Warn(
            $"{branch} builds on {baseBranch}, which isn't in {stage} yet, so {baseBranch}'s changes come along too. " +
            $"To promote them separately, promote {baseBranch} first: git pa promote {baseBranch}");
    }

    /// <summary>Warns when the feature has work its previous stage (dev for the first) never got.</summary>
    static void WarnIfUntested(GitRunner git, PaConfig config, string branch, string feature, string stage, IReporter reporter)
    {
        var previous = Previous(config, stage);
        var previousRef = Branches.Latest(git, config, previous);
        if (previousRef is null)
            return;
        var missing = int.Parse(git.Run("rev-list", "--count", "--no-merges", $"{previousRef}..{feature}"));
        if (missing > 0)
            reporter.Warn(
                $"{branch} has {missing} commit{(missing == 1 ? "" : "s")} that {(missing == 1 ? "isn't" : "aren't")} in {previousRef}, " +
                $"so {(missing == 1 ? "it" : "they")} skipped testing there.");
    }
}
