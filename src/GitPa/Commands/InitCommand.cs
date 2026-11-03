using System.CommandLine;
using Spectre.Console;

namespace GitPa.Commands;

static class InitCommand
{
    public static Command Create(Cli cli)
    {
        var mainOption = new Option<string>("--main") { Description = "Name of the main branch" };
        var devOption = new Option<string>("--dev") { Description = "Name of the dev branch" };
        var typesOption = new Option<string>("--branch-types")
        {
            Description = $"Branch types for `git pa new`, like \"{BranchTypes.Format(BranchTypes.Default)}\"; \"default\" resets them",
        };
        var globalOption = new Option<bool>("--global")
        {
            Description = "Save to your global git config instead of this repository",
        };

        var command = new Command("init", "Configure the main and dev branches") { mainOption, devOption, typesOption, globalOption };
        command.SetAction(parseResult => cli.Execute(() =>
        {
            var global = parseResult.GetValue(globalOption);
            var git = global ? new GitRunner(cli.WorkingDirectory) : cli.OpenRepository();

            var main = cli.Require(parseResult.GetValue(mainOption), "--main",
                () => Ask(cli, git, "Main branch:", PaConfig.MainKey, "main", "master"));
            var dev = cli.Require(parseResult.GetValue(devOption), "--dev",
                () => Ask(cli, git, "Dev branch:", PaConfig.DevKey, "dev", "develop"));

            foreach (var name in new[] { main, dev })
            {
                if (!git.IsValidBranchName(name))
                    throw new GitPaException($"'{name}' is not a valid branch name.");
            }

            var types = parseResult.GetValue(typesOption);
            if (types is not null && types.Trim() != "default")
                BranchTypes.Parse(types); // check before saving anything

            PaConfig.Save(git, main, dev, global);
            var savedTypes = types is null ? "" : $", branch types = {SaveBranchTypes(git, types.Trim(), global)}";
            WarnIfMissing(cli, git, main, dev);
            cli.Success($"Saved main = {main}, dev = {dev}{savedTypes}{(global ? " (global)" : "")}");
            return 0;
        }));
        return command;
    }

    static string Ask(Cli cli, GitRunner git, string question, string key, params string[] candidates)
    {
        var current = PaConfig.Get(git, key)
            ?? candidates.FirstOrDefault(git.LocalBranchExists)
            ?? candidates[0];
        return cli.Console.Prompt(new TextPrompt<string>(question)
            .DefaultValue(current)
            .Validate(name => git.IsValidBranchName(name)
                ? ValidationResult.Success()
                : ValidationResult.Error("Not a valid branch name")));
    }

    /// <summary>Saves pa.branchTypes ("default" unsets it) and returns what was saved.</summary>
    static string SaveBranchTypes(GitRunner git, string types, bool global)
    {
        var scope = global ? "--global" : "--local";
        if (types == "default")
        {
            git.TryRun("config", scope, "--unset", BranchTypes.Key); // fine when it isn't set
            return $"{BranchTypes.Format(BranchTypes.Default)} (default)";
        }
        var formatted = BranchTypes.Format(BranchTypes.Parse(types));
        git.Run("config", scope, BranchTypes.Key, formatted);
        return formatted;
    }

    static void WarnIfMissing(Cli cli, GitRunner git, params string[] branches)
    {
        if (!git.Succeeds("rev-parse", "--git-dir"))
            return;
        var remote = PaConfig.Get(git, PaConfig.RemoteKey) ?? PaConfig.DefaultRemote;
        foreach (var branch in branches.Where(b => !git.LocalBranchExists(b) && !git.RemoteBranchExists(remote, b)))
            cli.Reporter.Warn($"Branch '{branch}' does not exist locally or on {remote} yet.");
    }
}
