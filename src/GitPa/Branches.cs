namespace GitPa;

/// <summary>Helpers for finding main and the feature branches.</summary>
public static class Branches
{
    /// <summary>The newest known main: the remote-tracking branch if there is one, else the local branch.</summary>
    public static string LatestMain(GitRunner git, PaConfig config)
    {
        if (git.RemoteBranchExists(config.Remote, config.Main))
            return $"{config.Remote}/{config.Main}";
        if (git.LocalBranchExists(config.Main))
            return config.Main;
        throw new GitPaException($"Main branch '{config.Main}' was not found locally or on {config.Remote}.");
    }

    /// <summary>Local branches other than main and dev, most recently committed first.</summary>
    public static IReadOnlyList<string> LocalFeatures(GitRunner git, PaConfig config) =>
        git.LocalBranches().Where(b => b != config.Main && b != config.Dev).ToList();

    public static void EnsureFeature(PaConfig config, string branch)
    {
        if (branch == config.Main || branch == config.Dev)
            throw new GitPaException($"'{branch}' is not a feature branch.");
    }

    /// <summary>Makes sure a local feature branch exists, creating it from the remote copy if needed.</summary>
    public static void EnsureLocalFeature(GitRunner git, PaConfig config, string branch)
    {
        EnsureFeature(config, branch);
        if (git.LocalBranchExists(branch))
            return;
        if (!git.RemoteBranchExists(config.Remote, branch))
            throw new GitPaException($"Branch '{branch}' does not exist. Create it with: git pa new {branch}");
        git.Run("branch", "--quiet", "--track", branch, $"{config.Remote}/{branch}");
    }

    /// <summary>Fetches one branch, ignoring failures (for example when it was never pushed).</summary>
    public static bool TryFetch(GitRunner git, PaConfig config, string branch) =>
        git.RemoteExists(config.Remote) && git.Succeeds("fetch", "--quiet", config.Remote, branch);
}
