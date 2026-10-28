namespace GitPa;

/// <param name="ToPush">Commits not on the remote copy, or null when the branch was never pushed.</param>
/// <param name="ToPull">Commits on the remote copy that aren't local, or null when the branch was never pushed.</param>
public sealed record BranchStatus(
    string Name, string LastCommit, int AheadOfMain, int BehindMain, int NotInDev, int? ToPush, int? ToPull);

/// <param name="DevUpstream">The remote dev branch, or null when there is none.</param>
public sealed record RepoStatus(
    string MainRef, string Dev, string? DevUpstream, int DevToPush, int DevToPull, IReadOnlyList<BranchStatus> Branches);

/// <summary>Reports how each feature branch relates to main, dev, and its remote copy, as of the last fetch.</summary>
public static class StatusOperation
{
    public static RepoStatus Run(GitRunner git, PaConfig config)
    {
        var remote = config.Remote;
        var mainRef = git.RemoteBranchExists(remote, config.Main) ? $"{remote}/{config.Main}"
            : git.LocalBranchExists(config.Main) ? config.Main
            : throw new GitPaException($"Main branch '{config.Main}' was not found locally or on {remote}.");
        var devUpstream = git.RemoteBranchExists(remote, config.Dev) ? $"{remote}/{config.Dev}" : null;
        var (devToPull, devToPush) = devUpstream is null ? (0, 0) : Count(git, devUpstream, config.Dev);

        var branches = git.Run("for-each-ref", "--sort=-committerdate",
                "--format=%(refname:short)%09%(committerdate:relative)", "refs/heads")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('\t', 2))
            .Where(parts => parts[0] != config.Main && parts[0] != config.Dev)
            .Select(parts =>
            {
                var name = parts[0];
                var (behind, ahead) = Count(git, mainRef, name);
                var notInDev = git.LocalBranchExists(config.Dev)
                    ? int.Parse(git.Run("rev-list", "--count", $"{config.Dev}..{name}"))
                    : ahead;
                int? toPull = null, toPush = null;
                if (git.RemoteBranchExists(remote, name))
                    (toPull, toPush) = Count(git, $"{remote}/{name}", name);
                return new BranchStatus(name, parts[1], ahead, behind, notInDev, toPush, toPull);
            })
            .ToList();

        return new RepoStatus(mainRef, config.Dev, devUpstream, devToPush, devToPull, branches);
    }

    /// <summary>Counts commits only on <paramref name="left"/> and only on <paramref name="right"/>.</summary>
    static (int Left, int Right) Count(GitRunner git, string left, string right)
    {
        var counts = git.Run("rev-list", "--left-right", "--count", $"{left}...{right}").Split('\t');
        return (int.Parse(counts[0]), int.Parse(counts[1]));
    }
}
