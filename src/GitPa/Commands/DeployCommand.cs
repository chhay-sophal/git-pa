using System.CommandLine;
using System.Diagnostics;
using Spectre.Console;

namespace GitPa.Commands;

static class DeployCommand
{
    /// <param name="findTools">Locates MSBuild (given an optional configured path), pac and nuget.</param>
    public static Command Create(Cli cli, Func<string?, DeployTools> findTools, IToolRunner runner)
    {
        var assemblyArgument = new Argument<string>("assembly")
        {
            Description = $"Assembly to deploy, as named in {DeployConfig.FileName}",
            Arity = ArgumentArity.ZeroOrOne,
        };
        var yesOption = new Option<bool>("--yes", "-y") { Description = "Don't ask for confirmation" };
        var skipSyncOption = new Option<bool>("--skip-sync") { Description = "Build without syncing dev first" };
        var setupOption = new Option<bool>("--setup")
        {
            Description = $"Choose the environment and assemblies from Dataverse and save them to {DeployConfig.FileName}",
        };
        var dryRunOption = Cli.DryRunOption();

        var command = new Command("deploy", "Sync dev, build a plug-in assembly, and push it to the dev environment")
        {
            assemblyArgument, yesOption, skipSyncOption, setupOption, dryRunOption,
        };
        command.SetAction(parseResult => cli.Execute(() =>
        {
            var git = cli.OpenRepository();
            var config = PaConfig.Load(git);

            if (parseResult.GetValue(setupOption))
            {
                DeploySetup.Run(cli, git, findTools(null), runner);
                return 0;
            }

            DeployConfig deploy;
            if (DeployConfig.FindFile(git.WorkingDirectory) is not null || !cli.Interactive)
                deploy = DeployConfig.Load(git.WorkingDirectory);
            else if (cli.Console.Confirm($"This repository has no {DeployConfig.FileName} yet. Set it up now?"))
                deploy = DeploySetup.Run(cli, git, findTools(null), runner);
            else
                return 1;
            if (DeployConfig.UsesLegacyFile(git.WorkingDirectory) && !parseResult.GetValue(dryRunOption))
                MoveLegacyFile(cli, git, deploy);
            DevBranch.EnsureCheckedOut(git, config);

            var name = parseResult.GetValue(assemblyArgument);
            var assembly = name is not null ? deploy.Find(name)
                : deploy.Assemblies.Count == 1 ? deploy.Assemblies[0]
                : deploy.Find(cli.Require(null, "assembly name", () => cli.Console.Prompt(new SelectionPrompt<string>()
                    .Title("Assembly to deploy:")
                    .AddChoices(deploy.Assemblies.Select(a => a.Name)))));

            var tools = findTools(deploy.MSBuild);
            var sync = !parseResult.GetValue(skipSyncOption);
            if (parseResult.GetValue(dryRunOption))
            {
                cli.PrintPlan(DeployOperation.Plan(git, config, deploy, assembly, tools, sync));
                PreviewClassCheck(cli, git, deploy, assembly, tools, runner);
                return 0;
            }

            cli.Console.MarkupLine($"Deploy [bold]{Markup.Escape(assembly.Name)}[/] to [bold]{Markup.Escape(deploy.Environment)}[/]");
            if (git.Run("status", "--porcelain").Length > 0)
                cli.Console.MarkupLine("[grey]Your uncommitted changes are included in this build.[/]");

            if (!parseResult.GetValue(yesOption))
            {
                if (!cli.Interactive)
                    throw new GitPaException("Use --yes to deploy without confirmation, or --dry-run to preview.");
                if (!cli.Console.Confirm("Continue?", defaultValue: false))
                {
                    cli.Console.MarkupLine("Nothing was deployed.");
                    return 1;
                }
            }

            var stopwatch = Stopwatch.StartNew();
            DeployOperation.Run(git, config, deploy, assembly, tools, sync, runner, cli.Reporter);
            cli.Success($"Deployed {assembly.Name} to {deploy.Environment} in {stopwatch.Elapsed:m\\:ss}.");
            return 0;
        }));
        return command;
    }

    /// <summary>Offers to rewrite git-pa.json (git-pa 0.2) as dataverse.json; without a terminal, only says so.</summary>
    static void MoveLegacyFile(Cli cli, GitRunner git, DeployConfig deploy)
    {
        var message = $"{DeployConfig.LegacyFileName} is the old name for {DeployConfig.FileName}, which other tools read too.";
        if (!cli.Interactive)
        {
            cli.Reporter.Warn($"{message} Run git pa deploy in a terminal to move it.");
            return;
        }
        if (!cli.Console.Confirm($"{message} Move it now?"))
            return;
        deploy.Save(git.WorkingDirectory);
        File.Delete(Path.Combine(git.WorkingDirectory, DeployConfig.LegacyFileName));
        cli.Success($"Moved to {DeployConfig.FileName}. Commit the change: git add {DeployConfig.FileName} {DeployConfig.LegacyFileName}");
    }

    /// <summary>For a dry run: compares the last build (if any) with the registered classes. Read-only.</summary>
    static void PreviewClassCheck(Cli cli, GitRunner git, DeployConfig deploy, DeployAssembly assembly, DeployTools tools, IToolRunner runner)
    {
        if (tools.Pac is null || !File.Exists(DeployConfig.Resolve(git.WorkingDirectory, assembly.File)))
            return;

        var check = DeployOperation.CheckClasses(git, deploy, assembly, tools.Pac, runner);
        if (check.Registered is null)
        {
            cli.Reporter.Warn("Couldn't read the registered plug-in classes from Dataverse.");
            return;
        }
        cli.Console.MarkupLine(
            $"Last build has {check.Built.Count} plug-in classes; {check.Registered.Count} are registered in {Markup.Escape(deploy.Environment)}.");
        if (check.Missing.Count > 0)
            cli.Console.MarkupLine($"[red]Registered but missing from the build:[/] {Markup.Escape(string.Join(", ", check.Missing))}");
        if (check.New.Count > 0)
            cli.Console.MarkupLine($"[yellow]New, not registered yet:[/] {Markup.Escape(string.Join(", ", check.New))}");
        if (check.Missing.Count == 0 && check.New.Count == 0)
            cli.Console.MarkupLine("[green]The build and the registration match.[/]");
    }
}
