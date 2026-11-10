using System.CommandLine;
using Spectre.Console;

namespace GitPa.Commands;

static class NewCommand
{
    public static Command Create(Cli cli)
    {
        var nameArgument = new Argument<string>("name")
        {
            Description = "Branch name, following your team's naming convention",
            Arity = ArgumentArity.ZeroOrOne,
        };
        var fromOption = new Option<string>("--from")
        {
            Description = "Feature branch to build on, when this one needs its unmerged work (default: main)",
        };
        var dryRunOption = Cli.DryRunOption();

        var command = new Command("new", "Create a branch from the latest main (or another feature branch), staying on dev")
        {
            nameArgument, fromOption, dryRunOption,
        };
        command.SetAction(parseResult => cli.Execute(() =>
        {
            var git = cli.OpenRepository();
            var config = PaConfig.Load(git);

            var name = cli.Require(parseResult.GetValue(nameArgument), "branch name", () => AskName(cli, git));
            var from = parseResult.GetValue(fromOption) ?? AskFrom(cli, git, config);

            if (parseResult.GetValue(dryRunOption))
                return cli.PrintPlan(NewBranchOperation.Plan(git, config, name, from));

            var baseRef = NewBranchOperation.Run(git, config, name, cli.Reporter, from);
            cli.Success($"Created {name} from {baseRef}. You are still on {git.CurrentBranch() ?? "a detached HEAD"}.");
            if (Stack.SavedBase(git, name) is { } stackedOn)
                cli.Console.MarkupLine(
                    $"[grey]It builds on {Markup.Escape(stackedOn)}: git pa update brings in its new commits, " +
                    $"and merging {Markup.Escape(stackedOn)} to {Markup.Escape(config.Main)} first keeps this branch's pull request small.[/]");
            return 0;
        }));
        return command;
    }

    /// <summary>
    /// Asks what the branch starts from when there are feature branches it could build on; main is the
    /// default. Null (main) when not interactive.
    /// </summary>
    static string? AskFrom(Cli cli, GitRunner git, PaConfig config)
    {
        if (!cli.Interactive)
            return null;
        var features = Branches.LocalFeatures(git, config);
        if (features.Count == 0)
            return null;
        var mainChoice = $"{config.Main} (most branches)";
        var choice = cli.Console.Prompt(new SelectionPrompt<string>()
            .Title("Start from:")
            .PageSize(10)
            .AddChoices([mainChoice, .. features]));
        return choice == mainChoice ? null : choice;
    }

    static string AskName(Cli cli, GitRunner git) =>
        cli.Console.Prompt(new TextPrompt<string>("Branch name:")
            .Validate(name => !git.IsValidBranchName(name.Trim()) ? ValidationResult.Error("Not a valid branch name")
                : git.LocalBranchExists(name.Trim()) ? ValidationResult.Error("That branch already exists")
                : ValidationResult.Success()))
            .Trim();
}
