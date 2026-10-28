namespace GitPa;

public static class NewBranchOperation
{
    /// <summary>
    /// Creates <paramref name="name"/> from the latest main without leaving the current branch.
    /// Returns the ref the branch was created from.
    /// </summary>
    public static string Run(GitRunner git, PaConfig config, string name, IReporter reporter)
    {
        if (!git.IsValidBranchName(name))
            throw new GitPaException($"'{name}' is not a valid branch name.");
        if (git.LocalBranchExists(name))
            throw new GitPaException($"Branch '{name}' already exists.");

        var baseRef = LatestMain(git, config, reporter);
        git.Run("branch", "--no-track", name, baseRef);
        return baseRef;
    }

    static string LatestMain(GitRunner git, PaConfig config, IReporter reporter)
    {
        if (git.RemoteExists(config.Remote))
        {
            reporter.Step($"Fetching {config.Remote}/{config.Main}");
            var fetch = git.TryRun("fetch", "--quiet", config.Remote, config.Main);
            if (!fetch.Success)
                reporter.Warn($"Could not fetch {config.Remote}/{config.Main}; using the last known version.\n{fetch.Message}");
        }

        if (git.RemoteBranchExists(config.Remote, config.Main))
            return $"{config.Remote}/{config.Main}";
        if (git.LocalBranchExists(config.Main))
            return config.Main;
        throw new GitPaException($"Main branch '{config.Main}' was not found locally or on {config.Remote}.");
    }
}
