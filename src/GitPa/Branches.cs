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

    /// <summary>The newest known copy of a branch (remote-tracking, else local), or null when there is none.</summary>
    public static string? Latest(GitRunner git, PaConfig config, string branch) =>
        git.RemoteBranchExists(config.Remote, branch) ? $"{config.Remote}/{branch}"
        : git.LocalBranchExists(branch) ? branch
        : null;

    public static bool Exists(GitRunner git, PaConfig config, string branch) =>
        git.LocalBranchExists(branch) || git.RemoteBranchExists(config.Remote, branch);

    /// <summary>
    /// The newest copy of a branch: the local one, or the remote one when it has commits the local one lacks.
    /// When the two have diverged, the local one. Null when the branch doesn't exist.
    /// </summary>
    public static string? Newest(GitRunner git, PaConfig config, string branch)
    {
        var remote = git.RemoteBranchExists(config.Remote, branch) ? $"{config.Remote}/{branch}" : null;
        if (!git.LocalBranchExists(branch))
            return remote;
        return remote is not null && git.Succeeds("merge-base", "--is-ancestor", branch, remote)
            && !git.Succeeds("merge-base", "--is-ancestor", remote, branch)
            ? remote
            : branch;
    }

    /// <summary>
    /// Like <see cref="Newest"/>, but throws when the branch doesn't exist or its local and remote copies
    /// have diverged, since using either would leave work behind.
    /// </summary>
    public static string NewestOrThrow(GitRunner git, PaConfig config, string branch)
    {
        var newest = Newest(git, config, branch) ?? throw new GitPaException($"Branch '{branch}' does not exist.");
        var remote = $"{config.Remote}/{branch}";
        if (newest == branch && git.RemoteBranchExists(config.Remote, branch)
            && !git.Succeeds("merge-base", "--is-ancestor", remote, branch))
            throw new GitPaException(
                $"Your '{branch}' and {remote} have both changed. Push or pull it first (git pa status shows which), then try again.");
        return newest;
    }

    /// <summary>Feature branches, local or on the remote, by name.</summary>
    public static IReadOnlyList<string> AllFeatures(GitRunner git, PaConfig config)
    {
        var remotePrefix = $"{config.Remote}/";
        var remote = git.Run("for-each-ref", "--format=%(refname:short)", $"refs/remotes/{config.Remote}")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(r => r.StartsWith(remotePrefix))
            .Select(r => r[remotePrefix.Length..]);
        return git.LocalBranches().Concat(remote)
            .Where(b => b != "HEAD" && !config.IsLongLived(b))
            .Distinct()
            .ToList();
    }

    /// <summary>Local branches other than main, dev and the stages, most recently committed first.</summary>
    public static IReadOnlyList<string> LocalFeatures(GitRunner git, PaConfig config) =>
        git.LocalBranches().Where(b => !config.IsLongLived(b)).ToList();

    public static void EnsureFeature(PaConfig config, string branch)
    {
        if (config.IsLongLived(branch))
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
