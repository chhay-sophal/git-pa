namespace GitPa.Tests;

public class BranchNameTests
{
    [Theory]
    [InlineData("feature", "1234", "Add login page", "feature/1234-add-login-page")]
    [InlineData("feature", "1234", null, "feature/1234")]
    [InlineData("bugfix", "PA-77", "  Null   check! ", "bugfix/PA-77-null-check")]
    [InlineData("bugfix", " pa-77 ", "Null check", "bugfix/PA-77-null-check")]
    [InlineData("hotfix", "88", "", "hotfix/88")]
    [InlineData("refactor", "9", "Split service (part 2)", "refactor/9-split-service-part-2")]
    [InlineData("chore", null, "Update NuGet packages", "chore/update-nuget-packages")]
    public void BuildsConventionalNames(string type, string? ticket, string? title, string expected)
    {
        Assert.Equal(expected, BranchName.Build(type, ticket, title));
    }

    [Theory]
    [InlineData("feature", null, "title")] // ticket required
    [InlineData("feature", "12 34", "title")] // invalid ticket
    [InlineData("chore", null, "  !!  ")] // chore title required
    [InlineData("story", "1", "title")] // unknown type
    public void RejectsIncompleteInput(string type, string? ticket, string? title)
    {
        Assert.Throws<GitPaException>(() => BranchName.Build(type, ticket, title));
    }

    [Fact]
    public void EnforcesLengthLimit()
    {
        var title = new string('a', BranchName.MaxLength);

        var error = Assert.Throws<GitPaException>(() => BranchName.Build("feature", "1234", title));

        Assert.Contains($"limit is {BranchName.MaxLength}", error.Message);
        Assert.Equal(BranchName.MaxLength,
            BranchName.Build("feature", "1234", title[..(BranchName.MaxLength - "feature/1234-".Length)]).Length);
    }

    [Fact]
    public void ProducesNamesGitAccepts()
    {
        using var repo = new TestRepo();
        foreach (var name in new[] { BranchName.Build("bugfix", "PA-77", "Fix: the ~weird^ name?"), BranchName.Build("chore", null, "a..b") })
            Assert.True(repo.Git.IsValidBranchName(name), name);
    }
}
