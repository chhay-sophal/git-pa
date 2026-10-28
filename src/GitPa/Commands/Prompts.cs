using Spectre.Console;

namespace GitPa.Commands;

static class Prompts
{
    public static string PickFeatureBranch(Cli cli, GitRunner git, PaConfig config, string title)
    {
        var branches = Branches.LocalFeatures(git, config);
        if (branches.Count == 0)
            throw new GitPaException("There are no feature branches yet. Create one with: git pa new <name>");
        return cli.Console.Prompt(new SelectionPrompt<string>()
            .Title(title)
            .PageSize(10)
            .AddChoices(branches));
    }
}
