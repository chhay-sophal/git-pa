using System.CommandLine;
using Spectre.Console;

namespace GitPa.Commands;

static class StatusCommand
{
    public static Command Create(Cli cli)
    {
        var fetchOption = new Option<bool>("--fetch", "-f") { Description = "Fetch from the remote first" };

        var command = new Command("status", "Show feature branches compared with main, dev, and the remote") { fetchOption };
        command.SetAction(parseResult => cli.Execute(() =>
        {
            var git = cli.OpenRepository();
            var config = PaConfig.Load(git);

            if (parseResult.GetValue(fetchOption) && git.RemoteExists(config.Remote))
            {
                cli.Reporter.Step($"Fetching {config.Remote}");
                var fetch = git.TryRun("fetch", "--quiet", "--prune", config.Remote);
                if (!fetch.Success)
                    cli.Reporter.Warn($"Could not fetch {config.Remote}; showing the last known state.\n{fetch.Message}");
            }

            Render(cli.Console, StatusOperation.Run(git, config));
            return 0;
        }));
        return command;
    }

    static void Render(IAnsiConsole console, RepoStatus status)
    {
        console.MarkupLine($"[bold]{Markup.Escape(status.Dev)}[/]: {Markup.Escape(DevSummary(status))}");

        if (status.Branches.Count == 0)
        {
            console.MarkupLine("[grey]No feature branches yet. Create one with: git pa new <name>[/]");
            return;
        }

        var table = new Table().Border(TableBorder.Rounded)
            .AddColumns("Branch", "Last commit", $"vs {status.MainRef}", $"In {status.Dev}", "Remote");
        foreach (var b in status.Branches)
        {
            table.AddRow(
                Markup.Escape(b.Name),
                Markup.Escape(b.LastCommit),
                VsMain(b),
                b.NotInDev == 0 ? "[green]yes[/]" : $"[yellow]{Plural(b.NotInDev, "commit")} missing[/]",
                Remote(b));
        }
        console.Write(table);
        console.MarkupLine("[grey]As of the last fetch. Use --fetch to update first.[/]");
    }

    static string DevSummary(RepoStatus status)
    {
        if (status.DevUpstream is null)
            return "no remote copy";
        var parts = new List<string>();
        if (status.DevToPull > 0)
            parts.Add($"{Plural(status.DevToPull, "commit")} behind {status.DevUpstream}, run git pa sync");
        if (status.DevToPush > 0)
            parts.Add($"{Plural(status.DevToPush, "commit")} not pushed");
        return parts.Count == 0 ? $"up to date with {status.DevUpstream}" : string.Join("; ", parts);
    }

    static string VsMain(BranchStatus b) => (b.AheadOfMain, b.BehindMain) switch
    {
        (0, 0) => "[grey]no changes[/]",
        (_, 0) => $"{b.AheadOfMain} ahead",
        (0, _) => $"[grey]{b.BehindMain} behind[/]",
        _ => $"{b.AheadOfMain} ahead, [yellow]{b.BehindMain} behind[/]",
    };

    static string Remote(BranchStatus b) => (b.ToPush, b.ToPull) switch
    {
        (null, _) => "[grey]not pushed[/]",
        (0, 0) => "[green]up to date[/]",
        (var push, 0) => $"[yellow]{push} to push[/]",
        (0, var pull) => $"[yellow]{pull} to pull[/]",
        var (push, pull) => $"[red]{push} to push, {pull} to pull[/]",
    };

    static string Plural(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";
}
