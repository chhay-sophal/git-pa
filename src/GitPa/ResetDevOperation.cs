namespace GitPa;

/// <param name="Ref">The ref to merge: the local branch, or the remote copy when that is newer or the only one.</param>
/// <param name="InDev">Whether some of the branch's work is in dev now, i.e. it is currently deployed.</param>
public sealed record ResetCandidate(string Name, string Ref, string LastCommit, bool InDev);

/// <summary>
/// Rebuilds the shared dev branch as main plus selected feature branches and force-pushes it.
/// Teammates' git-pa notices the reset (via <see cref="DevBranch.ResetTrailer"/>) and follows it.
/// </summary>
public static class ResetDevOperation
{
    /// <summary>Feature branches (local and remote) with work that isn't in main yet, as of the last fetch.</summary>
    public static IReadOnlyList<ResetCandidate> Candidates(GitRunner git, PaConfig config)
    {
        var main = Branches.LatestMain(git, config);
        var dev = git.RemoteBranchExists(config.Remote, config.Dev) ? $"{config.Remote}/{config.Dev}" : config.Dev;
        var local = ListRefs(git, "refs/heads", 2);
        var remote = ListRefs(git, $"refs/remotes/{config.Remote}", 3);

        var candidates = new List<ResetCandidate>();
        foreach (var (name, (localRef, localDate)) in local.Concat(remote.Where(r => !local.ContainsKey(r.Key)))
                     .ToDictionary(p => p.Key, p => p.Value))
        {
            if (name == config.Main || name == config.Dev || name == "HEAD")
                continue;

            // Prefer the local branch unless the remote copy has commits it lacks.
            var (reference, lastCommit) = (localRef, localDate);
            if (remote.TryGetValue(name, out var r) && local.ContainsKey(name)
                && !git.Succeeds("merge-base", "--is-ancestor", r.Ref, localRef))
                (reference, lastCommit) = r;

            var ahead = int.Parse(git.Run("rev-list", "--count", $"{main}..{reference}"));
            if (ahead == 0)
                continue;
            var notInDev = int.Parse(git.Run("rev-list", "--count", $"{dev}..{reference}"));
            candidates.Add(new ResetCandidate(name, reference, lastCommit, InDev: notInDev < ahead));
        }
        return candidates.OrderByDescending(c => c.InDev).ToList();
    }

    /// <summary>Returns the git commands <see cref="Run"/> would execute.</summary>
    public static IReadOnlyList<string> Plan(GitRunner git, PaConfig config, IReadOnlyList<ResetCandidate> keep)
    {
        Validate(git, config);
        var worktree = TempWorktree.DisplayPathFor(git, "reset-dev");
        return
        [
            GitRunner.Format("worktree", "add", "--detach", worktree, Branches.LatestMain(git, config)),
            $"  in worktree: git commit --allow-empty -m \"Reset {config.Dev} to {config.Main}\"   (marks the reset for teammates)",
            .. keep.Select(c => "  in worktree: " + GitRunner.Format("merge", "--no-ff", c.Ref)),
            GitRunner.Format("worktree", "remove", worktree),
            $"git push --force-with-lease {config.Remote} <new {config.Dev}>:{config.Dev}",
            $"git update-ref refs/pa-backup/{config.Dev}-<date> HEAD   (backup of your old {config.Dev})",
            $"git reset --hard <new {config.Dev}>   (uncommitted changes are set aside and restored)",
        ];
    }

    /// <summary>Returns the backup ref holding the old dev.</summary>
    public static string Run(GitRunner git, PaConfig config, IReadOnlyList<ResetCandidate> keep, IReporter reporter)
    {
        Validate(git, config);
        var main = Branches.LatestMain(git, config);
        var oldRemoteDev = git.RemoteBranchExists(config.Remote, config.Dev)
            ? git.Run("rev-parse", $"{config.Remote}/{config.Dev}")
            : "";

        string newTip;
        using (var worktree = TempWorktree.Create(git, "reset-dev", main, detach: true))
        {
            worktree.Git.Run("commit", "--quiet", "--allow-empty", "-m", $"Reset {config.Dev} to {config.Main}",
                "-m", $"{DevBranch.ResetTrailer}: {DateTimeOffset.UtcNow:O}");
            for (var i = 0; i < keep.Count; i++)
            {
                reporter.Step($"Merging {keep[i].Name}");
                var merge = worktree.Git.TryRun("merge", "--no-ff", "--no-edit",
                    "-m", $"Merge branch '{keep[i].Name}' into {config.Dev}", keep[i].Ref);
                if (!merge.Success)
                {
                    worktree.Git.TryRun("merge", "--abort");
                    var before = i == 0 ? config.Main : string.Join(", ", keep.Take(i).Select(c => c.Name));
                    throw new GitPaException(
                        $"'{keep[i].Name}' conflicts with {before}. Nothing was changed.\n" +
                        $"Leave it out, or bring it up to date first (git pa update {keep[i].Name}).\n{merge.Message}");
                }
            }
            newTip = worktree.Git.Run("rev-parse", "HEAD");
        }

        DevBranch.CheckUntrackedCollisions(git, config, [newTip], replacing: true);

        // Push first: if someone pushed to dev in the meantime, the lease fails and nothing has changed yet.
        reporter.Step($"Force-pushing the new {config.Dev} to {config.Remote}");
        if (git.RunAttached("push", "--quiet", $"--force-with-lease=refs/heads/{config.Dev}:{oldRemoteDev}",
                config.Remote, $"{newTip}:refs/heads/{config.Dev}") != 0)
            throw new GitPaException(
                $"Pushing the new {config.Dev} failed, maybe because someone pushed to it meanwhile. " +
                "Nothing was changed; run git pa reset-dev again.");
        git.TryRun("fetch", "--quiet", config.Remote, config.Dev);

        return DevBranch.MoveTo(git, config, newTip, reporter);
    }

    static void Validate(GitRunner git, PaConfig config)
    {
        DevBranch.EnsureCheckedOut(git, config);
        if (!git.RemoteExists(config.Remote))
            throw new GitPaException($"There is no remote named '{config.Remote}' to reset {config.Dev} on.");
    }

    /// <summary>Branch name → (full ref, relative date of the last commit).</summary>
    static Dictionary<string, (string Ref, string LastCommit)> ListRefs(GitRunner git, string prefix, int strip) =>
        git.Run("for-each-ref", "--sort=-committerdate",
                $"--format=%(refname:lstrip={strip})%09%(refname)%09%(committerdate:relative)", prefix)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('\t'))
            .ToDictionary(p => p[0], p => (p[1], p[2]));
}
