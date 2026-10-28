using System.CommandLine;

namespace GitPa.Commands;

static class SyncCommand
{
    public static Command Create(Cli cli)
    {
        var command = new Command("sync", "Pull the latest remote dev into dev, keeping your uncommitted changes");
        command.SetAction(_ => cli.Execute(() =>
        {
            var git = cli.OpenRepository();
            var config = PaConfig.Load(git);

            var pulled = SyncOperation.Run(git, config, cli.Reporter);
            cli.Success(pulled == 0
                ? $"{config.Dev} is already up to date with {config.Remote}/{config.Dev}."
                : $"Pulled {pulled} commit{(pulled == 1 ? "" : "s")} from {config.Remote}/{config.Dev}.");
            return 0;
        }));
        return command;
    }
}
