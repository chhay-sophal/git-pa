namespace GitPa.Tests;

public class UpdateCheckTests : IDisposable
{
    readonly string cacheFile = Path.Combine(Path.GetTempPath(), $"git-pa-update-check-{Guid.NewGuid():N}.txt");
    readonly DateTimeOffset now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);
    int fetches;

    Task<IReadOnlyList<string>> Fetch(params string[] versions)
    {
        fetches++;
        return Task.FromResult<IReadOnlyList<string>>(versions);
    }

    [Theory]
    [InlineData("0.2.0", "0.1.0", true)]
    [InlineData("0.10.0", "0.9.0", true)]
    [InlineData("0.2.0", "0.2.0-dev.1", true)]
    [InlineData("0.2.0", "0.2.0", false)]
    [InlineData("0.2.0", "0.3.0-dev.1", false)]
    public void ComparesVersions(string candidate, string current, bool newer)
    {
        Assert.Equal(newer, UpdateCheck.IsNewer(candidate, current));
    }

    [Fact]
    public void IgnoresPrereleasesWhenFindingLatest()
    {
        Assert.Equal("0.10.0", UpdateCheck.LatestStable(["0.1.0", "0.10.0", "0.9.0", "0.11.0-beta"]));
    }

    [Fact]
    public async Task ChecksAtMostOncePerDay()
    {
        Assert.Equal("0.2.0", await UpdateCheck.FindNewerAsync("0.1.0", cacheFile, () => Fetch("0.1.0", "0.2.0"), now));
        Assert.Equal("0.2.0", await UpdateCheck.FindNewerAsync("0.1.0", cacheFile, () => Fetch("0.3.0"), now.AddHours(23)));
        Assert.Equal(1, fetches);

        Assert.Equal("0.3.0", await UpdateCheck.FindNewerAsync("0.1.0", cacheFile, () => Fetch("0.3.0"), now.AddHours(25)));
        Assert.Equal(2, fetches);
    }

    [Fact]
    public async Task ReturnsNullWhenUpToDateOrOffline()
    {
        Assert.Null(await UpdateCheck.FindNewerAsync("0.2.0", cacheFile, () => Fetch("0.2.0"), now));
        Assert.Null(await UpdateCheck.FindNewerAsync("0.2.0", cacheFile + ".2",
            () => throw new HttpRequestException("offline"), now));
    }

    public void Dispose()
    {
        File.Delete(cacheFile);
        File.Delete(cacheFile + ".2");
    }
}
