using System.Text;
using System.Text.RegularExpressions;

namespace GitPa;

/// <param name="Content">The local file's content, to upload.</param>
public sealed record WebResourceChange(WebResourceLink Link, WebResourceRecord Record, byte[] Content);

/// <summary>How the linked files compare with the web resources in the environment.</summary>
/// <param name="Changed">Linked files whose content differs from Dataverse.</param>
/// <param name="MissingFiles">Links whose local file doesn't exist.</param>
/// <param name="NotInEnvironment">Links to web resources the environment doesn't have.</param>
/// <param name="Managed">Links to managed web resources, which can't be changed.</param>
public sealed record WebResourceComparison(
    IReadOnlyList<WebResourceChange> Changed,
    IReadOnlyList<WebResourceLink> MissingFiles,
    IReadOnlyList<WebResourceLink> NotInEnvironment,
    IReadOnlyList<WebResourceLink> Managed,
    int Unchanged);

/// <summary>Publishes the linked web resources whose local files differ from the environment.</summary>
public static class WebResourceDeploy
{
    /// <summary>
    /// The links matching any of the patterns, in their configured order: a web resource name or linked
    /// file (relative to the repository root or to <paramref name="workingDirectory"/>), case-insensitive,
    /// with * and ? wildcards. Throws for a pattern that matches nothing, before anything is deployed.
    /// </summary>
    public static IReadOnlyList<WebResourceLink> Select(
        IReadOnlyList<WebResourceLink> links, IReadOnlyList<string> patterns, string repositoryRoot, string workingDirectory)
    {
        if (patterns.Count == 0)
            return links;
        var unmatched = patterns.Where(p => !links.Any(l => Matches(l, p, repositoryRoot, workingDirectory))).ToList();
        if (unmatched.Count > 0)
            throw new GitPaException(
                $"No linked web resource matches {string.Join(", ", unmatched.Select(p => $"'{p}'"))}. " +
                $"Use a web resource name or a linked file from {DeployConfig.FileName}; * and ? work as wildcards.");
        return links.Where(l => patterns.Any(p => Matches(l, p, repositoryRoot, workingDirectory))).ToList();
    }

    public static bool Matches(WebResourceLink link, string pattern, string repositoryRoot, string workingDirectory)
    {
        var normalized = pattern.Trim().Replace('\\', '/');
        if (normalized.StartsWith("./"))
            normalized = normalized[2..];
        if (normalized.IndexOfAny(['*', '?']) >= 0)
        {
            var regex = new Regex(
                "^" + Regex.Escape(normalized).Replace(@"\*", ".*").Replace(@"\?", ".") + "$", RegexOptions.IgnoreCase);
            return regex.IsMatch(link.Name) || regex.IsMatch(link.File);
        }

        string? fromHere; // a file path typed relative to the current folder
        try
        {
            fromHere = Path.GetRelativePath(repositoryRoot, Path.GetFullPath(pattern, workingDirectory)).Replace('\\', '/');
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            fromHere = null;
        }
        return new[] { normalized, fromHere }.Any(p =>
            string.Equals(p, link.Name, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(p, link.File.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));
    }

    public static WebResourceComparison Compare(string repositoryRoot, IReadOnlyList<WebResourceLink> links, IDataverse dataverse)
    {
        var missingFiles = links.Where(l => !File.Exists(DeployConfig.Resolve(repositoryRoot, l.File))).ToList();
        var present = links.Except(missingFiles).ToList();
        var records = dataverse.WebResources(present.Select(l => l.Name).ToList())
            .ToDictionary(r => r.Name, StringComparer.OrdinalIgnoreCase);

        var changed = new List<WebResourceChange>();
        var notInEnvironment = new List<WebResourceLink>();
        var managed = new List<WebResourceLink>();
        var unchanged = 0;
        foreach (var link in present)
        {
            if (!records.TryGetValue(link.Name, out var record))
                notInEnvironment.Add(link);
            else if (record.IsManaged)
                managed.Add(link);
            else
            {
                var content = File.ReadAllBytes(DeployConfig.Resolve(repositoryRoot, link.File));
                if (SameContent(content, record.Content))
                    unchanged++;
                else
                    changed.Add(new WebResourceChange(link, record, content));
            }
        }
        return new WebResourceComparison(changed, missingFiles, notInEnvironment, managed, unchanged);
    }

    /// <summary>
    /// Uploads the changed files, then publishes them together. If an upload fails, the ones already
    /// uploaded are still published, so the environment never keeps unpublished changes that a later
    /// comparison would see as already deployed.
    /// </summary>
    public static void Publish(IReadOnlyList<WebResourceChange> changes, IDataverse dataverse, IReporter reporter)
    {
        reporter.Step($"Uploading {Plural(changes.Count, "web resource")}");
        var uploaded = new List<WebResourceChange>();
        GitPaException? failure = null;
        foreach (var change in changes)
        {
            try
            {
                dataverse.UpdateContent(change.Record.Id, change.Content);
                uploaded.Add(change);
            }
            catch (GitPaException e)
            {
                failure = new GitPaException(
                    $"Uploading {change.Link.Name} failed: {e.Message}" +
                    (uploaded.Count > 0 ? $"\nThe {Plural(uploaded.Count, "web resource")} uploaded before it were published; the rest were not." : ""));
                break;
            }
        }

        if (uploaded.Count > 0)
        {
            reporter.Step($"Publishing {Plural(uploaded.Count, "web resource")}");
            dataverse.Publish(uploaded.Select(c => c.Record.Id).ToList());
        }
        if (failure is not null)
            throw failure;
    }

    /// <summary>
    /// Compares content the way the wrsync extension does: a UTF-8 byte order mark and CRLF vs LF line
    /// endings don't count as changes (a Windows checkout and the maker portal disagree on both).
    /// </summary>
    public static bool SameContent(byte[] local, byte[] remote) => Normalize(local) == Normalize(remote);

    static string Normalize(byte[] content) =>
        Encoding.UTF8.GetString(content).TrimStart('﻿').Replace("\r\n", "\n").Replace('\r', '\n');

    public static string Plural(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";
}
