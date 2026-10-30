using System.CommandLine;
using System.Diagnostics;
using Spectre.Console;

namespace GitPa.Commands;

static class DeployCommand
{
    const string WebResourcesChoice = "Web resources";

    /// <param name="findTools">Locates MSBuild (given an optional configured path), pac and nuget.</param>
    /// <param name="connect">Connects to Dataverse for web resources, signing in if needed.</param>
    public static Command Create(Cli cli, Func<string?, DeployTools> findTools, IToolRunner runner, DataverseConnector connect)
    {
        var namesArgument = new Argument<string[]>("name")
        {
            Description = $"Assembly to deploy, as named in {DeployConfig.FileName}; with --web-resources, the web resources " +
                "to deploy, by name or linked file (* and ? work as wildcards)",
            Arity = ArgumentArity.ZeroOrMore,
        };
        var webResourcesOption = new Option<bool>("--web-resources", "-w")
        {
            Description = "Publish the linked web resources that changed, instead of an assembly",
        };
        var yesOption = new Option<bool>("--yes", "-y") { Description = "Don't ask for confirmation" };
        var skipSyncOption = new Option<bool>("--skip-sync") { Description = "Deploy without syncing dev first" };
        var setupOption = new Option<bool>("--setup")
        {
            Description = $"Choose the environment and assemblies from Dataverse and save them to {DeployConfig.FileName}",
        };
        var signOutOption = new Option<bool>("--sign-out")
        {
            Description = "Forget the Dataverse sign-in used for web resources",
        };
        var dryRunOption = Cli.DryRunOption();

        var command = new Command("deploy", "Sync dev, then build and push a plug-in assembly or publish changed web resources")
        {
            namesArgument, webResourcesOption, yesOption, skipSyncOption, setupOption, signOutOption, dryRunOption,
        };
        command.SetAction(parseResult => cli.Execute(() =>
        {
            if (parseResult.GetValue(signOutOption))
            {
                var forgotten = DataverseSignIn.SignOut();
                cli.Success(forgotten > 0 ? "Signed out of Dataverse." : "You weren't signed in to Dataverse.");
                return 0;
            }

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

            var sync = !parseResult.GetValue(skipSyncOption);
            var dryRun = parseResult.GetValue(dryRunOption);
            var yes = parseResult.GetValue(yesOption);
            var names = parseResult.GetValue(namesArgument) ?? [];
            var webResources = parseResult.GetValue(webResourcesOption);
            if (webResources)
                return DeployWebResources(cli, git, config, deploy, names, sync, dryRun, yes, connect);

            if (names.Length > 1)
                throw new GitPaException("Deploy one assembly at a time. To pick web resources, add --web-resources.");
            var name = names.FirstOrDefault();
            if (name is not null && !deploy.Assemblies.Any(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)) &&
                deploy.WebResources.Any(l => WebResourceDeploy.Matches(l, name, git.WorkingDirectory, cli.WorkingDirectory)))
                throw new GitPaException($"'{name}' is a web resource. Deploy it with: git pa deploy --web-resources {name}");

            if (name is null)
            {
                var choices = deploy.Assemblies.Select(a => a.Name).ToList();
                if (deploy.WebResources.Count > 0)
                    choices.Add(WebResourcesChoice);
                var choice = choices.Count == 1 ? choices[0]
                    : cli.Require(null, "assembly name (or --web-resources)", () => cli.Console.Prompt(new SelectionPrompt<string>()
                        .Title("What to deploy:")
                        .AddChoices(choices)));
                webResources = choice == WebResourcesChoice && deploy.WebResources.Count > 0;
                name = webResources ? null : choice;
            }
            if (webResources)
                return DeployWebResources(cli, git, config, deploy, [], sync, dryRun, yes, connect);

            var assembly = deploy.Find(name!);
            var tools = findTools(deploy.MSBuild);
            if (dryRun)
            {
                cli.PrintPlan(DeployOperation.Plan(git, config, deploy, assembly, tools, sync));
                PreviewClassCheck(cli, git, deploy, assembly, tools, runner);
                return 0;
            }

            cli.Console.MarkupLine($"Deploy [bold]{Markup.Escape(assembly.Name)}[/] to [bold]{Markup.Escape(deploy.Environment)}[/]");
            if (git.Run("status", "--porcelain").Length > 0)
                cli.Console.MarkupLine("[grey]Your uncommitted changes are included in this build.[/]");
            if (!Confirmed(cli, yes))
                return 1;

            var stopwatch = Stopwatch.StartNew();
            DeployOperation.Run(git, config, deploy, assembly, tools, sync, runner, cli.Reporter);
            cli.Success($"Deployed {assembly.Name} to {deploy.Environment} in {stopwatch.Elapsed:m\\:ss}.");
            return 0;
        }));
        return command;
    }

    static bool Confirmed(Cli cli, bool yes)
    {
        if (yes)
            return true;
        if (!cli.Interactive)
            throw new GitPaException("Use --yes to deploy without confirmation, or --dry-run to preview.");
        if (cli.Console.Confirm("Continue?", defaultValue: false))
            return true;
        cli.Console.MarkupLine("Nothing was deployed.");
        return false;
    }

    /// <summary>
    /// Syncs dev, compares the linked files (all, or those matching <paramref name="patterns"/>) with their
    /// web resources, and uploads and publishes the changed ones: in a terminal, the ones you keep ticked.
    /// Unlike for an assembly, what changed is only known after syncing, so the choice comes after the sync.
    /// </summary>
    static int DeployWebResources(Cli cli, GitRunner git, PaConfig config, DeployConfig deploy, IReadOnlyList<string> patterns,
        bool sync, bool dryRun, bool yes, DataverseConnector connect)
    {
        if (deploy.WebResources.Count == 0)
            throw new GitPaException(
                $"{DeployConfig.FileName} has no linked web resources. Link them with the wrsync VS Code extension " +
                "(Share Links with the Team).");
        var links = WebResourceDeploy.Select(deploy.WebResources, patterns, git.WorkingDirectory, cli.WorkingDirectory);

        if (dryRun)
            cli.Console.MarkupLine("[yellow]Dry run[/], nothing will be changed" + (sync ? $" ({config.Dev} is not synced first)." : "."));
        else if (sync)
        {
            var (pulled, resetBackup) = SyncOperation.Run(git, config, cli.Reporter);
            if (pulled > 0 || resetBackup is not null)
                cli.Reporter.Step($"Synced {config.Dev} with {config.Remote}/{config.Dev}");
        }

        var dataverse = connect(deploy.Environment, cli.Interactive, cli.Reporter);
        cli.Reporter.Step($"Comparing {WebResourceDeploy.Plural(links.Count, "linked web resource")} with {deploy.Environment}");
        var comparison = WebResourceDeploy.Compare(git.WorkingDirectory, links, dataverse);
        if (comparison.MissingFiles.Count > 0)
            cli.Reporter.Warn("Skipped, the linked file doesn't exist:\n  " +
                string.Join("\n  ", comparison.MissingFiles.Select(l => $"{l.Name} → {l.File}")));
        if (comparison.NotInEnvironment.Count > 0)
            cli.Reporter.Warn($"Skipped, not in {deploy.Environment} (create them there first):\n  " +
                string.Join("\n  ", comparison.NotInEnvironment.Select(l => l.Name)));
        if (comparison.Managed.Count > 0)
            cli.Reporter.Warn("Skipped, managed (from an installed solution) so they can't be changed:\n  " +
                string.Join("\n  ", comparison.Managed.Select(l => l.Name)));

        if (comparison.Changed.Count == 0)
        {
            cli.Success($"Nothing to publish: the linked web resources already match {deploy.Environment}.");
            return 0;
        }

        var me = dataverse.UserId();
        bool BySomeoneElse(WebResourceChange change) => change.Record.ModifiedById is { } by && by != me;
        string LastChanged(WebResourceChange change)
        {
            var when = change.Record.ModifiedOn is { } on ? on.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "";
            var who = Markup.Escape(change.Record.ModifiedByName ?? "");
            return BySomeoneElse(change) ? $"[yellow]{who}[/] {when}" : $"[grey]{who} {when}[/]";
        }

        var changed = comparison.Changed;
        cli.Console.MarkupLine(
            $"{(dryRun ? "Would publish" : "Changed:")} [bold]{WebResourceDeploy.Plural(changed.Count, "web resource")}[/] " +
            $"{(dryRun ? "to" : "compared with")} [bold]{Markup.Escape(deploy.Environment)}[/] [grey]({comparison.Unchanged} unchanged)[/]");
        var choose = !dryRun && !yes && cli.Interactive;
        if (!choose)
        {
            var table = new Table().Border(TableBorder.Rounded).AddColumns("Web resource", "File", "Last changed in Dataverse");
            foreach (var change in changed)
                table.AddRow(Markup.Escape(change.Link.Name), Markup.Escape(change.Link.File), LastChanged(change));
            cli.Console.Write(table);
        }
        if (changed.Any(BySomeoneElse))
            cli.Console.MarkupLine(
                "[yellow]Someone else changed the highlighted ones in Dataverse last.[/] If they edited them there " +
                "directly rather than through git, publishing replaces their changes" + (choose ? "; untick them to leave them out." : "."));
        if (dryRun)
            return 0;
        if (!yes && !cli.Interactive)
            throw new GitPaException("Use --yes to deploy without confirmation, or --dry-run to preview.");

        if (git.Run("status", "--porcelain").Length > 0)
            cli.Console.MarkupLine("[grey]Your uncommitted changes are included.[/]");
        if (choose)
        {
            var prompt = new MultiSelectionPrompt<WebResourceChange>()
                .Title($"Web resources to publish to [bold]{Markup.Escape(deploy.Environment)}[/]:")
                .NotRequired()
                .PageSize(20)
                .InstructionsText("[grey](space to toggle, enter to publish the ticked ones; untick all to cancel)[/]")
                .UseConverter(c => $"{Markup.Escape(c.Link.Name)} [grey]{Markup.Escape(c.Link.File)} ·[/] {LastChanged(c)}")
                .AddChoices(changed);
            foreach (var change in changed)
                prompt.Select(change);
            changed = cli.Console.Prompt(prompt);
            if (changed.Count == 0)
            {
                cli.Console.MarkupLine("Nothing was deployed.");
                return 1;
            }
        }

        var stopwatch = Stopwatch.StartNew();
        WebResourceDeploy.Publish(changed, dataverse, cli.Reporter);
        cli.Success($"Published {WebResourceDeploy.Plural(changed.Count, "web resource")} to {deploy.Environment} in {stopwatch.Elapsed:m\\:ss}.");
        return 0;
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
