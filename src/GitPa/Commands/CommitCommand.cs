using System.CommandLine;
using Spectre.Console;

namespace GitPa.Commands;

static class CommitCommand
{
    public static Command Create(Cli cli)
    {
        var branchOption = new Option<string>("--branch", "-b") { Description = "Feature branch to commit to" };
        var summaryOption = new Option<string>("--summary", "-m") { Description = "Commit summary" };
        var descriptionOption = new Option<string[]>("--description", "-d")
        {
            Description = "Commit description; repeat for more paragraphs",
        };
        var allOption = new Option<bool>("--all", "-a") { Description = "Commit all changes instead of choosing files" };
        var pushOption = new Option<bool>("--push") { Description = "Push after committing, regardless of pa.autoPush" };
        var noPushOption = new Option<bool>("--no-push") { Description = "Do not push, regardless of pa.autoPush" };
        var dryRunOption = Cli.DryRunOption();

        var command = new Command("commit", "Commit changes to a feature branch and merge it into dev")
        {
            branchOption, summaryOption, descriptionOption, allOption, pushOption, noPushOption, dryRunOption,
        };
        command.SetAction(parseResult => cli.Execute(() =>
        {
            if (parseResult.GetValue(pushOption) && parseResult.GetValue(noPushOption))
                throw new GitPaException("Use either --push or --no-push, not both.");

            var git = cli.OpenRepository();
            var config = PaConfig.Load(git);
            CommitOperation.EnsureOnDev(git, config);

            var branch = parseResult.GetValue(branchOption);
            var summary = parseResult.GetValue(summaryOption);
            if (!cli.Interactive)
            {
                // Check required options before staging anything.
                foreach (var (value, option) in new[] { (branch, "--branch"), (summary, "--summary") })
                {
                    if (string.IsNullOrWhiteSpace(value))
                        throw new GitPaException($"Missing {option}.");
                }
            }

            // A dry run plans with what is staged now (or everything, with --all) and stages nothing.
            var all = parseResult.GetValue(allOption);
            var dryRun = parseResult.GetValue(dryRunOption);
            if (all && !dryRun)
                git.Run("add", "-A");
            else if (!all && !dryRun && cli.Interactive)
                PickFiles(cli, git);

            var target = cli.Require(branch, "--branch", () => Prompts.PickFeatureBranch(cli, git, config, "Target branch:"));
            summary = cli.Require(summary, "--summary",
                () => cli.Console.Prompt(new TextPrompt<string>("Commit summary:")
                    .Validate(s => string.IsNullOrWhiteSpace(s)
                        ? ValidationResult.Error("A summary is required")
                        : ValidationResult.Success())));
            var paragraphs = parseResult.GetValue(descriptionOption);
            var description = paragraphs is { Length: > 0 }
                ? string.Join("\n\n", paragraphs) // like repeated git commit -m
                : cli.Interactive ? PromptDescription(cli) : null;

            var push = parseResult.GetValue(pushOption) || (!parseResult.GetValue(noPushOption) && config.AutoPush);
            var request = new CommitRequest(target, summary, description, push);
            if (dryRun)
                return cli.PrintPlan(CommitOperation.Plan(git, config, request, stageAll: all));

            var sha = CommitOperation.Run(git, config, request, cli.Reporter);
            cli.Success($"Committed {sha} to {target} and merged it into {config.Dev}.");
            return 0;
        }));
        return command;
    }

    static void PickFiles(Cli cli, GitRunner git)
    {
        var files = ChangedFiles.List(git);
        if (files.Count == 0)
            throw new GitPaException("There are no changes to commit.");

        var prompt = new MultiSelectionPrompt<ChangedFile>()
            .Title("Files to commit:")
            .PageSize(15)
            .Required()
            .InstructionsText("[grey](space to toggle, enter to accept)[/]")
            .UseConverter(f => Markup.Escape($"{f.Kind,-9}{f.Path}") + (f.IsPartiallyStaged ? " [grey](partially staged)[/]" : ""))
            .AddChoices(files);
        foreach (var file in files.Where(f => f.IsStaged))
            prompt.Select(file);

        ChangedFiles.Stage(git, files, cli.Console.Prompt(prompt));
    }

    /// <summary>Reads description lines until an empty line.</summary>
    static string PromptDescription(Cli cli)
    {
        var lines = new List<string>();
        var question = "Commit description [grey](optional, empty line to finish)[/]:";
        while (true)
        {
            var line = cli.Console.Prompt(new TextPrompt<string>(question).AllowEmpty());
            if (string.IsNullOrWhiteSpace(line))
                return string.Join('\n', lines);
            lines.Add(line);
            question = "[grey]>[/]";
        }
    }
}
