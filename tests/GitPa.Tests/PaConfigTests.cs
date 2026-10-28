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
}
