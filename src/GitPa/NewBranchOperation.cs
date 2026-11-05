namespace GitPa;

public static class NewBranchOperation
{
    /// <summary>
    /// Creates <paramref name="name"/> from the latest main, or from the latest <paramref name="from"/> (a feature
    /// branch it builds on, saved as its base), without leaving the current branch. Returns the ref it was created from.
    /// </summary>
    public static string Run(GitRunner git, PaConfig config, string name, IReporter reporter, string? from = null)
    {
        from = Normalize(config, from);
        Validate(git, config, name, from);
        var source = from ?? config.Main;

        if (git.RemoteExists(config.Remote) && (from is null || git.RemoteBranchExists(config.Remote, from)))
        {
            reporter.Step($"Fetching {config.Remote}/{source}");
            var fetch = git.TryRun("fetch", "--quiet", config.Remote, source);
            if (!fetch.Success)
                reporter.Warn($"Could not fetch {config.Remote}/{source}; using the last known version.\n{fetch.Message}");
        }

        var baseRef = from is null ? Branches.LatestMain(git, config) : Branches.NewestOrThrow(git, config, from);
        git.Run("branch", "--no-track", name, baseRef);
        if (from is not null)
            Stack.SetBase(git, name, from);
        return baseRef;
    }

    /// <summary>Validates the name and returns the git commands <see cref="Run"/> would execute.</summary>
    public static IReadOnlyList<string> Plan(GitRunner git, PaConfig config, string name, string? from = null)
    {
        from = Normalize(config, from);
        Validate(git, config, name, from);
        var steps = new List<string>();
        if (git.RemoteExists(config.Remote) && (from is null || git.RemoteBranchExists(config.Remote, from)))
            steps.Add(GitRunner.Format("fetch", config.Remote, from ?? config.Main));
        var baseRef = from is null ? Branches.LatestMain(git, config) : Branches.NewestOrThrow(git, config, from);
        steps.Add(GitRunner.Format("branch", "--no-track", name, baseRef));
        if (from is not null)
            steps.Add(GitRunner.Format("config", Stack.Key(name), from) + "   (remembers what it builds on)");
        return steps;
    }

    /// <summary>Starting "from main" is the same as not stacking at all.</summary>
    static string? Normalize(PaConfig config, string? from) =>
        string.IsNullOrWhiteSpace(from) || from.Trim() == config.Main ? null : from.Trim();

    static void Validate(GitRunner git, PaConfig config, string name, string? from)
    {
        if (!git.IsValidBranchName(name))
            throw new GitPaException($"'{name}' is not a valid branch name.");
        if (git.LocalBranchExists(name))
            throw new GitPaException($"Branch '{name}' already exists.");
        if (from is null)
            return;
        if (config.IsLongLived(from))
            throw new GitPaException($"New branches start from {config.Main} or a feature branch, not '{from}'.");
        if (!Branches.Exists(git, config, from))
            throw new GitPaException($"Branch '{from}' does not exist.");
    }
}
