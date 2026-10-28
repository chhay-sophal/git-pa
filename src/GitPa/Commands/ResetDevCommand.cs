using System.CommandLine;
using Spectre.Console;

namespace GitPa.Commands;

static class ResetDevCommand
{
    public static Command Create(Cli cli)
    {
        var branchOption = new Option<string[]>("--branch", "-b")
        {
            Description = "Feature branch to keep in dev; repeat for more (default: the branches in dev now)",
        };
        var yesOption = new Option<bool>("--yes", "-y") { Description = "Don't ask for confirmation" };
        var dryRunOption = Cli.DryRunOption();

        var command = new Command("reset-dev", "Rebuild the shared dev branch from main plus the feature branches you pick")
        {
            branchOption, yesOption, dryRunOption,
        };
        command.SetAction(parseResult => cli.Execute(() =>
        {
            var git = cli.OpenRepository();
            var config = PaConfig.Load(git);
            DevBranch.EnsureCheckedOut(git, config);
            var dryRun = parseResult.GetValue(dryRunOption);

            if (!dryRun)
            {
                cli.Reporter.Step($"Fetching {config.Remote}");
                var fetch = git.TryRun("fetch", "--quiet", "--prune", config.Remote);
                if (!fetch.Success)
                    throw new GitPaException($"Could not fetch {config.Remote}:\n{fetch.Message}");
            }

            var candidates = ResetDevOperation.Candidates(git, config);
            var keep = Choose(cli, candidates, parseResult.GetValue(branchOption));
            if (dryRun)
                return cli.PrintPlan(ResetDevOperation.Plan(git, config, keep));

            var main = Branches.LatestMain(git, config);
            var left = candidates.Where(c => c.InDev && !keep.Contains(c)).Select(c => c.Name).ToList();
            cli.Console.MarkupLine($"New [bold]{Markup.Escape(config.Dev)}[/] = {Markup.Escape(main)}" +
                (keep.Count == 0 ? "" : " + " + Markup.Escape(string.Join(", ", keep.Select(c => c.Name)))));
            if (left.Count > 0)
                cli.Console.MarkupLine($"Removed from {Markup.Escape(config.Dev)}: [yellow]{Markup.Escape(string.Join(", ", left))}[/]");

            if (!parseResult.GetValue(yesOption))
            {
                if (!cli.Interactive)
                    throw new GitPaException("This rewrites the shared dev branch. Use --yes to confirm, or --dry-run to preview.");
                if (!cli.Console.Confirm(
                        $"This replaces {config.Remote}/{config.Dev} for everyone. Tell your teammates first. Continue?",
                        defaultValue: false))
                {
                    cli.Console.MarkupLine("Nothing was changed.");
                    return 1;
                }
            }

            var backup = ResetDevOperation.Run(git, config, keep, cli.Reporter);
            cli.Success($"Rebuilt {config.Dev} and pushed it. Your old {config.Dev} is saved as {backup}.");
            cli.Console.MarkupLine(
                $"Teammates: the next [bold]git pa sync[/] or [bold]git pa commit[/] moves their {Markup.Escape(config.Dev)} " +
                "onto the new one. Plain [bold]git pull[/] would merge the old history back in, so they shouldn't use it.");
            return 0;
        }));
        return command;
    }

    static IReadOnlyList<ResetCandidate> Choose(Cli cli, IReadOnlyList<ResetCandidate> candidates, string[]? names)
    {
        if (names is { Length: > 0 })
        {
            return names.Select(name => candidates.FirstOrDefault(c => c.Name == name)
                    ?? throw new GitPaException($"'{name}' was not found, or has no work that isn't already in main."))
                .ToList();
        }

        var inDev = candidates.Where(c => c.InDev).ToList();
        if (!cli.Interactive || candidates.Count == 0)
            return inDev;

        var prompt = new MultiSelectionPrompt<ResetCandidate>()
            .Title("Feature branches to keep in dev:")
            .PageSize(15)
            .NotRequired()
            .InstructionsText("[grey](space to toggle, enter to accept; branches in dev now are pre-selected)[/]")
            .UseConverter(c => $"{Markup.Escape(c.Name)} [grey]({Markup.Escape(c.LastCommit)}{(c.InDev ? ", in dev now" : "")})[/]")
            .AddChoices(candidates);
        foreach (var candidate in inDev)
            prompt.Select(candidate);
        return cli.Console.Prompt(prompt);
    }
}
