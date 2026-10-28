using System.CommandLine;
using System.Diagnostics;
using Spectre.Console;

namespace GitPa.Commands;

static class PrCommand
{
    public static Command Create(Cli cli)
    {
        var branchArgument = new Argument<string>("branch")
        {
            Description = "Feature branch to open a pull request for",
            Arity = ArgumentArity.ZeroOrOne,
        };
        var printOption = new Option<bool>("--print") { Description = "Print the URL instead of opening the browser" };
        var noPushOption = new Option<bool>("--no-push") { Description = "Do not push the branch if it isn't on the remote yet" };

        var command = new Command("pr", "Open the page to create a pull request from a feature branch to main")
        {
            branchArgument, printOption, noPushOption,
        };
        command.SetAction(parseResult => cli.Execute(() =>
        {
            var git = cli.OpenRepository();
            var config = PaConfig.Load(git);
            var branch = cli.Require(parseResult.GetValue(branchArgument), "branch name",
                () => Prompts.PickFeatureBranch(cli, git, config, "Branch for the pull request:"));
            Branches.EnsureFeature(config, branch);
            if (!git.LocalBranchExists(branch) && !git.RemoteBranchExists(config.Remote, branch))
                throw new GitPaException($"Branch '{branch}' does not exist.");

            var remoteUrl = git.TryRun("remote", "get-url", config.Remote);
            if (!remoteUrl.Success)
                throw new GitPaException($"There is no remote named '{config.Remote}'.");

            var url = PaConfig.Get(git, PullRequestUrl.TemplateKey) is { } template
                ? PullRequestUrl.FromTemplate(template, branch, config.Main)
                : PullRequestUrl.Build(remoteUrl.Output, branch, config.Main, PullRequestUrl.ResolveSshHost)
                  ?? throw new GitPaException(
                      $"Don't know how to open pull requests for {remoteUrl.Output}. Set a URL template, for example:\n" +
                      $"  git config {PullRequestUrl.TemplateKey} \"https://example.com/repo/pr/new?from={{branch}}&to={{target}}\"");

            if (!git.RemoteBranchExists(config.Remote, branch))
            {
                if (parseResult.GetValue(noPushOption))
                    cli.Reporter.Warn($"'{branch}' isn't on {config.Remote} yet, so the pull request page won't find it.");
                else
                {
                    cli.Reporter.Step($"Pushing {branch} to {config.Remote} first");
                    if (git.RunAttached("push", "--quiet", "-u", config.Remote, branch) != 0)
                        throw new GitPaException($"Pushing '{branch}' failed.");
                }
            }

            if (parseResult.GetValue(printOption) || !OpenBrowser(url))
                cli.Console.WriteLine(url);
            else
                cli.Success($"Opened the pull request page for {branch} → {config.Main}.");
            return 0;
        }));
        return command;
    }

    static bool OpenBrowser(string url)
    {
        try
        {
            var info = OperatingSystem.IsWindows() ? new ProcessStartInfo(url) { UseShellExecute = true }
                : OperatingSystem.IsMacOS() ? new ProcessStartInfo("open", [url])
                : new ProcessStartInfo("xdg-open", [url]);
            Process.Start(info)?.Dispose();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
