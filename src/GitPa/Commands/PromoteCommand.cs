using System.CommandLine;

namespace GitPa.Commands;

static class PromoteCommand
{
    public static Command Create(Cli cli)
    {
        var branchArgument = new Argument<string>("branch")
        {
            Description = "Feature branch to promote",
            Arity = ArgumentArity.ZeroOrOne,
        };
        var toOption = new Option<string>("--to")
        {
            Description = $"Stage to merge it into (default: the first one it isn't in yet, from {PaConfig.StagesKey})",
        };
        var pushOption = new Option<bool>("--push") { Description = "Push after merging, regardless of pa.autoPush" };
        var noPushOption = new Option<bool>("--no-push") { Description = "Do not push, regardless of pa.autoPush" };
        var dryRunOption = Cli.DryRunOption();

        var command = new Command("promote", "Merge a feature branch into the next stage (like uat) without switching to it")
        {
            branchArgument, toOption, pushOption, noPushOption, dryRunOption,
        };
        command.SetAction(parseResult => cli.Execute(() =>
        {
            if (parseResult.GetValue(pushOption) && parseResult.GetValue(noPushOption))
                throw new GitPaException("Use either --push or --no-push, not both.");

            var git = cli.OpenRepository();
            var config = PaConfig.Load(git);
            var push = parseResult.GetValue(pushOption) || (!parseResult.GetValue(noPushOption) && config.AutoPush);
            PromoteOperation.ResolveStage(config, parseResult.GetValue(toOption)); // explains how to set up stages

            var branch = cli.Require(parseResult.GetValue(branchArgument), "branch name",
                () => Prompts.PickFeatureBranch(cli, git, config, "Branch to promote:"));
            Branches.EnsureFeature(config, branch);

            var stage = parseResult.GetValue(toOption);
            if (stage is null)
            {
                stage = PromoteOperation.NextStage(git, config, branch);
                if (stage is null)
                {
                    cli.Success($"{branch} is already in {string.Join(" and ", config.Stages)}. " +
                        $"Open its pull request to {config.Main} with: git pa pr {branch}");
                    return 0;
                }
            }

            if (parseResult.GetValue(dryRunOption))
                return cli.PrintPlan(PromoteOperation.Plan(git, config, branch, stage, push));

            var result = PromoteOperation.Run(git, config, branch, stage, push, cli.Reporter);
            cli.Success(result.AlreadyThere
                ? $"{branch} is already in {result.Stage}."
                : $"Merged {branch} into {result.Stage}" +
                  (result.Commits > 0 ? $" ({result.Commits} commit{(result.Commits == 1 ? "" : "s")})." : "."));
            return 0;
        }));
        return command;
    }
}
