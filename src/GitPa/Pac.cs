using System.Text.RegularExpressions;

namespace GitPa;

public sealed record PacEnvironment(string Name, string Url, bool Active);

public sealed record PacSolution(string Name, string UniqueName, Guid Id);

public sealed record PacAssembly(string Name, Guid Id);

/// <summary>
/// Read-only Dataverse queries through the Power Platform CLI, using its existing sign-in.
/// Each method returns null when pac fails (not signed in, no access, offline).
/// </summary>
/// <remarks>
/// pac prints tables padded for the console, and display names may contain spaces or characters the console
/// encoding mangles, so rows are parsed from their fixed-format parts (GUIDs, URLs, unique names), not by column.
/// </remarks>
public static class Pac
{
    const string Guid = "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}";

    public static IReadOnlyList<PacEnvironment>? Environments(IToolRunner runner, string pac, string workingDirectory)
    {
        var result = runner.Capture(pac, ["org", "list"], workingDirectory);
        if (result.ExitCode != 0)
            return null;
        return Lines(result.Output)
            .Select(line => Regex.Match(line, $@"^(?<active>\*)?\s*(?<name>.+?)\s+{Guid}\s+(?<url>https://\S+)\s+\S+$"))
            .Where(m => m.Success)
            .Select(m => new PacEnvironment(m.Groups["name"].Value.Trim(), m.Groups["url"].Value.TrimEnd('/'), m.Groups["active"].Success))
            .ToList();
    }

    public static IReadOnlyList<PacSolution>? Solutions(IToolRunner runner, string pac, string environment, string workingDirectory)
    {
        var rows = Fetch(runner, pac, environment, workingDirectory, "friendlyname",
            "<fetch><entity name='solution'><attribute name='friendlyname'/><attribute name='uniquename'/>" +
            "<filter><condition attribute='isvisible' operator='eq' value='1'/>" +
            "<condition attribute='ismanaged' operator='eq' value='0'/></filter>" +
            "<order attribute='friendlyname'/></entity></fetch>");
        return rows?
            .Select(row => Regex.Match(row, $@"^(?<name>.+?)\s+(?<unique>\S+)\s+(?<id>{Guid})$"))
            .Where(m => m.Success)
            .Select(m => new PacSolution(m.Groups["name"].Value, m.Groups["unique"].Value, System.Guid.Parse(m.Groups["id"].Value)))
            .ToList();
    }

    /// <summary>Plug-in assemblies in a solution, or all unmanaged ones when <paramref name="solutionId"/> is null.</summary>
    public static IReadOnlyList<PacAssembly>? Assemblies(
        IToolRunner runner, string pac, string environment, Guid? solutionId, string workingDirectory)
    {
        var filter = solutionId is { } id
            ? "<link-entity name='solutioncomponent' from='objectid' to='pluginassemblyid'><filter>" +
              $"<condition attribute='solutionid' operator='eq' value='{id}'/>" +
              "<condition attribute='componenttype' operator='eq' value='91'/></filter></link-entity>"
            : "<filter><condition attribute='ismanaged' operator='eq' value='0'/></filter>";
        var rows = Fetch(runner, pac, environment, workingDirectory, "name",
            $"<fetch><entity name='pluginassembly'><attribute name='name'/>{filter}<order attribute='name'/></entity></fetch>");
        return rows?
            .Select(row => Regex.Match(row, $@"^(?<name>\S+)\s+(?<id>{Guid})"))
            .Where(m => m.Success)
            .Select(m => new PacAssembly(m.Groups["name"].Value, System.Guid.Parse(m.Groups["id"].Value)))
            .ToList();
    }

    /// <summary>The plug-in types registered for an assembly.</summary>
    public static IReadOnlySet<string>? PluginTypes(
        IToolRunner runner, string pac, string environment, Guid assemblyId, string workingDirectory) =>
        Fetch(runner, pac, environment, workingDirectory, "typename",
                "<fetch><entity name='plugintype'><attribute name='typename'/><filter>" +
                $"<condition attribute='pluginassemblyid' operator='eq' value='{assemblyId}'/></filter></entity></fetch>")?
            .Select(row => row.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries)[0])
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>Runs a FetchXML query and returns the table rows below the header (which starts with <paramref name="firstColumn"/>).</summary>
    static IReadOnlyList<string>? Fetch(
        IToolRunner runner, string pac, string environment, string workingDirectory, string firstColumn, string fetchXml)
    {
        var result = runner.Capture(pac, ["org", "fetch", "--environment", environment, "--xml", fetchXml], workingDirectory);
        if (result.ExitCode != 0)
            return null;

        var lines = Lines(result.Output);
        var header = lines.FindIndex(l => l.StartsWith(firstColumn, StringComparison.OrdinalIgnoreCase));
        if (header < 0)
            return lines.Any(l => l.Contains("No records", StringComparison.OrdinalIgnoreCase)) ? [] : null;
        return lines.Skip(header + 1).Where(l => l.Length > 0).ToList();
    }

    static List<string> Lines(string output) => output.Split('\n', StringSplitOptions.TrimEntries).ToList();
}
