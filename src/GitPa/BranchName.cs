using System.Text.RegularExpressions;

namespace GitPa;

/// <summary>
/// Builds conventional branch names: {type}/{ticket}[-{title}], or {type}/{title} for types without a ticket
/// (see <see cref="BranchTypes"/>).
/// </summary>
public static class BranchName
{
    public const int MaxLength = 60;

    public static string Build(BranchType type, string? ticket, string? title)
    {
        var slug = Slug(title ?? "");
        string name;
        if (!type.NeedsTicket)
        {
            if (slug.Length == 0)
                throw new GitPaException($"A {type.Name} branch needs a title.");
            name = $"{type.Name}/{slug}";
        }
        else
        {
            ticket = ticket?.Trim().ToUpperInvariant() ?? "";
            if (ValidateTicket(ticket) is { } error)
                throw new GitPaException(error);
            name = slug.Length == 0 ? $"{type.Name}/{ticket}" : $"{type.Name}/{ticket}-{slug}";
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
    public static string? TicketFrom(string branch, IReadOnlyList<BranchType> types)
    {
        var names = types.Where(t => t.NeedsTicket).Select(t => Regex.Escape(t.Name)).ToList();
        if (names.Count == 0)
            return null;
        // Tickets are uppercase and titles lowercase, so the ticket ends where a lowercase part begins.
        var match = Regex.Match(branch, $"^(?:{string.Join('|', names)})/([A-Z0-9]+(?:-[A-Z0-9]+)*)(?:-[a-z0-9.].*)?$");
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// Prefixes the summary with the branch's ticket ("PA-77: Add login"), so Bitbucket links the commit
    /// to Jira. Leaves it alone when the branch has no ticket or the summary already mentions it.
    /// </summary>
    public static string WithTicketPrefix(string branch, string summary, IReadOnlyList<BranchType> types)
    {
        var ticket = TicketFrom(branch, types);
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
