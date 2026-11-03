namespace GitPa.Tests;

public class BranchNameTests
{
    static BranchType Type(string name) => BranchTypes.Find(BranchTypes.Default, name);

    static readonly IReadOnlyList<BranchType> Types = BranchTypes.Default;

    [Theory]
    [InlineData("feature", "1234", "Add login page", "feature/1234-add-login-page")]
    [InlineData("feature", "1234", null, "feature/1234")]
    [InlineData("bugfix", "PA-77", "  Null   check! ", "bugfix/PA-77-null-check")]
    [InlineData("bugfix", " pa-77 ", "Null check", "bugfix/PA-77-null-check")]
    [InlineData("hotfix", "88", "", "hotfix/88")]
    [InlineData("refactor", "9", "Split service (part 2)", "refactor/9-split-service-part-2")]
    [InlineData("chore", null, "Update NuGet packages", "chore/update-nuget-packages")]
    [InlineData("test", "PA-77", "Cover login", "test/PA-77-cover-login")]
    [InlineData("docs", null, "Explain deploy", "docs/explain-deploy")]
    public void BuildsConventionalNames(string type, string? ticket, string? title, string expected)
    {
        Assert.Equal(expected, BranchName.Build(Type(type), ticket, title));
    }

    [Theory]
    [InlineData("feature", null, "title")] // ticket required
    [InlineData("feature", "12 34", "title")] // invalid ticket
    [InlineData("chore", null, "  !!  ")] // chore title required
    public void RejectsIncompleteInput(string type, string? ticket, string? title)
    {
        Assert.Throws<GitPaException>(() => BranchName.Build(Type(type), ticket, title));
    }

    [Fact]
    public void EnforcesLengthLimit()
    {
        var title = new string('a', BranchName.MaxLength);

        var error = Assert.Throws<GitPaException>(() => BranchName.Build(Type("feature"), "1234", title));

        Assert.Contains($"limit is {BranchName.MaxLength}", error.Message);
        Assert.Equal(BranchName.MaxLength,
            BranchName.Build(Type("feature"), "1234", title[..(BranchName.MaxLength - "feature/1234-".Length)]).Length);
    }

    [Theory]
    [InlineData("feature/PA-77-add-login", "PA-77")]
    [InlineData("bugfix/1234", "1234")]
    [InlineData("hotfix/1234-2fa-fix", "1234")]
    [InlineData("refactor/AB-12-C3-split", "AB-12-C3")]
    [InlineData("chore/update-packages", null)]
    [InlineData("feat/login", null)]
    [InlineData("feature/no-ticket-here", null)]
    [InlineData("test/PA-9-cover-login", "PA-9")]
    [InlineData("docs/PA-9-not-a-ticket-type", null)]
    public void FindsTicketInBranchName(string branch, string? ticket)
    {
        Assert.Equal(ticket, BranchName.TicketFrom(branch, Types));
    }

    [Fact]
    public void FindsTicketsForConfiguredTypesOnly()
    {
        var types = BranchTypes.Parse("story spike.x notes:noticket");

        Assert.Equal("PA-1", BranchName.TicketFrom("story/PA-1-login", types));
        Assert.Equal("7", BranchName.TicketFrom("spike.x/7", types));
        Assert.Null(BranchName.TicketFrom("spikeax/7", types)); // the dot isn't a wildcard
        Assert.Null(BranchName.TicketFrom("feature/PA-1-login", types));
        Assert.Null(BranchName.TicketFrom("notes/PA-1", BranchTypes.Parse("notes:noticket")));
    }

    [Theory]
    [InlineData("feature/PA-77-add-login", "Add login page", "PA-77: Add login page")]
    [InlineData("feature/PA-77-add-login", "PA-77 Add login page", "PA-77 Add login page")]
    [InlineData("feature/PA-77-add-login", "pa-77: add login", "pa-77: add login")]
    [InlineData("chore/update-packages", "Update packages", "Update packages")]
    public void PrefixesSummaryWithTicket(string branch, string summary, string expected)
    {
        Assert.Equal(expected, BranchName.WithTicketPrefix(branch, summary, Types));
    }

    [Fact]
    public void ProducesNamesGitAccepts()
    {
        using var repo = new TestRepo();
        foreach (var name in new[] { BranchName.Build(Type("bugfix"), "PA-77", "Fix: the ~weird^ name?"), BranchName.Build(Type("chore"), null, "a..b") })
            Assert.True(repo.Git.IsValidBranchName(name), name);
    }
}
