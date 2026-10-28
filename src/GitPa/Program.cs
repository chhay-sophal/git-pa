using System.Text;
using GitPa.Commands;
using Spectre.Console;

Console.OutputEncoding = Encoding.UTF8;

var console = AnsiConsole.Console;
if (Console.IsInputRedirected)
    console.Profile.Capabilities.Interactive = false;

return GitPaCli.Build(new Cli(console, Environment.CurrentDirectory)).Parse(args).Invoke();
