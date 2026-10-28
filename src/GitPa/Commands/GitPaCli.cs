using System.CommandLine;

namespace GitPa.Commands;

public static class GitPaCli
{
    public static RootCommand Build(Cli cli)
    {
        var root = new RootCommand("Git workflow helper for Power Apps plugin developers sharing a dev environment.")
        {
            InitCommand.Create(cli),
            NewCommand.Create(cli),
            CommitCommand.Create(cli),
            SyncCommand.Create(cli),
            StatusCommand.Create(cli),
            UpdateCommand.Create(cli),
            PrCommand.Create(cli),
        };
        root.Options.OfType<VersionOption>().Single().Aliases.Add("-v");
        return root;
    }
}
