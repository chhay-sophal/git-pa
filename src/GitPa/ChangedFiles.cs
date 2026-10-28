namespace GitPa;

/// <summary>A changed file as reported by git status; <see cref="Index"/> and <see cref="WorkTree"/> are its XY codes.</summary>
public sealed record ChangedFile(string Path, string? OriginalPath, char Index, char WorkTree)
{
    public bool IsUntracked => Index == '?';

    public bool IsStaged => Index is not (' ' or '?');

    public bool HasUnstagedChanges => WorkTree != ' ';

    public bool IsPartiallyStaged => IsStaged && HasUnstagedChanges;

    public string Kind => (Index, WorkTree) switch
    {
        ('?', _) or ('A', _) => "new",
        ('R', _) => "renamed",
        ('C', _) => "copied",
        ('D', _) or (_, 'D') => "deleted",
        _ => "modified",
    };

    public string[] Paths => OriginalPath is null ? [Path] : [Path, OriginalPath];
}

public static class ChangedFiles
{
    public static IReadOnlyList<ChangedFile> List(GitRunner git)
    {
        var entries = git.RunNul("status", "--porcelain=v1", "-z", "--untracked-files=all");
        var files = new List<ChangedFile>();
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var (x, y) = (entry[0], entry[1]);
            // Renames and copies are followed by their original path.
            var original = x is 'R' or 'C' || y is 'R' or 'C' ? entries[++i] : null;
            files.Add(new ChangedFile(entry[3..], original, x, y));
        }
        return files;
    }

    /// <summary>
    /// Makes the index match the selection: stages the selected files and unstages the rest.
    /// Partially staged files that stay selected are left as they are.
    /// </summary>
    public static void Stage(GitRunner git, IReadOnlyList<ChangedFile> all, IReadOnlyCollection<ChangedFile> selected)
    {
        var add = selected.Where(f => f.HasUnstagedChanges && !f.IsPartiallyStaged).SelectMany(f => f.Paths).ToArray();
        var unstage = all.Except(selected).Where(f => f.IsStaged).SelectMany(f => f.Paths).ToArray();

        if (unstage.Length > 0)
            git.Run(["restore", "--staged", "--", .. unstage]);
        if (add.Length > 0)
            git.Run(["add", "-A", "--", .. add]);
    }
}
