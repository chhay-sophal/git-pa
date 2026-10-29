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
        steps.Add("compare the built plug-in classes with the registered ones (pac org fetch); stop if any registered class is missing");
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

        reporter.Step($"Comparing plug-in classes with the ones registered in {deploy.Environment}");
        var check = CheckClasses(git, deploy, assembly, pac, runner);
        if (check.Registered is null)
            reporter.Warn("Couldn't read the registered plug-in classes, so new or removed classes weren't checked.");
        if (check.Missing.Count > 0)
            throw new GitPaException(
                $"These classes are registered for {assembly.Name} in {deploy.Environment} but are not in this build:\n" +
                $"  {string.Join("\n  ", check.Missing)}\n" +
                "Pushing would break their registered steps, so nothing was deployed. If you removed or renamed them on " +
                "purpose, unregister them (and their steps) in the Plugin Registration Tool first. Otherwise, make sure " +
                $"your {config.Dev} has everyone's work (git pa sync).");

        reporter.Step($"Pushing {assembly.Name} to {deploy.Environment}");
        if (runner.Run(pac, PushArgs(deploy, assembly), root) != 0)
            throw new GitPaException(
                $"pac plugin push failed. Check that pac is signed in with access to {deploy.Environment} (pac auth list).");

        if (check.New.Count > 0)
            ReportNewClasses(deploy, assembly, check.New, QueryRegistered(git, deploy, assembly, pac, runner), reporter);
    }

    /// <param name="Registered">Classes registered in Dataverse, or null when they couldn't be read.</param>
    public sealed record ClassCheck(IReadOnlySet<string> Built, IReadOnlySet<string>? Registered)
    {
        /// <summary>Registered classes missing from the build.</summary>
        public IReadOnlyList<string> Missing => Registered is null ? [] : Registered.Except(Built).Order().ToList();

        /// <summary>Classes in the build that aren't registered yet.</summary>
        public IReadOnlyList<string> New => Registered is null ? [] : Built.Except(Registered).Order().ToList();
    }

    /// <summary>Compares the classes in the built assembly with the plug-in types registered in Dataverse.</summary>
    public static ClassCheck CheckClasses(GitRunner git, DeployConfig deploy, DeployAssembly assembly, string pac, IToolRunner runner) =>
        new(PluginClasses.Read(DeployConfig.Resolve(git.WorkingDirectory, assembly.File)),
            QueryRegistered(git, deploy, assembly, pac, runner));

    static void ReportNewClasses(DeployConfig deploy, DeployAssembly assembly, IReadOnlyList<string> added,
        IReadOnlySet<string>? registeredNow, IReporter reporter)
    {
        var registered = registeredNow is null ? [] : added.Where(registeredNow.Contains).ToList();
        var unregistered = registeredNow is null ? [] : added.Except(registered).ToList();
        if (registered.Count > 0)
            reporter.Warn(
                $"New classes, now registered by the push:\n  {string.Join("\n  ", registered)}\n" +
                "They don't run until you register their steps in the Plugin Registration Tool.");
        if (unregistered.Count > 0 || registeredNow is null)
            reporter.Warn(
                $"New classes that are not registered yet:\n  {string.Join("\n  ", registeredNow is null ? added : unregistered)}\n" +
                $"In the Plugin Registration Tool, select {assembly.Name}, choose Update, tick them, and register their steps.");
    }

    /// <summary>The plug-in types registered for the assembly, read with pac org fetch; null if that fails.</summary>
    static IReadOnlySet<string>? QueryRegistered(GitRunner git, DeployConfig deploy, DeployAssembly assembly, string pac, IToolRunner runner)
    {
        var fetch = "<fetch><entity name='plugintype'><attribute name='typename'/><filter>" +
            $"<condition attribute='pluginassemblyid' operator='eq' value='{assembly.PluginId}'/></filter></entity></fetch>";
        var result = runner.Capture(pac, ["org", "fetch", "--environment", deploy.Environment, "--xml", fetch], git.WorkingDirectory);
        if (result.ExitCode != 0)
            return null;

        // pac prints a table: some connection lines, a header starting with "typename", then one row per type.
        var lines = result.Output.Split('\n', StringSplitOptions.TrimEntries);
        var header = Array.FindIndex(lines, l => l.StartsWith("typename", StringComparison.OrdinalIgnoreCase));
        if (header < 0)
            return lines.Any(l => l.Contains("No records", StringComparison.OrdinalIgnoreCase))
                ? new HashSet<string>()
                : null;
        return lines.Skip(header + 1)
            .Where(l => l.Length > 0)
            .Select(l => l.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries)[0])
            .ToHashSet(StringComparer.Ordinal);
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
