namespace GitPa;

public sealed record CleanCandidate(string Name, string Reason);

/// <summary>Finds and deletes local feature branches whose work is already in main.</summary>
public static class CleanOperation
{
    public static IReadOnlyList<CleanCandidate> Find(GitRunner git, PaConfig config)
    {
        var main = Branches.LatestMain(git, config);
        var mainTree = git.Run("rev-parse", $"{main}^{{tree}}");
        var current = git.CurrentBranch();
        var candidates = new List<CleanCandidate>();

        foreach (var branch in Branches.LocalFeatures(git, config).Where(b => b != current))
        {
            if (IsUntouched(git, branch))
                continue;
            if (git.Succeeds("merge-base", "--is-ancestor", branch, main))
            {
                candidates.Add(new CleanCandidate(branch, $"merged into {main}"));
                continue;
            }

            // Squash-merged pull requests leave no ancestry, so check whether merging would change main at all.
            // (merge-tree --write-tree needs git 2.38+; older versions just skip this check.)
            var merge = git.TryRun("merge-tree", "--write-tree", main, branch);
            if (merge.Success && merge.Output.Split('\n')[0] == mainTree)
                candidates.Add(new CleanCandidate(branch, $"all changes are in {main} (squash-merged)"));
        }
        return candidates;
    }

    /// <summary>Deletes the branches and returns the ones that couldn't be deleted, with the reason.</summary>
    public static IReadOnlyList<string> Delete(GitRunner git, IEnumerable<string> branches) =>
        branches
            .Select(b => (Branch: b, Result: git.TryRun("branch", "-D", b)))
            .Where(r => !r.Result.Success)
            .Select(r => $"{r.Branch}: {r.Result.Message}")
            .ToList();

    /// <summary>A branch that was created but never committed to (e.g. by git pa new) looks merged; keep it.</summary>
    static bool IsUntouched(GitRunner git, string branch)
    {
        var reflog = git.TryRun("reflog", "show", "--format=%gs", $"refs/heads/{branch}");
        return reflog.Success
            && reflog.Output.Split('\n') is [var only]
            && only.StartsWith("branch: Created from");
    }
}
