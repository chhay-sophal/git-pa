using System.CommandLine;
using Spectre.Console;

namespace GitPa.Commands;

static class InitCommand
{
    public static Command Create()
    {
        var mainOption = new Option<string>("--main") { Description = "Name of the main branch" };
        var devOption = new Option<string>("--dev") { Description = "Name of the dev branch" };
        var globalOption = new Option<bool>("--global")
        {
            Description = "Save to your global git config instead of this repository",
        };

        var command = new Command("init", "Configure the main and dev branches") { mainOption, devOption, globalOption };
        command.SetAction(parseResult => Cli.Execute(() =>
        {
            var global = parseResult.GetValue(globalOption);
            var git = global ? new GitRunner(Environment.CurrentDirectory) : GitRunner.OpenRepository(Environment.CurrentDirectory);

            var main = Cli.Require(parseResult.GetValue(mainOption), "--main",
                () => Ask(git, "Main branch:", PaConfig.MainKey, "main", "master"));
            var dev = Cli.Require(parseResult.GetValue(devOption), "--dev",
                () => Ask(git, "Dev branch:", PaConfig.DevKey, "dev", "develop"));

            foreach (var name in new[] { main, dev })
            {
                if (!git.IsValidBranchName(name))
                    throw new GitPaException($"'{name}' is not a valid branch name.");
            }

            PaConfig.Save(git, main, dev, global);
            WarnIfMissing(git, main, dev);
            Cli.Success($"Saved main = {main}, dev = {dev}{(global ? " (global)" : "")}");
            return 0;
        }));
        return command;
    }

    static string Ask(GitRunner git, string question, string key, params string[] candidates)
    {
        var current = PaConfig.Get(git, key)
            ?? candidates.FirstOrDefault(git.LocalBranchExists)
            ?? candidates[0];
        return AnsiConsole.Prompt(new TextPrompt<string>(question)
            .DefaultValue(current)
            .Validate(name => git.IsValidBranchName(name)
                ? ValidationResult.Success()
                : ValidationResult.Error("Not a valid branch name")));
    }

    static void WarnIfMissing(GitRunner git, params string[] branches)
    {
        if (!git.Succeeds("rev-parse", "--git-dir"))
            return;
        var remote = PaConfig.Get(git, PaConfig.RemoteKey) ?? PaConfig.DefaultRemote;
        foreach (var branch in branches.Where(b => !git.LocalBranchExists(b) && !git.RemoteBranchExists(remote, b)))
            Cli.Reporter.Warn($"Branch '{branch}' does not exist locally or on {remote} yet.");
    }
}
