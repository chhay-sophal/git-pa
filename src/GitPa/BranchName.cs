using System.Text.RegularExpressions;

namespace GitPa;

/// <summary>
/// Builds conventional branch names: chore/{title}, or {type}/{ticket}[-{title}] for the other types.
/// </summary>
public static class BranchName
{
    public const string Chore = "chore";
    public const int MaxLength = 60;

    public static readonly IReadOnlyList<string> Types = ["bugfix", "hotfix", "feature", Chore, "refactor"];

    public static bool NeedsTicket(string type) => type != Chore;

    public static string Build(string type, string? ticket, string? title)
    {
        if (!Types.Contains(type))
            throw new GitPaException($"Unknown branch type '{type}'. Use one of: {string.Join(", ", Types)}.");

        var slug = Slug(title ?? "");
        string name;
        if (type == Chore)
        {
            if (slug.Length == 0)
                throw new GitPaException("A chore branch needs a title.");
            name = $"{Chore}/{slug}";
        }
        else
        {
            ticket = ticket?.Trim().ToUpperInvariant() ?? "";
            if (ValidateTicket(ticket) is { } error)
                throw new GitPaException(error);
            name = slug.Length == 0 ? $"{type}/{ticket}" : $"{type}/{ticket}-{slug}";
        }

        if (name.Length > MaxLength)
            throw new GitPaException(
                $"'{name}' is {name.Length} characters; the limit is {MaxLength}. Shorten the title by {name.Length - MaxLength}.");
        return name;
    }

    /// <summary>Returns an error message, or null when the ticket number is usable.</summary>
    public static string? ValidateTicket(string ticket) =>
        ticket.Length == 0 ? "A ticket number is required."
        : !Regex.IsMatch(ticket, "^[A-Za-z0-9]+(-[A-Za-z0-9]+)*$") ? "Use only letters, digits and dashes, like 1234 or PA-77."
        : null;

    /// <summary>The ticket from a name like feature/PA-77-add-login, or null when the branch doesn't carry one.</summary>
    public static string? TicketFrom(string branch)
    {
        // Tickets are uppercase and titles lowercase, so the ticket ends where a lowercase part begins.
        var types = string.Join('|', Types.Where(NeedsTicket));
        var match = Regex.Match(branch, $"^(?:{types})/([A-Z0-9]+(?:-[A-Z0-9]+)*)(?:-[a-z0-9.].*)?$");
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// Prefixes the summary with the branch's ticket ("PA-77: Add login"), so Bitbucket links the commit
    /// to Jira. Leaves it alone when the branch has no ticket or the summary already mentions it.
    /// </summary>
    public static string WithTicketPrefix(string branch, string summary)
    {
        var ticket = TicketFrom(branch);
        summary = summary.Trim();
        return ticket is null || summary.StartsWith(ticket, StringComparison.OrdinalIgnoreCase)
            ? summary
            : $"{ticket}: {summary}";
    }

    /// <summary>Lowercases the title, turns spaces into dashes, and drops characters git or shells dislike.</summary>
    public static string Slug(string title)
    {
        var slug = Regex.Replace(title.Trim().ToLowerInvariant(), @"\s+", "-");
        slug = Regex.Replace(slug, "[^a-z0-9._-]", "");
        slug = Regex.Replace(slug, "[-.]{2,}", "-");
        return slug.Trim('-', '.');
    }
}
