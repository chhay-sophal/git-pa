using System.CommandLine;
using Spectre.Console;

namespace GitPa.Commands;

static class NewCommand
{
    public static Command Create()
    {
        var nameArgument = new Argument<string>("name")
        {
            Description = "Name of the new feature branch",
            Arity = ArgumentArity.ZeroOrOne,
        };

        var command = new Command("new", "Create a feature branch from the latest main, staying on dev") { nameArgument };
        command.SetAction(parseResult => Cli.Execute(() =>
        {
            var git = GitRunner.OpenRepository(Environment.CurrentDirectory);
            var config = PaConfig.Load(git);

            var name = Cli.Require(parseResult.GetValue(nameArgument), "branch name",
                () => AnsiConsole.Prompt(new TextPrompt<string>("New branch name:")
                    .Validate(n => git.IsValidBranchName(n)
                        ? ValidationResult.Success()
                        : ValidationResult.Error("Not a valid branch name"))));

            var baseRef = NewBranchOperation.Run(git, config, name, Cli.Reporter);
            Cli.Success($"Created {name} from {baseRef}. You are still on {git.CurrentBranch() ?? "a detached HEAD"}.");
            return 0;
        }));
        return command;
    }
}
