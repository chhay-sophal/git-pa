using System.CommandLine;
using Spectre.Console;

namespace GitPa.Commands;

static class CommitCommand
{
    public static Command Create(Cli cli)
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
        command.SetAction(parseResult => cli.Execute(() =>
        {
            if (parseResult.GetValue(pushOption) && parseResult.GetValue(noPushOption))
                throw new GitPaException("Use either --push or --no-push, not both.");

            var git = cli.OpenRepository();
            var config = PaConfig.Load(git);
            CommitOperation.EnsureOnDev(git, config);

            Stage(cli, git, parseResult.GetValue(allOption));

            var target = cli.Require(parseResult.GetValue(branchOption), "--branch", () => PickBranch(cli, git, config));
            var summary = cli.Require(parseResult.GetValue(summaryOption), "--summary",
                () => cli.Console.Prompt(new TextPrompt<string>("Commit summary:")
                    .Validate(s => string.IsNullOrWhiteSpace(s)
                        ? ValidationResult.Error("A summary is required")
                        : ValidationResult.Success())));
            var description = parseResult.GetValue(descriptionOption);
            if (description is null && cli.Interactive)
                description = cli.Console.Prompt(new TextPrompt<string>("Commit description [grey](optional)[/]:").AllowEmpty());

            var push = parseResult.GetValue(pushOption) || (!parseResult.GetValue(noPushOption) && config.AutoPush);

            var sha = CommitOperation.Run(git, config, new CommitRequest(target, summary, description, push), cli.Reporter);
            cli.Success($"Committed {sha} to {target} and merged it into {config.Dev}.");
            return 0;
        }));
        return command;
    }

    static void Stage(Cli cli, GitRunner git, bool all)
    {
        if (all)
        {
            git.Run("add", "-A");
        }
        else if (!git.HasStagedChanges() && cli.Interactive && git.Run("status", "--porcelain").Length > 0
                 && cli.Console.Confirm("Nothing is staged. Stage all changes?", defaultValue: false))
        {
            git.Run("add", "-A");
        }

        if (git.HasStagedChanges() && cli.Interactive)
        {
            cli.Console.MarkupLine("[grey]Staged changes:[/]");
            cli.Console.WriteLine(git.Run("diff", "--cached", "--stat"));
        }
    }

    static string PickBranch(Cli cli, GitRunner git, PaConfig config)
    {
        var branches = git.LocalBranches().Where(b => b != config.Main && b != config.Dev).ToList();
        if (branches.Count == 0)
            throw new GitPaException("There are no feature branches yet. Create one with: git pa new <name>");
        return cli.Console.Prompt(new SelectionPrompt<string>()
            .Title("Target branch:")
            .PageSize(10)
            .AddChoices(branches));
    }
}
