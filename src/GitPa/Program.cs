using System.CommandLine;
using GitPa.Commands;

var root = new RootCommand("Git workflow helper for Power Apps plugin developers sharing a dev environment.")
{
    InitCommand.Create(),
};

return root.Parse(args).Invoke();
