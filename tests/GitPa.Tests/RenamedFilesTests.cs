namespace GitPa.Tests;

public class RenamedFilesTests
{
    const string Script = "function onLoad(context) {\n  // a web resource long enough for git's rename detection\n  return context;\n}\n";

    static TestRepo RepoWith(string file)
    {
        var repo = new TestRepo();
        Directory.CreateDirectory(Path.Combine(repo.Git.WorkingDirectory, "scripts"));
        TestRepo.Write(repo.Git, file, Script);
        TestRepo.Commit(repo.Git, "add script");
        return repo;
    }

    [Fact]
    public void FindsACommittedRename_FollowingSeveral()
    {
        using var repo = RepoWith("account.js");
        repo.Git.Run("mv", "account.js", "scripts/account.js");
        TestRepo.Commit(repo.Git, "move");
        repo.Git.Run("mv", "scripts/account.js", "scripts/sync_account.js");
        TestRepo.Write(repo.Git, "scripts/sync_account.js", Script + "// edited too\n");
        TestRepo.Commit(repo.Git, "rename and edit");

        Assert.Equal("scripts/sync_account.js", RenamedFiles.Find(repo.Git, "account.js"));
    }

    [Fact]
    public void FindsAStagedRename()
    {
        using var repo = RepoWith("account.js");
        repo.Git.Run("mv", "account.js", "scripts/account.js");

        Assert.Equal("scripts/account.js", RenamedFiles.Find(repo.Git, "account.js"));
    }

    [Fact]
    public void FindsAFileMovedOutsideGit_ByItsContent()
    {
        using var repo = RepoWith("account.js");
        File.Move(Path.Combine(repo.Git.WorkingDirectory, "account.js"), Path.Combine(repo.Git.WorkingDirectory, "scripts", "acc.js"));

        Assert.Equal("scripts/acc.js", RenamedFiles.Find(repo.Git, "account.js"));
    }

    [Fact]
    public void FindsNothing_ForADeletedOrUnknownFile()
    {
        using var repo = RepoWith("account.js");
        repo.Git.Run("rm", "--quiet", "account.js");
        TestRepo.Commit(repo.Git, "delete");

        Assert.Null(RenamedFiles.Find(repo.Git, "account.js"));
        Assert.Null(RenamedFiles.Find(repo.Git, "never/existed.js"));
    }
}
