using System.CommandLine;
using System.Text;
using GitPa.Commands;

Console.OutputEncoding = Encoding.UTF8;

var root = new RootCommand("Git workflow helper for Power Apps plugin developers sharing a dev environment.")
{
    InitCommand.Create(),
    NewCommand.Create(),
    CommitCommand.Create(),
};
root.Options.OfType<VersionOption>().Single().Aliases.Add("-v");

return root.Parse(args).Invoke();
