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

    /// <summary>Lowercases the title, turns spaces into dashes, and drops characters git or shells dislike.</summary>
    public static string Slug(string title)
    {
        var slug = Regex.Replace(title.Trim().ToLowerInvariant(), @"\s+", "-");
        slug = Regex.Replace(slug, "[^a-z0-9._-]", "");
        slug = Regex.Replace(slug, "[-.]{2,}", "-");
        return slug.Trim('-', '.');
    }
}
