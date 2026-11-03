namespace GitPa;

/// <summary>Finds where a file went when it's no longer at its old path, using git's rename detection.</summary>
public static class RenamedFiles
{
    /// <summary>Renames followed in a row (a file moved, then renamed again) before giving up.</summary>
    const int MaxHops = 5;

    /// <summary>The file's current path (relative, with forward slashes) if git shows it was renamed; null otherwise.</summary>
    public static string? Find(GitRunner git, string path)
    {
        var current = path.Replace('\\', '/');
        for (var hop = 0; hop < MaxHops; hop++)
        {
            var next = FindOnce(git, current);
            if (next is null || next == current)
                return null;
            if (File.Exists(Path.Combine(git.WorkingDirectory, next)))
                return next;
            current = next;
        }
        return null;
    }

    static string? FindOnce(GitRunner git, string path) =>
        RenamedTo(git.RunNul("diff", "--cached", "-M", "--name-status", "-z"), path) // staged (git mv)
        ?? UntrackedCopy(git, path) // moved in Explorer, not staged yet
        ?? Committed(git, path);

    /// <summary>An untracked file with exactly the content the file had at HEAD.</summary>
    static string? UntrackedCopy(GitRunner git, string path)
    {
        var old = git.TryRun("rev-parse", "--verify", "--quiet", $"HEAD:{path}");
        if (!old.Success)
            return null;
        var extension = Path.GetExtension(path);
        var candidates = git.RunNul("ls-files", "--others", "--exclude-standard", "-z")
            .Where(f => string.Equals(Path.GetExtension(f), extension, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (candidates.Count == 0)
            return null;
        var hashes = git.Run(["hash-object", "--", .. candidates]).Split('\n');
        var matches = candidates.Where((_, i) => i < hashes.Length && hashes[i].Trim() == old.Output).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    /// <summary>Where the commit that removed the file put it, if that commit renamed it.</summary>
    static string? Committed(GitRunner git, string path)
    {
        var commit = git.TryRun("log", "-1", "--format=%H", "--diff-filter=D", "--", path);
        if (!commit.Success || commit.Output.Length == 0)
            return null;
        return RenamedTo(git.RunNul("diff-tree", "-r", "-M", "--name-status", "--no-commit-id", "-z", commit.Output), path);
    }

    /// <summary>Reads -z --name-status output (status, path, and a second path for renames and copies).</summary>
    static string? RenamedTo(IReadOnlyList<string> entries, string path)
    {
        for (var i = 0; i < entries.Count; i++)
        {
            var status = entries[i];
            if (status.StartsWith('R') && i + 2 < entries.Count)
            {
                if (entries[i + 1] == path)
                    return entries[i + 2];
                i += 2;
            }
            else if (status.StartsWith('C'))
                i += 2;
            else
                i += 1;
        }
        return null;
    }
}
