using System.CommandLine;

namespace GitPa.Commands;

static class UpdateCommand
{
    public static Command Create(Cli cli)
    {
        var branchArgument = new Argument<string>("branch")
        {
            Description = "Feature branch to update",
            Arity = ArgumentArity.ZeroOrOne,
        };
        var allOption = new Option<bool>("--all", "-a") { Description = "Update every local feature branch" };
        var pushOption = new Option<bool>("--push") { Description = "Push after updating, regardless of pa.autoPush" };
        var noPushOption = new Option<bool>("--no-push") { Description = "Do not push, regardless of pa.autoPush" };
        var dryRunOption = Cli.DryRunOption();

        var command = new Command("update", "Merge the latest main into a feature branch without switching to it")
        {
            branchArgument, allOption, pushOption, noPushOption, dryRunOption,
        };
        command.SetAction(parseResult => cli.Execute(() =>
        {
            if (parseResult.GetValue(pushOption) && parseResult.GetValue(noPushOption))
                throw new GitPaException("Use either --push or --no-push, not both.");

            var git = cli.OpenRepository();
            var config = PaConfig.Load(git);
            var push = parseResult.GetValue(pushOption) || (!parseResult.GetValue(noPushOption) && config.AutoPush);

            IReadOnlyList<string> branches;
            if (parseResult.GetValue(allOption))
                branches = Branches.LocalFeatures(git, config);
            else
                branches = [cli.Require(parseResult.GetValue(branchArgument), "branch name",
                    () => Prompts.PickFeatureBranch(cli, git, config, "Branch to update:"))];

            if (parseResult.GetValue(dryRunOption))
                return cli.PrintPlan(branches.SelectMany(b => UpdateOperation.Plan(git, config, b, push)).ToList());

            var failed = 0;
            foreach (var branch in branches)
            {
                try
                {
                    var merged = UpdateOperation.Run(git, config, branch, push, cli.Reporter);
                    cli.Success(merged == 0
                        ? $"{branch} is already up to date with {config.Main}."
                        : $"Merged {merged} commit{(merged == 1 ? "" : "s")} from {config.Main} into {branch}.");
                }
                catch (GitPaException e) when (branches.Count > 1)
                {
                    // With --all, report each failure and carry on with the other branches.
                    cli.Reporter.Warn($"{branch}: {e.Message}");
                    failed++;
                }
            }
            return failed == 0 ? 0 : 1;
        }));
        return command;
    }
}
