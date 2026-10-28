using System.Globalization;
using System.Text.Json;

namespace GitPa;

/// <summary>Checks nuget.org for a newer git-pa, at most once a day.</summary>
public static class UpdateCheck
{
    public const string DisableVariable = "GIT_PA_NO_UPDATE_CHECK";
    public const string DisableKey = "pa.updateCheck";
    const string VersionsUrl = "https://api.nuget.org/v3-flatcontainer/gitpa/index.json";
    static readonly TimeSpan CheckInterval = TimeSpan.FromDays(1);

    public static string CacheFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "git-pa", "update-check.txt");

    /// <summary>
    /// Returns the newest stable version if it is newer than <paramref name="current"/>. Uses the cached answer
    /// when it is less than a day old; never throws.
    /// </summary>
    public static async Task<string?> FindNewerAsync(
        string current, string cacheFile, Func<Task<IReadOnlyList<string>>> fetchVersions, DateTimeOffset now)
    {
        try
        {
            var latest = ReadCache(cacheFile, now);
            if (latest is null)
            {
                latest = LatestStable(await fetchVersions());
                if (latest is not null)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(cacheFile)!);
                    await File.WriteAllTextAsync(cacheFile, $"{now:O}\n{latest}");
                }
            }
            return latest is not null && IsNewer(latest, current) ? latest : null;
        }
        catch (Exception)
        {
            return null; // offline, blocked, or a broken cache: never get in the way
        }
    }

    public static async Task<IReadOnlyList<string>> FetchVersionsAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        await using var stream = await http.GetStreamAsync(VersionsUrl);
        using var json = await JsonDocument.ParseAsync(stream);
        return json.RootElement.GetProperty("versions").EnumerateArray().Select(v => v.GetString()!).ToList();
    }

    public static string? LatestStable(IEnumerable<string> versions) =>
        versions.Where(v => !v.Contains('-') && Version.TryParse(v, out _))
            .OrderByDescending(Version.Parse)
            .FirstOrDefault();

    /// <summary>Compares semantic versions; a release is newer than a pre-release of the same version.</summary>
    public static bool IsNewer(string candidate, string current)
    {
        var (candidateCore, candidatePre) = Split(candidate);
        var (currentCore, currentPre) = Split(current);
        if (candidateCore != currentCore)
            return candidateCore > currentCore;
        return candidatePre is null && currentPre is not null;
    }

    static (Version Core, string? Prerelease) Split(string version)
    {
        var dash = version.IndexOf('-');
        return dash < 0 ? (Version.Parse(version), null) : (Version.Parse(version[..dash]), version[(dash + 1)..]);
    }

    static string? ReadCache(string cacheFile, DateTimeOffset now)
    {
        if (!File.Exists(cacheFile))
            return null;
        var lines = File.ReadAllLines(cacheFile);
        return lines.Length == 2
               && DateTimeOffset.TryParse(lines[0], CultureInfo.InvariantCulture, DateTimeStyles.None, out var checkedAt)
               && now - checkedAt < CheckInterval
            ? lines[1]
            : null;
    }
}
