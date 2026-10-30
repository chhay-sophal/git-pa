using System.Text.Json;

namespace GitPa;

/// <param name="Project">Project to build, relative to the repository root.</param>
/// <param name="File">The built (merged) assembly to push, relative to the repository root.</param>
/// <param name="PluginId">ID of the plug-in assembly record in Dataverse.</param>
public sealed record DeployAssembly(string Name, string Project, string File, Guid PluginId);

/// <summary>What <c>git pa deploy</c> builds and where it pushes it, read from git-pa.json in the repository root.</summary>
/// <param name="Solution">Solution to restore NuGet packages for before building, if any.</param>
/// <param name="MSBuild">Path to MSBuild.exe, when it can't be found automatically.</param>
public sealed record DeployConfig(
    string Environment, string Configuration, string? Solution, string? MSBuild, IReadOnlyList<DeployAssembly> Assemblies)
{
    public const string FileName = "git-pa.json";

    static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static DeployConfig Load(string repositoryRoot)
    {
        var path = Path.Combine(repositoryRoot, FileName);
        if (!System.IO.File.Exists(path))
            throw new GitPaException(
                $"There is no {FileName} in this repository, so git-pa doesn't know what to deploy. " +
                "Create it with: git pa deploy --setup");

        FileDto? file;
        try
        {
            file = JsonSerializer.Deserialize<FileDto>(System.IO.File.ReadAllText(path), Options);
        }
        catch (JsonException e)
        {
            throw new GitPaException($"{FileName} is not valid JSON: {e.Message}");
        }

        var deploy = file?.Deploy ?? throw new GitPaException($"{FileName} has no \"deploy\" section.");
        if (!Uri.TryCreate(deploy.Environment, UriKind.Absolute, out var environment) || environment.Scheme != "https")
            throw new GitPaException($"{FileName}: \"deploy.environment\" must be the environment URL, like https://contoso-dev.crm.dynamics.com.");
        if (deploy.Assemblies is not { Count: > 0 })
            throw new GitPaException($"{FileName}: \"deploy.assemblies\" must list at least one assembly.");

        var assemblies = deploy.Assemblies.Select((a, i) =>
        {
            var where = $"{FileName}: deploy.assemblies[{i}]";
            if (string.IsNullOrWhiteSpace(a.Project) || string.IsNullOrWhiteSpace(a.File))
                throw new GitPaException($"{where} needs \"project\" and \"file\".");
            if (!Guid.TryParse(a.PluginId, out var pluginId))
                throw new GitPaException($"{where}: \"pluginId\" must be the plug-in assembly's GUID.");
            var name = string.IsNullOrWhiteSpace(a.Name) ? Path.GetFileNameWithoutExtension(a.Project) : a.Name;
            return new DeployAssembly(name, a.Project, a.File, pluginId);
        }).ToList();

        var duplicate = assemblies.GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new GitPaException($"{FileName}: two assemblies are named '{duplicate.Key}'; give them different \"name\" values.");

        return new DeployConfig(environment.ToString().TrimEnd('/'), deploy.Configuration ?? "Release",
            deploy.Solution, deploy.MSBuild, assemblies);
    }

    /// <summary>Writes the config to git-pa.json, leaving out values that are the defaults.</summary>
    public void Save(string repositoryRoot)
    {
        var file = new
        {
            deploy = new
            {
                environment = Environment,
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
            },
        };
        var json = JsonSerializer.Serialize(file, new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        });
        System.IO.File.WriteAllText(Path.Combine(repositoryRoot, FileName), json + "\n");
    }

    public DeployAssembly Find(string name) =>
        Assemblies.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? throw new GitPaException(
            $"No assembly named '{name}' in {FileName}. Choose one of: {string.Join(", ", Assemblies.Select(a => a.Name))}.");

    /// <summary>Turns a path from the config file into a full path.</summary>
    public static string Resolve(string repositoryRoot, string path) =>
        Path.GetFullPath(Path.Combine(repositoryRoot, path.Replace('/', Path.DirectorySeparatorChar)));

    sealed class FileDto
    {
        public DeployDto? Deploy { get; set; }
    }

    sealed class DeployDto
    {
        public string? Environment { get; set; }
        public string? Configuration { get; set; }
        public string? Solution { get; set; }
        public string? MSBuild { get; set; }
        public List<AssemblyDto>? Assemblies { get; set; }
    }

    sealed class AssemblyDto
    {
        public string? Name { get; set; }
        public string? Project { get; set; }
        public string? File { get; set; }
        public string? PluginId { get; set; }
    }
}
