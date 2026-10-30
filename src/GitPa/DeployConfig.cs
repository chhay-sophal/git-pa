using System.Text.Json;
using System.Text.Json.Nodes;

namespace GitPa;

/// <param name="Project">Project to build, relative to the repository root.</param>
/// <param name="File">The built (merged) assembly to push, relative to the repository root.</param>
/// <param name="PluginId">ID of the plug-in assembly record in Dataverse.</param>
public sealed record DeployAssembly(string Name, string Project, string File, Guid PluginId);

/// <summary>A web resource linked to a local file (by the wrsync VS Code extension, or by hand).</summary>
/// <param name="Name">The web resource's name in Dataverse, like new_/scripts/account.js; the same in every environment.</param>
/// <param name="File">The local file, relative to the repository root.</param>
public sealed record WebResourceLink(string Name, string File);

/// <summary>
/// What <c>git pa deploy</c> builds and where it pushes it, read from dataverse.json in the repository root.
/// The file is shared with other tools (like the wrsync extension), so only the environment and the
/// "plugins" section belong to git-pa; other sections are kept as they are.
/// </summary>
/// <param name="Solution">Solution to restore NuGet packages for before building, if any.</param>
/// <param name="MSBuild">Path to MSBuild.exe, when it can't be found automatically.</param>
public sealed record DeployConfig(
    string Environment, string Configuration, string? Solution, string? MSBuild, IReadOnlyList<DeployAssembly> Assemblies)
{
    /// <summary>Linked web resources, from the "webResources" section (written by the wrsync extension).</summary>
    public IReadOnlyList<WebResourceLink> WebResources { get; init; } = [];

    public const string FileName = "dataverse.json";

    /// <summary>The name used up to git-pa 0.2, with everything in a "deploy" section. Still read.</summary>
    public const string LegacyFileName = "git-pa.json";

    static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>The config file in use: dataverse.json, or else git-pa.json; null when there is neither.</summary>
    public static string? FindFile(string repositoryRoot) =>
        new[] { FileName, LegacyFileName }.Select(name => Path.Combine(repositoryRoot, name)).FirstOrDefault(System.IO.File.Exists);

    public static bool UsesLegacyFile(string repositoryRoot) =>
        FindFile(repositoryRoot) is { } path && Path.GetFileName(path) == LegacyFileName;

    public static DeployConfig Load(string repositoryRoot)
    {
        var path = FindFile(repositoryRoot) ?? throw new GitPaException(
            $"There is no {FileName} in this repository, so git-pa doesn't know what to deploy. " +
            "Create it with: git pa deploy --setup");
        var name = Path.GetFileName(path);

        FileDto? file;
        try
        {
            file = JsonSerializer.Deserialize<FileDto>(System.IO.File.ReadAllText(path), Options);
        }
        catch (JsonException e)
        {
            throw new GitPaException($"{name} is not valid JSON: {e.Message}");
        }

        var (section, plugins) = file?.Plugins is { } p ? ("plugins", p)
            : file?.Deploy is { } d ? ("deploy", (PluginsDto)d)
            : ("plugins", null);
        var links = file?.WebResources?.Links;
        if (plugins is null && links is not { Count: > 0 })
            throw new GitPaException(
                $"{name} has nothing to deploy: it has no \"plugins\" section and no linked web resources. " +
                "Set up plug-ins with: git pa deploy --setup. Link web resources with the wrsync VS Code extension.");
        var environmentText = file!.Environment ?? file.Deploy?.Environment;
        var environmentKey = file.Environment is null && file.Deploy is not null ? "deploy.environment" : "environment";
        if (!Uri.TryCreate(environmentText, UriKind.Absolute, out var environment) || environment.Scheme != "https")
            throw new GitPaException($"{name}: \"{environmentKey}\" must be the environment URL, like https://contoso-dev.crm.dynamics.com.");
        if (plugins is not null && plugins.Assemblies is not { Count: > 0 })
            throw new GitPaException($"{name}: \"{section}.assemblies\" must list at least one assembly.");

        var assemblies = (plugins?.Assemblies ?? []).Select((a, i) =>
        {
            var where = $"{name}: {section}.assemblies[{i}]";
            if (string.IsNullOrWhiteSpace(a.Project) || string.IsNullOrWhiteSpace(a.File))
                throw new GitPaException($"{where} needs \"project\" and \"file\".");
            if (!Guid.TryParse(a.PluginId, out var pluginId))
                throw new GitPaException($"{where}: \"pluginId\" must be the plug-in assembly's GUID.");
            var assemblyName = string.IsNullOrWhiteSpace(a.Name) ? Path.GetFileNameWithoutExtension(a.Project) : a.Name;
            return new DeployAssembly(assemblyName, a.Project, a.File, pluginId);
        }).ToList();

        var duplicate = assemblies.GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new GitPaException($"{name}: two assemblies are named '{duplicate.Key}'; give them different \"name\" values.");

        var webResources = (links ?? []).Select((l, i) =>
            string.IsNullOrWhiteSpace(l.Name) || string.IsNullOrWhiteSpace(l.File)
                ? throw new GitPaException($"{name}: webResources.links[{i}] needs \"name\" and \"file\".")
                : new WebResourceLink(l.Name, l.File)).ToList();
        var duplicateLink = webResources.GroupBy(l => l.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicateLink is not null)
            throw new GitPaException($"{name}: the web resource '{duplicateLink.Key}' is linked more than once.");

        return new DeployConfig(environment.ToString().TrimEnd('/'), plugins?.Configuration ?? "Release",
            plugins?.Solution, plugins?.MSBuild, assemblies) { WebResources = webResources };
    }

    /// <summary>
    /// Reads dataverse.json as plain JSON, to keep the sections git-pa doesn't own. Null when the file
    /// doesn't exist; throws when it isn't valid JSON, so nothing in it gets lost by overwriting it.
    /// </summary>
    public static JsonObject? ReadJson(string repositoryRoot)
    {
        var path = Path.Combine(repositoryRoot, FileName);
        if (!System.IO.File.Exists(path))
            return null;
        try
        {
            return JsonNode.Parse(System.IO.File.ReadAllText(path), documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            }) as JsonObject ?? throw new GitPaException($"{FileName} must contain a JSON object. Fix it first.");
        }
        catch (JsonException e)
        {
            throw new GitPaException($"{FileName} is not valid JSON, fix it first: {e.Message}");
        }
    }

    /// <summary>
    /// Writes the environment and the "plugins" section to dataverse.json, leaving out values that are the
    /// defaults and keeping the file's other sections.
    /// </summary>
    public void Save(string repositoryRoot)
    {
        var plugins = new
        {
            configuration = Configuration,
            solution = Solution,
            msbuild = MSBuild,
            assemblies = Assemblies.Select(a => new
            {
                name = a.Name == Path.GetFileNameWithoutExtension(a.Project) ? null : a.Name,
                project = a.Project,
                file = a.File,
                pluginId = a.PluginId.ToString(),
            }),
        };
        var file = new JsonObject
        {
            ["environment"] = Environment,
            ["plugins"] = JsonSerializer.SerializeToNode(plugins, new JsonSerializerOptions
            {
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            }),
        };
        foreach (var (key, value) in ReadJson(repositoryRoot) ?? [])
        {
            if (key is not ("environment" or "plugins" or "deploy"))
                file[key] = value?.DeepClone();
        }

        var json = file.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        System.IO.File.WriteAllText(Path.Combine(repositoryRoot, FileName), json + "\n");
    }

    public DeployAssembly Find(string name) =>
        Assemblies.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? throw new GitPaException(Assemblies.Count == 0 ? $"There are no plug-in assemblies in {FileName}." :
            $"No assembly named '{name}' in {FileName}. Choose one of: {string.Join(", ", Assemblies.Select(a => a.Name))}.");

    /// <summary>Turns a path from the config file into a full path.</summary>
    public static string Resolve(string repositoryRoot, string path) =>
        Path.GetFullPath(Path.Combine(repositoryRoot, path.Replace('/', Path.DirectorySeparatorChar)));

    sealed class FileDto
    {
        public string? Environment { get; set; }
        public PluginsDto? Plugins { get; set; }
        public DeployDto? Deploy { get; set; }
        public WebResourcesDto? WebResources { get; set; }
    }

    class PluginsDto
    {
        public string? Configuration { get; set; }
        public string? Solution { get; set; }
        public string? MSBuild { get; set; }
        public List<AssemblyDto>? Assemblies { get; set; }
    }

    /// <summary>The git-pa 0.2 layout: the environment inside the section.</summary>
    sealed class DeployDto : PluginsDto
    {
        public string? Environment { get; set; }
    }

    sealed class WebResourcesDto
    {
        public List<LinkDto>? Links { get; set; }
    }

    sealed class LinkDto
    {
        public string? Name { get; set; }
        public string? File { get; set; }
    }

    sealed class AssemblyDto
    {
        public string? Name { get; set; }
        public string? Project { get; set; }
        public string? File { get; set; }
        public string? PluginId { get; set; }
    }
}
