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
                branches = BasesFirst(git, config, Branches.LocalFeatures(git, config));
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
                    var result = UpdateOperation.Run(git, config, branch, push, cli.Reporter);
                    var sources = result.Base is null ? config.Main : $"{result.Base} and {config.Main}";
                    if (result.UpToDate)
                        cli.Success($"{branch} is already up to date with {sources}.");
                    else
                    {
                        var parts = new List<string>();
                        if (result.FromBase > 0)
                            parts.Add($"{Plural(result.FromBase)} from {result.Base}");
                        if (result.FromMain > 0)
                            parts.Add($"{Plural(result.FromMain)} from {config.Main}");
                        cli.Success($"Merged {string.Join(" and ", parts)} into {branch}.");
                    }
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

    static string Plural(int commits) => commits == 1 ? "1 commit" : $"{commits} commits";

    /// <summary>
    /// Orders the branches so each comes after the branch it builds on, so a stacked branch gets
    /// its base's update in the same run.
    /// </summary>
    static IReadOnlyList<string> BasesFirst(GitRunner git, PaConfig config, IReadOnlyList<string> branches)
    {
        var bases = branches.ToDictionary(b => b, b => Stack.Base(git, config, b));
        var ordered = new List<string>();
        void Add(string branch, int depth)
        {
            if (ordered.Contains(branch))
                return;
            // The depth limit guards against a base loop someone set up by hand.
            if (depth < branches.Count && bases.GetValueOrDefault(branch) is { } baseBranch && bases.ContainsKey(baseBranch))
                Add(baseBranch, depth + 1);
            if (!ordered.Contains(branch))
                ordered.Add(branch);
        }
        foreach (var branch in branches)
            Add(branch, 0);
        return ordered;
    }
}
