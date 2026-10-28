using System.CommandLine;
using System.Diagnostics;
using Spectre.Console;

namespace GitPa.Commands;

static class DeployCommand
{
    public static Command Create(Cli cli, Func<DeployConfig, DeployTools> findTools, IToolRunner runner)
    {
        var assemblyArgument = new Argument<string>("assembly")
        {
            Description = $"Assembly to deploy, as named in {DeployConfig.FileName}",
            Arity = ArgumentArity.ZeroOrOne,
        };
        var yesOption = new Option<bool>("--yes", "-y") { Description = "Don't ask for confirmation" };
        var skipSyncOption = new Option<bool>("--skip-sync") { Description = "Build without syncing dev first" };
        var dryRunOption = Cli.DryRunOption();

        var command = new Command("deploy", "Sync dev, build a plug-in assembly, and push it to the dev environment")
        {
            assemblyArgument, yesOption, skipSyncOption, dryRunOption,
        };
        command.SetAction(parseResult => cli.Execute(() =>
        {
            var git = cli.OpenRepository();
            var config = PaConfig.Load(git);
            var deploy = DeployConfig.Load(git.WorkingDirectory);
            DevBranch.EnsureCheckedOut(git, config);

            var name = parseResult.GetValue(assemblyArgument);
            var assembly = name is not null ? deploy.Find(name)
                : deploy.Assemblies.Count == 1 ? deploy.Assemblies[0]
                : deploy.Find(cli.Require(null, "assembly name", () => cli.Console.Prompt(new SelectionPrompt<string>()
                    .Title("Assembly to deploy:")
                    .AddChoices(deploy.Assemblies.Select(a => a.Name)))));

            var tools = findTools(deploy);
            var sync = !parseResult.GetValue(skipSyncOption);
            if (parseResult.GetValue(dryRunOption))
                return cli.PrintPlan(DeployOperation.Plan(git, config, deploy, assembly, tools, sync));

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
}
