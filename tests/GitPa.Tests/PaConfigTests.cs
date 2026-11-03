namespace GitPa.Tests;

public class PaConfigTests
{
    [Fact]
    public void Load_ReturnsSavedBranchesWithDefaults()
    {
        using var repo = new TestRepo();

        var config = repo.Config;

        Assert.Equal("main", config.Main);
        Assert.Equal("dev", config.Dev);
        Assert.Equal("origin", config.Remote);
        Assert.True(config.AutoPush);
    }

    [Fact]
    public void Load_ThrowsWhenNotConfigured()
    {
        using var repo = new TestRepo();
        var other = repo.Clone("other");

        Assert.Throws<GitPaException>(() => PaConfig.Load(other));
    }

    [Fact]
    public void Load_ReadsAutoPushSetting()
    {
        using var repo = new TestRepo();
        repo.Git.Run("config", PaConfig.AutoPushKey, "false");

        Assert.False(repo.Config.AutoPush);
    }

    [Fact]
    public void Load_ReadsTeamSettingsFromSharedFile()
    {
        using var repo = new TestRepo();
        var teammate = repo.Clone("teammate"); // no git-pa config of its own
        TestRepo.Write(teammate, PaConfig.SharedFileName, "[pa]\n\tmain = main\n\tdev = dev\n\tautoPush = false\n");

        var config = PaConfig.Load(teammate);

        Assert.Equal(("main", "dev"), (config.Main, config.Dev));
        Assert.False(config.AutoPush);
    }

    [Fact]
    public void Get_PrefersThisRepositorysConfigOverSharedFile()
    {
        using var repo = new TestRepo();
        TestRepo.Write(repo.Git, PaConfig.SharedFileName, "[pa]\n\tdev = team-dev\n\tbranchTypes = story\n");

        Assert.Equal("dev", PaConfig.Get(repo.Git, PaConfig.DevKey)); // set locally by TestRepo
        Assert.Equal("story", PaConfig.Get(repo.Git, BranchTypes.Key));
    }

    [Fact]
    public void Get_ReadsSharedFileFromSubfolders()
    {
        using var repo = new TestRepo();
        TestRepo.Write(repo.Git, PaConfig.SharedFileName, "[pa]\n\tprUrl = https://example.com/{branch}\n");
        var sub = Directory.CreateDirectory(Path.Combine(repo.Git.WorkingDirectory, "src", "deep")).FullName;

        Assert.Equal("https://example.com/{branch}", PaConfig.Get(new GitRunner(sub), PullRequestUrl.TemplateKey));
    }

    [Fact]
    public void Load_ReportsInvalidSharedValues()
    {
        using var repo = new TestRepo();
        TestRepo.Write(repo.Git, PaConfig.SharedFileName, "[pa]\n\tautoPush = maybe\n");

        Assert.Throws<GitPaException>(() => PaConfig.Load(repo.Git));
    }

    [Fact]
    public void Save_WritesSharedFileAtRoot()
    {
        using var repo = new TestRepo();

        PaConfig.Save(repo.Git, "trunk", "develop", ConfigScope.Shared);

        var file = Path.Combine(repo.Git.WorkingDirectory, PaConfig.SharedFileName);
        Assert.Equal("trunk", repo.Git.Run("config", "--file", file, "--get", PaConfig.MainKey));
        Assert.Equal("main", PaConfig.Get(repo.Git, PaConfig.MainKey)); // the local setting still wins
    }
}
