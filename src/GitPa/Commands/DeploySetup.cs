using Spectre.Console;

namespace GitPa.Commands;

/// <summary>Interactive setup that writes git-pa.json from choices read from Dataverse and the repository.</summary>
static class DeploySetup
{
    static readonly PacSolution AnySolution = new("(any solution)", "", Guid.Empty);
    const string SkipProject = "(skip this assembly)";

    public static DeployConfig Run(Cli cli, GitRunner git, DeployTools tools, IToolRunner runner)
    {
        if (!cli.Interactive)
            throw new GitPaException("Setup asks questions, so it needs a terminal.");
        var pac = tools.Pac ?? throw new GitPaException(
            "Setup reads from Dataverse with the Power Platform CLI, which was not found: https://aka.ms/PowerPlatformCLI");
        var root = git.WorkingDirectory;
        var console = cli.Console;

        DeployConfig? existing = null;
        if (File.Exists(Path.Combine(root, DeployConfig.FileName)))
        {
            try
            {
                existing = DeployConfig.Load(root);
            }
            catch (GitPaException)
            {
                // a broken file gets replaced
            }
            if (!console.Confirm($"{DeployConfig.FileName} already exists. Replace it?", defaultValue: false))
                throw new GitPaException("Setup cancelled; nothing was changed.");
        }

        cli.Reporter.Step("Reading your environments (pac org list)");
        var environments = Pac.Environments(runner, pac, root);
        if (environments is not { Count: > 0 })
            throw new GitPaException("Couldn't list your environments. Sign in first with: pac auth create");
        var environment = console.Prompt(new SelectionPrompt<PacEnvironment>()
            .Title("Environment to deploy to:")
            .PageSize(15)
            .UseConverter(e => $"{Markup.Escape(e.Name)} [grey]{Markup.Escape(e.Url)}{(e.Active ? ", active in pac" : "")}[/]")
            .AddChoices(environments.OrderByDescending(e => e.Url == existing?.Environment).ThenByDescending(e => e.Active)));

        cli.Reporter.Step($"Reading solutions in {environment.Name}");
        var solutions = Pac.Solutions(runner, pac, environment.Url, root)
            ?? throw new GitPaException($"Couldn't read solutions from {environment.Url}. Check your access with: pac org who");
        var solution = console.Prompt(new SelectionPrompt<PacSolution>()
            .Title("Solution with your plug-in assemblies:")
            .PageSize(15)
            .UseConverter(s => s == AnySolution ? "[grey](any solution)[/]" : $"{Markup.Escape(s.Name)} [grey]{Markup.Escape(s.UniqueName)}[/]")
            .AddChoices(solutions.Append(AnySolution)));

        var assemblies = Pac.Assemblies(runner, pac, environment.Url, solution == AnySolution ? null : solution.Id, root)
            ?? throw new GitPaException("Couldn't read the plug-in assemblies.");
        if (assemblies.Count == 0)
            throw new GitPaException($"There are no plug-in assemblies in {solution.Name}.");
        var assemblyPrompt = new MultiSelectionPrompt<PacAssembly>()
            .Title("Assemblies to deploy from this repository:")
            .Required()
            .InstructionsText("[grey](space to toggle, enter to accept)[/]")
            .UseConverter(a => Markup.Escape(a.Name))
            .AddChoices(assemblies);
        foreach (var assembly in assemblies)
            assemblyPrompt.Select(assembly);
        var chosen = console.Prompt(assemblyPrompt);

        var configuration = console.Prompt(new TextPrompt<string>("Build configuration:")
            .DefaultValue(existing?.Configuration ?? "Release"));

        var projects = ProjectFinder.Projects(root);
        var deployAssemblies = new List<DeployAssembly>();
        foreach (var assembly in chosen)
        {
            var guess = ProjectFinder.ForAssembly(root, assembly.Name, configuration);
            if (guess is null)
            {
                if (projects.Count == 0)
                    throw new GitPaException("There are no .csproj files in this repository.");
                var project = console.Prompt(new SelectionPrompt<string>()
                    .Title($"No project builds an assembly named {Markup.Escape(assembly.Name)}. Which project builds it?")
                    .PageSize(15)
                    .AddChoices(projects.Append(SkipProject)));
                if (project == SkipProject)
                    continue;
                guess = ProjectFinder.Guess(root, project, configuration);
            }

            console.MarkupLine($"[bold]{Markup.Escape(assembly.Name)}[/]: built by {Markup.Escape(guess.Project)}" +
                (guess.UsesILMerge ? " [grey](with ILMerge)[/]" : ""));
            var file = console.Prompt(new TextPrompt<string>("  File to push:").DefaultValue(guess.File));
            deployAssemblies.Add(new DeployAssembly(assembly.Name, guess.Project, file.Trim(), assembly.Id));
        }
        if (deployAssemblies.Count == 0)
            throw new GitPaException("No assemblies were set up; nothing was saved.");

        var solutionFiles = Directory.EnumerateFiles(root, "*.sln").Concat(Directory.EnumerateFiles(root, "*.slnx"))
            .Select(Path.GetFileName).OfType<string>().Order().ToList();
        var solutionFile = solutionFiles.Count switch
        {
            0 => null,
            1 => solutionFiles[0],
            _ => console.Prompt(new SelectionPrompt<string>()
                .Title("Solution file to restore NuGet packages for:")
                .AddChoices(solutionFiles)),
        };

        var config = new DeployConfig(environment.Url, configuration, solutionFile, existing?.MSBuild, deployAssemblies);
        config.Save(root);
        cli.Success($"Saved {DeployConfig.FileName}. Commit it so your team deploys the same way: git add {DeployConfig.FileName}");
        return config;
    }
}
