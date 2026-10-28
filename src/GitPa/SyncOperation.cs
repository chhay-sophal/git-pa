namespace GitPa;

/// <param name="Pulled">Commits merged in from the remote dev.</param>
/// <param name="ResetBackup">When the remote dev was rebuilt by reset-dev, the backup ref of the old local dev.</param>
public sealed record SyncResult(int Pulled, string? ResetBackup);

/// <summary>Brings the latest remote dev into the local dev branch, keeping uncommitted changes.</summary>
public static class SyncOperation
{
    public static SyncResult Run(GitRunner git, PaConfig config, IReporter reporter)
    {
        Validate(git, config);
        var upstream = $"{config.Remote}/{config.Dev}";

        reporter.Step($"Fetching {upstream}");
        var fetch = git.TryRun("fetch", "--quiet", config.Remote, config.Dev);
        if (!fetch.Success)
            throw new GitPaException($"Could not fetch {upstream}:\n{fetch.Message}");

        if (DevBranch.FollowReset(git, config, reporter) is { } backup)
            return new SyncResult(0, backup);

        var behind = int.Parse(git.Run("rev-list", "--count", $"HEAD..{upstream}"));
        if (behind == 0)
            return new SyncResult(0, null);

        DevBranch.CheckUntrackedCollisions(git, config, [upstream]);
        DevBranch.Merge(git, [upstream], DevBranch.Restore.Everything, "", reporter);
        return new SyncResult(behind, null);
    }

    /// <summary>Validates and returns the git commands <see cref="Run"/> would execute.</summary>
    public static IReadOnlyList<string> Plan(GitRunner git, PaConfig config)
    {
        Validate(git, config);
        return
        [
            GitRunner.Format("fetch", config.Remote, config.Dev),
            "git stash push   (if you have uncommitted changes)",
            GitRunner.Format("merge", "--no-edit", $"{config.Remote}/{config.Dev}") + "   (if behind)",
            "git stash pop --index   (if you had uncommitted changes)",
        ];
    }

    static void Validate(GitRunner git, PaConfig config)
    {
        DevBranch.EnsureCheckedOut(git, config);
        if (!git.RemoteExists(config.Remote))
            throw new GitPaException($"There is no remote named '{config.Remote}' to sync with.");
    }
}
