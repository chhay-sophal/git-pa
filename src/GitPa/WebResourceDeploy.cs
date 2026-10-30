using System.Text;

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
