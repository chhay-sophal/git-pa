namespace GitPa.Tests;

/// <summary>
/// A throwaway clone of a bare "origin", with main and dev branches pushed,
/// dev checked out, and git-pa configured.
/// </summary>
sealed class TestRepo : IDisposable
{
    readonly string root;

    public TestRepo()
    {
        root = Directory.CreateTempSubdirectory("git-pa-tests-").FullName;
        new GitRunner(root).Run("init", "--quiet", "--bare", "-b", "main", "origin.git");

        Git = Clone("work");
        Write(Git, "README.md", string.Join('\n', Enumerable.Range(1, 10).Select(i => $"line {i}")) + "\n");
        Commit(Git, "initial");
        Git.Run("push", "--quiet", "-u", "origin", "main");
        Git.Run("switch", "--quiet", "-c", "dev");
        Git.Run("push", "--quiet", "-u", "origin", "dev");
        PaConfig.Save(Git, "main", "dev", global: false);
    }

    public GitRunner Git { get; }

    public PaConfig Config => PaConfig.Load(Git);

    public RecordingReporter Reporter { get; } = new();

    /// <summary>Another developer's clone of the same origin.</summary>
    public GitRunner Clone(string name)
    {
        // Config passed to clone applies before checkout, so files are checked out byte-for-byte.
        new GitRunner(root).Run("clone", "--quiet",
            "-c", "core.autocrlf=false",
            "-c", "commit.gpgsign=false",
            "-c", "user.name=Test",
            "-c", "user.email=test@example.com",
            "origin.git", name);
        return new GitRunner(Path.Combine(root, name));
    }

    public static void Write(GitRunner git, string file, string content) =>
        File.WriteAllText(Path.Combine(git.WorkingDirectory, file), content);

    public static string Read(GitRunner git, string file) =>
        File.ReadAllText(Path.Combine(git.WorkingDirectory, file));

    public static void Commit(GitRunner git, string message)
    {
        git.Run("add", "-A");
        git.Run("commit", "--quiet", "-m", message);
    }

    public void Dispose()
    {
        // git marks object files read-only, which blocks Directory.Delete on Windows.
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(root, recursive: true);
    }
}

sealed class RecordingReporter : IReporter
{
    public List<string> Warnings { get; } = [];

    public void Step(string message) { }

    public void Warn(string message) => Warnings.Add(message);
}
