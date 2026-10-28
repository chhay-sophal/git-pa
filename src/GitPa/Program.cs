using System.CommandLine;

var root = new RootCommand("Git workflow helper for Power Apps plugin developers sharing a dev environment.");

return root.Parse(args).Invoke();
