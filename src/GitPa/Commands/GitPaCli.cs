using System.CommandLine;

namespace GitPa.Commands;

public static class GitPaCli
{
    /// <param name="findTools">Locates MSBuild, pac and nuget for deploy; tests pass fakes.</param>
    /// <param name="toolRunner">Runs those tools; tests pass a fake.</param>
    /// <param name="connect">Connects to Dataverse for web resource deploys; tests pass a fake.</param>
    public static RootCommand Build(Cli cli, Func<string?, DeployTools>? findTools = null, IToolRunner? toolRunner = null,
        DataverseConnector? connect = null)
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
            CleanCommand.Create(cli),
            ResetDevCommand.Create(cli),
            DeployCommand.Create(cli, findTools ?? DeployTools.Find, toolRunner ?? new ProcessToolRunner(),
                connect ?? DataverseSignIn.Connect),
        };
        root.Options.OfType<VersionOption>().Single().Aliases.Add("-v");
        return root;
    }
}
