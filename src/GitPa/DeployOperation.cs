namespace GitPa;

/// <summary>Syncs dev, builds one plug-in assembly, and pushes it to the dev environment with pac.</summary>
public static class DeployOperation
{
    public static IReadOnlyList<string> Plan(
        GitRunner git, PaConfig config, DeployConfig deploy, DeployAssembly assembly, DeployTools tools, bool sync)
    {
        DevBranch.EnsureCheckedOut(git, config);
        var steps = new List<string>();
        if (sync)
            steps.Add("git pa sync");
        if (deploy.Solution is not null)
            steps.Add(Format(tools.NuGet ?? "nuget.exe (not found)", RestoreArgs(deploy)));
        steps.Add(Format(tools.MSBuild ?? "MSBuild.exe (not found)", BuildArgs(deploy, assembly)));
        steps.Add(Format(tools.Pac ?? "pac (not found)", PushArgs(deploy, assembly)));
        return steps;
    }

    public static void Run(GitRunner git, PaConfig config, DeployConfig deploy, DeployAssembly assembly,
        DeployTools tools, bool sync, IToolRunner runner, IReporter reporter)
    {
        DevBranch.EnsureCheckedOut(git, config);
        var msbuild = tools.MSBuild ?? throw new GitPaException(
            $"Visual Studio's MSBuild was not found. Install Visual Studio (or its Build Tools), or set \"msbuild\" in {DeployConfig.FileName}.");
        var pac = tools.Pac ?? throw new GitPaException(
            "pac was not found. Install the Power Platform CLI: https://aka.ms/PowerPlatformCLI");
        var root = git.WorkingDirectory;

        if (sync)
        {
            var (pulled, resetBackup) = SyncOperation.Run(git, config, reporter);
            if (pulled > 0 || resetBackup is not null)
                reporter.Step($"Synced {config.Dev} with {config.Remote}/{config.Dev}");
        }

        if (deploy.Solution is not null)
        {
            if (tools.NuGet is null)
                reporter.Warn("nuget.exe was not found, so NuGet packages were not restored.");
            else
            {
                reporter.Step($"Restoring NuGet packages for {deploy.Solution}");
                if (runner.Run(tools.NuGet, RestoreArgs(deploy), root) != 0)
                    throw new GitPaException("Restoring NuGet packages failed. Nothing was deployed.");
            }
        }

        reporter.Step($"Building {assembly.Project} ({deploy.Configuration})");
        var buildStarted = DateTime.UtcNow;
        if (runner.Run(msbuild, BuildArgs(deploy, assembly), root) != 0)
            throw new GitPaException("The build failed. Nothing was deployed.");

        var file = DeployConfig.Resolve(root, assembly.File);
        if (!File.Exists(file) || File.GetLastWriteTimeUtc(file) < buildStarted.AddSeconds(-2))
            throw new GitPaException(
                $"The build didn't produce {assembly.File}. Check its \"file\" path in {DeployConfig.FileName}. Nothing was deployed.");

        reporter.Step($"Pushing {assembly.Name} to {deploy.Environment}");
        if (runner.Run(pac, PushArgs(deploy, assembly), root) != 0)
            throw new GitPaException(
                $"pac plugin push failed. Check that pac is signed in with access to {deploy.Environment} (pac auth list).");
    }

    static string[] RestoreArgs(DeployConfig deploy) => ["restore", deploy.Solution!];

    static string[] BuildArgs(DeployConfig deploy, DeployAssembly assembly) =>
        [assembly.Project, "-t:Rebuild", $"-p:Configuration={deploy.Configuration}", "-nologo", "-v:minimal", "-clp:ErrorsOnly;Summary"];

    static string[] PushArgs(DeployConfig deploy, DeployAssembly assembly) =>
        ["plugin", "push", "--environment", deploy.Environment, "--pluginId", assembly.PluginId.ToString(),
            "--pluginFile", assembly.File, "--type", "Assembly"];

    static string Format(string tool, IEnumerable<string> args) =>
        string.Join(' ', new[] { tool }.Concat(args).Select(a => a.Any(char.IsWhiteSpace) ? $"\"{a}\"" : a));
}
