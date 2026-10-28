namespace GitPa.Tests;

public class ChangedFilesTests
{
    [Fact]
    public void ListsModifiedUntrackedRenamedAndPartiallyStagedFiles()
    {
        using var repo = new TestRepo();
        TestRepo.Write(repo.Git, "old name.txt", "content\n");
        TestRepo.Commit(repo.Git, "add file");
        repo.Git.Run("mv", "old name.txt", "new name.txt");
        TestRepo.Write(repo.Git, "README.md", "staged\n");
        repo.Git.Run("add", "README.md");
        TestRepo.Write(repo.Git, "README.md", "staged, then edited\n");
        Directory.CreateDirectory(Path.Combine(repo.Git.WorkingDirectory, "src"));
        TestRepo.Write(repo.Git, "src/Plugin.cs", "class Plugin {}\n");

        var files = ChangedFiles.List(repo.Git).ToDictionary(f => f.Path);

        Assert.Equal(3, files.Count);
        Assert.True(files["README.md"].IsPartiallyStaged);
        Assert.Equal("renamed", files["new name.txt"].Kind);
        Assert.Equal("old name.txt", files["new name.txt"].OriginalPath);
        Assert.True(files["src/Plugin.cs"].IsUntracked);
        Assert.Equal("new", files["src/Plugin.cs"].Kind);
    }

    [Fact]
    public void Stage_MakesIndexMatchSelection()
    {
        using var repo = new TestRepo();
        TestRepo.Write(repo.Git, "a.txt", "a\n");
        TestRepo.Write(repo.Git, "b.txt", "b\n");
        TestRepo.Commit(repo.Git, "add files");
        TestRepo.Write(repo.Git, "a.txt", "a changed\n");
        TestRepo.Write(repo.Git, "b.txt", "b changed\n");
        repo.Git.Run("add", "b.txt");
        TestRepo.Write(repo.Git, "c.txt", "new\n");
        File.Delete(Path.Combine(repo.Git.WorkingDirectory, "README.md"));
        var files = ChangedFiles.List(repo.Git);

        ChangedFiles.Stage(repo.Git, files, files.Where(f => f.Path is "a.txt" or "c.txt" or "README.md").ToList());

        Assert.Equal("README.md\na.txt\nc.txt", repo.Git.Run("diff", "--cached", "--name-only"));
        Assert.Equal("b.txt", repo.Git.Run("diff", "--name-only"));
    }

    [Fact]
    public void Stage_KeepsPartialStagingOfSelectedFile()
    {
        using var repo = new TestRepo();
        TestRepo.Write(repo.Git, "README.md", "staged\n");
        repo.Git.Run("add", "README.md");
        TestRepo.Write(repo.Git, "README.md", "staged, then edited\n");
        var files = ChangedFiles.List(repo.Git);

        ChangedFiles.Stage(repo.Git, files, files);

        Assert.Equal("staged\n", repo.Git.Run("show", ":README.md") + "\n");
    }
}
