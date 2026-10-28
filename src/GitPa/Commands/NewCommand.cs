using System.CommandLine;
using Spectre.Console;

namespace GitPa.Commands;

static class NewCommand
{
    public static Command Create(Cli cli)
    {
        var nameArgument = new Argument<string>("name")
        {
            Description = "Name of the new feature branch",
            Arity = ArgumentArity.ZeroOrOne,
        };

        var command = new Command("new", "Create a feature branch from the latest main, staying on dev") { nameArgument };
        command.SetAction(parseResult => cli.Execute(() =>
        {
            var git = cli.OpenRepository();
            var config = PaConfig.Load(git);

            var name = cli.Require(parseResult.GetValue(nameArgument), "branch name",
                () => cli.Console.Prompt(new TextPrompt<string>("New branch name:")
                    .Validate(n => git.IsValidBranchName(n)
                        ? ValidationResult.Success()
                        : ValidationResult.Error("Not a valid branch name"))));

            var baseRef = NewBranchOperation.Run(git, config, name, cli.Reporter);
            cli.Success($"Created {name} from {baseRef}. You are still on {git.CurrentBranch() ?? "a detached HEAD"}.");
            return 0;
        }));
        return command;
    }
}
