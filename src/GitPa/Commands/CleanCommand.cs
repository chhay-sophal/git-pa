using System.CommandLine;
using Spectre.Console;

namespace GitPa.Commands;

static class CleanCommand
{
    public static Command Create(Cli cli)
    {
        var yesOption = new Option<bool>("--yes", "-y") { Description = "Delete all finished branches without asking" };
        var dryRunOption = Cli.DryRunOption();

        var command = new Command("clean", "Delete local feature branches whose work is already in main")
        {
            yesOption, dryRunOption,
        };
        command.SetAction(parseResult => cli.Execute(() =>
        {
            var git = cli.OpenRepository();
            var config = PaConfig.Load(git);
            var dryRun = parseResult.GetValue(dryRunOption);

            if (!dryRun && Branches.TryFetch(git, config, config.Main))
                cli.Reporter.Step($"Fetched {config.Remote}/{config.Main}");

            var candidates = CleanOperation.Find(git, config);
            if (candidates.Count == 0)
            {
                cli.Success("Nothing to clean up.");
                return 0;
            }
            if (dryRun)
                return cli.PrintPlan(candidates.Select(c => $"{GitRunner.Format("branch", "-D", c.Name)}   ({c.Reason})").ToList());

            IReadOnlyCollection<CleanCandidate> chosen;
            if (parseResult.GetValue(yesOption))
                chosen = candidates;
            else if (cli.Interactive)
            {
                var prompt = new MultiSelectionPrompt<CleanCandidate>()
                    .Title("Branches to delete:")
                    .PageSize(15)
                    .NotRequired()
                    .InstructionsText("[grey](space to toggle, enter to delete the selected branches)[/]")
                    .UseConverter(c => $"{Markup.Escape(c.Name)} [grey]({Markup.Escape(c.Reason)})[/]")
                    .AddChoices(candidates);
                foreach (var candidate in candidates)
                    prompt.Select(candidate);
                chosen = cli.Console.Prompt(prompt);
            }
            else
                throw new GitPaException("Use --yes to delete without asking, or --dry-run to only list the branches.");

            var failures = CleanOperation.Delete(git, chosen.Select(c => c.Name));
            foreach (var failure in failures)
                cli.Reporter.Warn(failure);
            var deleted = chosen.Count - failures.Count;
            cli.Success($"Deleted {deleted} branch{(deleted == 1 ? "" : "es")}.");
            return failures.Count == 0 ? 0 : 1;
        }));
        return command;
    }
}
