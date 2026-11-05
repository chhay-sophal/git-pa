namespace GitPa;

/// <summary>
/// Stacked feature branches: a branch started from another feature branch (its base) instead of main, because
/// it needs that branch's unmerged work. The base is saved in git config when git pa new creates the branch;
/// for branches created elsewhere (like a teammate's), it is guessed from the history.
/// </summary>
public static class Stack
{
    /// <summary>The git config key that stores a branch's base.</summary>
    public static string Key(string branch) => $"branch.{branch}.paBase";

    public static void SetBase(GitRunner git, string branch, string baseBranch) =>
        git.Run("config", "--local", Key(branch), baseBranch);

    /// <summary>The saved base, whether or not it still exists or is merged; null when none was saved.</summary>
    public static string? SavedBase(GitRunner git, string branch)
    {
        var result = git.TryRun("config", "--local", "--get", Key(branch));
        return result.Success && result.Output.Length > 0 ? result.Output : null;
    }

    /// <summary>
    /// The feature branch this one builds on, while that branch still has work that isn't in main;
    /// null when the branch is based on main (or its base has since been merged or deleted).
    /// </summary>
    public static string? Base(GitRunner git, PaConfig config, string branch)
    {
        var main = Branches.LatestMain(git, config);
        if (SavedBase(git, branch) is { } saved)
            return Branches.Exists(git, config, saved) && !IsInMain(git, main, Branches.Newest(git, config, saved)) ? saved : null;
        return Guess(git, config, branch, main);
    }

    /// <summary>
    /// Whether all of <paramref name="reference"/>'s work is in main: merged, or squash-merged (merging it
    /// would change nothing; that check needs git 2.38+, older versions treat squash-merged work as not in main).
    /// </summary>
    public static bool IsInMain(GitRunner git, string main, string reference)
    {
        if (git.Succeeds("merge-base", "--is-ancestor", reference, main))
            return true;
        var merge = git.TryRun("merge-tree", "--write-tree", main, reference);
        return merge.Success && merge.Output.Split('\n')[0] == git.Run("rev-parse", $"{main}^{{tree}}");
    }

    /// <summary>
    /// The nearest feature branch whose commits (beyond main) are all inside this branch: the one this branch
    /// was most likely started from. Null when there is none.
    /// </summary>
    static string? Guess(GitRunner git, PaConfig config, string branch, string main)
    {
        var tip = Branches.Newest(git, config, branch);
        string? best = null;
        var bestAhead = 0;
        foreach (var candidate in Branches.AllFeatures(git, config).Where(c => c != branch))
        {
            var candidateRef = Branches.Newest(git, config, candidate);
            if (candidateRef is null || !git.Succeeds("merge-base", "--is-ancestor", candidateRef, tip))
                continue;
            // A branch with nothing beyond main (just created) is no base; the nearest base has the most commits.
            var ahead = int.Parse(git.Run("rev-list", "--count", $"{main}..{candidateRef}"));
            var isSameCommit = git.Run("rev-parse", candidateRef) == git.Run("rev-parse", tip);
            if (ahead > bestAhead && !isSameCommit)
                (best, bestAhead) = (candidate, ahead);
        }
        return best;
    }
}
