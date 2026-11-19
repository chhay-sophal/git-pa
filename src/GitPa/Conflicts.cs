using System.Text.RegularExpressions;

namespace GitPa;

/// <summary>A conflict left open in a temporary worktree, for someone to resolve.</summary>
/// <param name="Summary">What was being combined, like "Your change doesn't apply cleanly to feature/1".</param>
/// <param name="Files">The conflicted files, relative to the worktree.</param>
/// <param name="Guidance">Which side is which, and what the result should contain.</param>
public sealed record Conflict(string Summary, IReadOnlyList<string> Files, string Guidance);

/// <summary>Lets someone resolve a conflict in a temporary worktree before the operation carries on.</summary>
public interface IConflictResolver
{
    /// <summary>
    /// Returns true once the conflicts are resolved and staged (see <see cref="Conflicts.StageResolved"/>),
    /// or false to cancel the operation.
    /// </summary>
    bool Resolve(TempWorktree worktree, Conflict conflict);
}

public static class Conflicts
{
    /// <summary>Files git still marks as conflicted.</summary>
    public static IReadOnlyList<string> Unmerged(GitRunner git) =>
        git.RunNul("diff", "--name-only", "-z", "--diff-filter=U");

    /// <summary>
    /// Stages each of <paramref name="files"/> that no longer has conflict markers (deleting a file counts as
    /// resolving it). Returns the files that still have conflicts.
    /// </summary>
    public static IReadOnlyList<string> StageResolved(GitRunner git, IEnumerable<string> files)
    {
        foreach (var file in files.Where(f => !HasMarkers(Path.Combine(git.WorkingDirectory, f))))
            git.Run("add", "-A", "--", file);
        return Unmerged(git);
    }

    static bool HasMarkers(string path) =>
        File.Exists(path) && Regex.IsMatch(File.ReadAllText(path), @"^(<{7}|>{7})( |\r?$)", RegexOptions.Multiline);

    /// <summary>
    /// Feature branches (other than <paramref name="branch"/>) with commits that changed <paramref name="files"/>
    /// and are in <paramref name="source"/> but not in <paramref name="branch"/>: the work the conflict is
    /// most likely with.
    /// </summary>
    public static IReadOnlyList<string> FeaturesInvolved(
        GitRunner git, PaConfig config, string branch, string source, IReadOnlyList<string> files)
    {
        var branchRef = Branches.Newest(git, config, branch) ?? branch;
        var commits = git.Run(["log", "--no-merges", "--format=%H", "-n", "20", $"{branchRef}..{source}", "--", .. files])
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (commits.Length == 0)
            return [];
        var main = Branches.LatestMain(git, config);
        return Branches.AllFeatures(git, config)
            .Where(f => f != branch)
            .Where(f => Branches.Newest(git, config, f) is { } featureRef
                && !git.Succeeds("merge-base", "--is-ancestor", featureRef, main)
                && commits.Any(c => git.Succeeds("merge-base", "--is-ancestor", c, featureRef)))
            .ToList();
    }

    /// <summary>"feature/1 changed it too" style hint, or empty when no feature branch is involved.</summary>
    public static string InvolvedHint(IReadOnlyList<string> features, string source, string branch) =>
        features.Count == 0 ? ""
            : $" {string.Join(", ", features)} changed {(features.Count == 1 ? "it" : "them")} on {source}, " +
              $"and {(features.Count == 1 ? "that work isn't" : "their work isn't")} on {branch}.";

    /// <summary>
    /// Lets <paramref name="resolver"/> resolve the merge in progress in the worktree and commits it.
    /// Returns false when there is no resolver or it was cancelled; the merge is then still in progress.
    /// </summary>
    public static bool TryResolveMerge(TempWorktree worktree, IConflictResolver? resolver, Conflict conflict)
    {
        if (resolver is null || conflict.Files.Count == 0 || !resolver.Resolve(worktree, conflict))
            return false;
        var commit = worktree.Git.TryRun("commit", "--quiet", "--no-edit");
        if (!commit.Success)
            throw new GitPaException($"Committing the resolved merge failed.\n{commit.Message}");
        return true;
    }
}
