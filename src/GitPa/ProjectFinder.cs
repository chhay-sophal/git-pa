using System.Xml.Linq;

namespace GitPa;

/// <param name="Project">The .csproj, relative to the repository root.</param>
/// <param name="File">Where the build puts the assembly to push, relative to the repository root.</param>
public sealed record ProjectGuess(string Project, string File, bool UsesILMerge);

/// <summary>Finds the local project that builds a plug-in assembly, and where its output lands.</summary>
public static class ProjectFinder
{
    static readonly string[] SkippedFolders = ["bin", "obj", "packages", "node_modules", ".git", ".vs"];

    /// <summary>All .csproj files in the repository, relative to the root, with forward slashes.</summary>
    public static IReadOnlyList<string> Projects(string root) =>
        Find(root, root).Order(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>The project whose assembly name matches, or null when none does.</summary>
    public static ProjectGuess? ForAssembly(string root, string assemblyName, string configuration) =>
        Projects(root)
            .Where(p => string.Equals(AssemblyName(root, p), assemblyName, StringComparison.OrdinalIgnoreCase))
            .Select(p => Guess(root, p, configuration))
            .FirstOrDefault();

    public static ProjectGuess Guess(string root, string project, string configuration)
    {
        var path = Path.Combine(root, project);
        var xml = XDocument.Load(path);
        var usesILMerge = File.ReadAllText(path).Contains("ILMerge", StringComparison.OrdinalIgnoreCase);

        string output;
        if (xml.Root?.Attribute("Sdk") is not null)
        {
            // SDK-style projects put each target framework in its own folder.
            var framework = Value(xml, "TargetFramework") ?? Value(xml, "TargetFrameworks")?.Split(';')[0];
            output = framework is null ? $"bin/{configuration}/" : $"bin/{configuration}/{framework.Trim()}/";
        }
        else
        {
            // Old-style projects set OutputPath per configuration, e.g. Condition="'$(Configuration)|$(Platform)' == 'Release|AnyCPU'".
            output = xml.Descendants()
                .Where(e => e.Name.LocalName == "PropertyGroup"
                    && (e.Attribute("Condition")?.Value.Contains($"'{configuration}|", StringComparison.OrdinalIgnoreCase) ?? false))
                .SelectMany(e => e.Elements().Where(c => c.Name.LocalName == "OutputPath"))
                .Select(e => e.Value.Trim())
                .FirstOrDefault() ?? $"bin/{configuration}/";
            output = output.Replace('\\', '/').TrimEnd('/') + "/";
        }

        var directory = Path.GetDirectoryName(project)?.Replace('\\', '/');
        var prefix = string.IsNullOrEmpty(directory) ? "" : directory + "/";
        var file = $"{prefix}{output}{(usesILMerge ? "ILMerge/" : "")}{AssemblyName(root, project)}.dll";
        return new ProjectGuess(project, file, usesILMerge);
    }

    static string AssemblyName(string root, string project) =>
        Value(XDocument.Load(Path.Combine(root, project)), "AssemblyName") ?? Path.GetFileNameWithoutExtension(project);

    static string? Value(XDocument xml, string element) =>
        xml.Descendants().FirstOrDefault(e => e.Name.LocalName == element)?.Value.Trim() is { Length: > 0 } value ? value : null;

    static IEnumerable<string> Find(string root, string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*.csproj"))
            yield return Path.GetRelativePath(root, file).Replace('\\', '/');
        foreach (var sub in Directory.EnumerateDirectories(directory))
        {
            if (SkippedFolders.Contains(Path.GetFileName(sub), StringComparer.OrdinalIgnoreCase))
                continue;
            foreach (var file in Find(root, sub))
                yield return file;
        }
    }
}
