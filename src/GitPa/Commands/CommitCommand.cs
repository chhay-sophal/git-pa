using System.CommandLine;
using Spectre.Console;

namespace GitPa.Commands;

static class CommitCommand
{
    public static Command Create()
    {
        var branchOption = new Option<string>("--branch", "-b") { Description = "Feature branch to commit to" };
        var summaryOption = new Option<string>("--summary", "-m") { Description = "Commit summary" };
        var descriptionOption = new Option<string>("--description", "-d") { Description = "Commit description" };
        var allOption = new Option<bool>("--all", "-a") { Description = "Stage all changes before committing" };
        var pushOption = new Option<bool>("--push") { Description = "Push after committing, regardless of pa.autoPush" };
        var noPushOption = new Option<bool>("--no-push") { Description = "Do not push, regardless of pa.autoPush" };

        var command = new Command("commit", "Commit staged changes to a feature branch and merge it into dev")
        {
            branchOption, summaryOption, descriptionOption, allOption, pushOption, noPushOption,
        };
        command.SetAction(parseResult => Cli.Execute(() =>
        {
            if (parseResult.GetValue(pushOption) && parseResult.GetValue(noPushOption))
                throw new GitPaException("Use either --push or --no-push, not both.");

            var git = GitRunner.OpenRepository(Environment.CurrentDirectory);
            var config = PaConfig.Load(git);
            CommitOperation.EnsureOnDev(git, config);

            Stage(git, parseResult.GetValue(allOption));

            var target = Cli.Require(parseResult.GetValue(branchOption), "--branch", () => PickBranch(git, config));
            var summary = Cli.Require(parseResult.GetValue(summaryOption), "--summary",
                () => AnsiConsole.Prompt(new TextPrompt<string>("Commit summary:")
                    .Validate(s => string.IsNullOrWhiteSpace(s)
                        ? ValidationResult.Error("A summary is required")
                        : ValidationResult.Success())));
            var description = parseResult.GetValue(descriptionOption);
            if (description is null && Cli.Interactive)
                description = AnsiConsole.Prompt(new TextPrompt<string>("Commit description [grey](optional)[/]:").AllowEmpty());

            var push = parseResult.GetValue(pushOption) || (!parseResult.GetValue(noPushOption) && config.AutoPush);

            var sha = CommitOperation.Run(git, config, new CommitRequest(target, summary, description, push), Cli.Reporter);
            Cli.Success($"Committed {sha} to {target} and merged it into {config.Dev}.");
            return 0;
        }));
        return command;
    }

    static void Stage(GitRunner git, bool all)
    {
        if (all)
        {
            git.Run("add", "-A");
        }
        else if (!git.HasStagedChanges() && Cli.Interactive && git.Run("status", "--porcelain").Length > 0
                 && AnsiConsole.Confirm("Nothing is staged. Stage all changes?", defaultValue: false))
        {
            git.Run("add", "-A");
        }

        if (git.HasStagedChanges() && Cli.Interactive)
        {
            AnsiConsole.MarkupLine("[grey]Staged changes:[/]");
            AnsiConsole.WriteLine(git.Run("diff", "--cached", "--stat"));
        }
    }

    static string PickBranch(GitRunner git, PaConfig config)
    {
        var branches = git.LocalBranches().Where(b => b != config.Main && b != config.Dev).ToList();
        if (branches.Count == 0)
            throw new GitPaException("There are no feature branches yet. Create one with: git pa new <name>");
        return AnsiConsole.Prompt(new SelectionPrompt<string>()
            .Title("Target branch:")
            .PageSize(10)
            .AddChoices(branches));
    }
}
