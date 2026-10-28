using System.CommandLine;
using Spectre.Console;

namespace GitPa.Commands;

static class NewCommand
{
    public static Command Create(Cli cli)
    {
        var nameArgument = new Argument<string>("name")
        {
            Description = "Exact branch name, instead of building one from type, ticket and title",
            Arity = ArgumentArity.ZeroOrOne,
        };
        var typeOption = new Option<string>("--type", "-t") { Description = $"Branch type: {string.Join(", ", BranchName.Types)}" };
        var ticketOption = new Option<string>("--ticket") { Description = "Ticket number (all types except chore)" };
        var titleOption = new Option<string>("--title") { Description = "Short title (required for chore, optional otherwise)" };
        var dryRunOption = Cli.DryRunOption();

        var command = new Command("new", "Create a branch from the latest main, staying on dev")
        {
            nameArgument, typeOption, ticketOption, titleOption, dryRunOption,
        };
        command.SetAction(parseResult => cli.Execute(() =>
        {
            var git = cli.OpenRepository();
            var config = PaConfig.Load(git);

            var name = parseResult.GetValue(nameArgument);
            var (type, ticket, title) =
                (parseResult.GetValue(typeOption), parseResult.GetValue(ticketOption), parseResult.GetValue(titleOption));
            if (name is not null && (type ?? ticket ?? title) is not null)
                throw new GitPaException("Give either a branch name or --type/--ticket/--title, not both.");
            name ??= BuildName(cli, type, ticket, title);

            if (parseResult.GetValue(dryRunOption))
                return cli.PrintPlan(NewBranchOperation.Plan(git, config, name));

            var baseRef = NewBranchOperation.Run(git, config, name, cli.Reporter);
            cli.Success($"Created {name} from {baseRef}. You are still on {git.CurrentBranch() ?? "a detached HEAD"}.");
            return 0;
        }));
        return command;
    }

    /// <summary>Builds the name from the options, asking for whatever is missing when interactive.</summary>
    static string BuildName(Cli cli, string? type, string? ticket, string? title)
    {
        type = cli.Require(type, "--type (or a branch name)", () => cli.Console.Prompt(new SelectionPrompt<string>()
            .Title("Branch type:")
            .AddChoices(BranchName.Types)));
        type = type.Trim().ToLowerInvariant();
        if (!BranchName.Types.Contains(type))
            throw new GitPaException($"Unknown branch type '{type}'. Use one of: {string.Join(", ", BranchName.Types)}.");

        if (BranchName.NeedsTicket(type))
        {
            ticket = cli.Require(ticket, "--ticket", () => cli.Console.Prompt(new TextPrompt<string>("Ticket number:")
                .Validate(t => BranchName.ValidateTicket(t.Trim()) is { } error
                    ? ValidationResult.Error(error)
                    : ValidationResult.Success())));
            if (title is null && cli.Interactive)
                title = cli.Console.Prompt(new TextPrompt<string>("Title [grey](optional)[/]:")
                    .AllowEmpty()
                    .Validate(t => Check(type, ticket, t)));
        }
        else
        {
            title = cli.Require(title, "--title", () => cli.Console.Prompt(new TextPrompt<string>("Title:")
                .Validate(t => Check(type, ticket, t))));
        }

        var name = BranchName.Build(type, ticket, title);
        if (cli.Interactive)
            cli.Console.MarkupLine($"Branch name: [bold]{Markup.Escape(name)}[/]");
        return name;
    }

    static ValidationResult Check(string type, string? ticket, string title)
    {
        try
        {
            BranchName.Build(type, ticket, title);
            return ValidationResult.Success();
        }
        catch (GitPaException e)
        {
            return ValidationResult.Error(e.Message);
        }
    }
}
