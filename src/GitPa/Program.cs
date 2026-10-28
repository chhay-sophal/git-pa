using System.Reflection;
using System.Text;
using GitPa;
using GitPa.Commands;
using Spectre.Console;

Console.OutputEncoding = Encoding.UTF8;

var console = AnsiConsole.Console;
if (Console.IsInputRedirected)
    console.Profile.Capabilities.Interactive = false;

// Look for a newer version while the command runs; only in a terminal, and never in scripts or CI.
var current = Assembly.GetEntryAssembly()!.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
var updateCheck = console.Profile.Capabilities.Interactive && UpdateCheckEnabled()
    ? Task.Run(() => UpdateCheck.FindNewerAsync(current, UpdateCheck.CacheFile, UpdateCheck.FetchVersionsAsync, DateTimeOffset.Now))
    : null;

var exitCode = GitPaCli.Build(new Cli(console, Environment.CurrentDirectory)).Parse(args).Invoke();

if (updateCheck is not null && updateCheck.Wait(TimeSpan.FromSeconds(1)) && updateCheck.Result is { } newer)
{
    console.MarkupLine(
        $"[yellow]git-pa {Markup.Escape(newer)} is available[/] (you have {Markup.Escape(current)}). " +
        "Update with: [bold]dotnet tool update -g GitPa[/]");
}
return exitCode;

static bool UpdateCheckEnabled() =>
    Environment.GetEnvironmentVariable(UpdateCheck.DisableVariable) is null or ""
    && new GitRunner(Environment.CurrentDirectory).TryRun("config", "--type=bool", "--get", UpdateCheck.DisableKey).Output != "false";
