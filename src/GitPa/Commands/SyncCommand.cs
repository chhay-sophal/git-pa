using System.CommandLine;

namespace GitPa.Commands;

static class SyncCommand
{
    public static Command Create(Cli cli)
    {
        var dryRunOption = Cli.DryRunOption();

        var command = new Command("sync", "Pull the latest remote dev into dev, keeping your uncommitted changes")
        {
            dryRunOption,
        };
        command.SetAction(parseResult => cli.Execute(() =>
        {
            var git = cli.OpenRepository();
            var config = PaConfig.Load(git);
            if (parseResult.GetValue(dryRunOption))
                return cli.PrintPlan(SyncOperation.Plan(git, config));

            var pulled = SyncOperation.Run(git, config, cli.Reporter);
            cli.Success(pulled == 0
                ? $"{config.Dev} is already up to date with {config.Remote}/{config.Dev}."
                : $"Pulled {pulled} commit{(pulled == 1 ? "" : "s")} from {config.Remote}/{config.Dev}.");
            return 0;
        }));
        return command;
    }
}
