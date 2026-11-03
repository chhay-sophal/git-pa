namespace GitPa.Tests;

public class BranchTypesTests
{
    [Fact]
    public void DefaultsIncludeTestAndDocs()
    {
        Assert.Equal("bugfix hotfix feature chore:noticket refactor test docs:noticket",
            BranchTypes.Format(BranchTypes.Default));
    }

    [Fact]
    public void ParsesNamesAndNoTicketMarks()
    {
        var types = BranchTypes.Parse("  Feature, story\tspike:NOTICKET  ");

        Assert.Equal([new("feature", true), new("story", true), new BranchType("spike", false)], types);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  , ")]
    [InlineData("feat/x")]
    [InlineData("-feature")]
    [InlineData("feature:optional")]
    [InlineData("feature Feature")]
    public void RejectsInvalidSettings(string value)
    {
        Assert.Throws<GitPaException>(() => BranchTypes.Parse(value));
    }

    [Fact]
    public void FindsTypesIgnoringCase()
    {
        Assert.Equal("docs", BranchTypes.Find(BranchTypes.Default, " DOCS ").Name);

        var error = Assert.Throws<GitPaException>(() => BranchTypes.Find(BranchTypes.Default, "story"));
        Assert.Contains(BranchTypes.Key, error.Message);
    }

    [Fact]
    public void LoadsFromGitConfig_OrDefaults()
    {
        using var repo = new TestRepo();
        Assert.Equal(BranchTypes.Default, BranchTypes.Load(repo.Git));

        repo.Git.Run("config", BranchTypes.Key, "story chore:noticket");

        Assert.Equal("story chore:noticket", BranchTypes.Format(BranchTypes.Load(repo.Git)));
    }
}
