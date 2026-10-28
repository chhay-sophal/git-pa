using System.Diagnostics;
using System.Text.RegularExpressions;

namespace GitPa;

/// <summary>Builds the "create pull request" page URL for GitHub, Azure DevOps, and Bitbucket remotes.</summary>
public static class PullRequestUrl
{
    public const string TemplateKey = "pa.prUrl";

    /// <summary>Returns the URL, or null when the host isn't recognized.</summary>
    /// <param name="resolveSshHost">Maps an SSH host alias (from ~/.ssh/config) to the real host name.</param>
    public static string? Build(string remoteUrl, string branch, string target, Func<string, string?>? resolveSshHost = null)
    {
        var (host, port, path, ssh) = Parse(remoteUrl);
        if (ssh && Classify(host) is null && resolveSshHost?.Invoke(host) is { } real)
            host = real;
        host = host.ToLowerInvariant();
        var hostWithPort = !ssh && port is not null ? $"{host}:{port}" : host;
        var segments = path.Split('/');

        switch (Classify(host))
        {
            case "github":
                return $"https://github.com/{path}/compare/{EscapePath(target)}...{EscapePath(branch)}?expand=1";
            case "bitbucket":
                return $"https://bitbucket.org/{path}/pull-requests/new?source={Q(branch)}&dest={Q(target)}";
            case "azure":
                var repoUrl = host switch
                {
                    // git@ssh.dev.azure.com:v3/org/project/repo
                    "ssh.dev.azure.com" when segments.Length == 4 =>
                        $"https://dev.azure.com/{segments[1]}/{segments[2]}/_git/{segments[3]}",
                    // git@vs-ssh.visualstudio.com:v3/org/project/repo
                    "vs-ssh.visualstudio.com" when segments.Length == 4 =>
                        $"https://{segments[1]}.visualstudio.com/{segments[2]}/_git/{segments[3]}",
                    _ when path.Contains("/_git/") => $"https://{hostWithPort}/{path}",
                    _ => null,
                };
                return repoUrl is null ? null : $"{repoUrl}/pullrequestcreate?sourceRef={Q(branch)}&targetRef={Q(target)}";
        }

        // Bitbucket Server / Data Center: https://host/scm/PROJ/repo.git or ssh://git@host:7999/proj/repo.git
        var serverPath = segments is ["scm", _, _] ? segments[1..] : ssh && port == 7999 && segments.Length == 2 ? segments : null;
        if (serverPath is not null)
            return $"https://{hostWithPort}/projects/{serverPath[0]}/repos/{serverPath[1]}/pull-requests?create" +
                $"&sourceBranch={Q("refs/heads/" + branch)}&targetBranch={Q("refs/heads/" + target)}";
        return null;
    }

    /// <summary>Fills a pa.prUrl template, replacing {branch} and {target}.</summary>
    public static string FromTemplate(string template, string branch, string target) =>
        template.Replace("{branch}", Q(branch)).Replace("{target}", Q(target));

    /// <summary>Asks ssh which host an alias from ~/.ssh/config points to.</summary>
    public static string? ResolveSshHost(string alias)
    {
        try
        {
            var info = new ProcessStartInfo("ssh") { RedirectStandardOutput = true, RedirectStandardError = true };
            info.ArgumentList.Add("-G");
            info.ArgumentList.Add(alias);
            using var process = Process.Start(info)!;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return Regex.Match(output, @"^hostname (\S+)", RegexOptions.Multiline) is { Success: true } m ? m.Groups[1].Value : null;
        }
        catch (Exception)
        {
            return null; // no ssh available
        }
    }

    static string? Classify(string host)
    {
        host = host.ToLowerInvariant();
        if (host == "github.com")
            return "github";
        if (host == "bitbucket.org")
            return "bitbucket";
        if (host is "dev.azure.com" or "ssh.dev.azure.com" || host.EndsWith(".visualstudio.com"))
            return "azure";
        return null;
    }

    static (string Host, int? Port, string Path, bool Ssh) Parse(string url)
    {
        if (url.Contains("://"))
        {
            var uri = new Uri(url);
            return (uri.Host, uri.IsDefaultPort ? null : uri.Port, TrimPath(uri.AbsolutePath), uri.Scheme == "ssh");
        }

        // scp-like syntax: [user@]host:path
        var match = Regex.Match(url, @"^(?:[^@/]+@)?([^:/]+):(.+)$");
        if (!match.Success)
            throw new GitPaException($"Unrecognized remote URL: {url}");
        return (match.Groups[1].Value, null, TrimPath(match.Groups[2].Value), true);
    }

    static string TrimPath(string path)
    {
        path = path.Trim('/');
        return path.EndsWith(".git") ? path[..^4] : path;
    }

    static string Q(string value) => Uri.EscapeDataString(value);

    static string EscapePath(string branch) => string.Join('/', branch.Split('/').Select(Uri.EscapeDataString));
}
