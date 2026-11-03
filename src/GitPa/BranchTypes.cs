using System.Text.RegularExpressions;

namespace GitPa;

/// <summary>A branch type for `git pa new`: either {name}/{ticket}[-{title}], or {name}/{title} without a ticket.</summary>
public sealed record BranchType(string Name, bool NeedsTicket)
{
    public override string ToString() => NeedsTicket ? Name : $"{Name}{BranchTypes.NoTicketSuffix}";
}

/// <summary>
/// The branch types `git pa new` offers, from the pa.branchTypes git setting: names separated by spaces
/// or commas, with ":noticket" on the ones that take a title instead of a ticket.
/// </summary>
public static class BranchTypes
{
    public const string Key = "pa.branchTypes";
    public const string NoTicketSuffix = ":noticket";

    public static readonly IReadOnlyList<BranchType> Default =
    [
        new("bugfix", true), new("hotfix", true), new("feature", true), new("chore", false),
        new("refactor", true), new("test", true), new("docs", false),
    ];

    /// <summary>The configured types, or the defaults when pa.branchTypes isn't set.</summary>
    public static IReadOnlyList<BranchType> Load(GitRunner git) =>
        PaConfig.Get(git, Key) is { } value ? Parse(value) : Default;

    public static IReadOnlyList<BranchType> Parse(string value)
    {
        var types = new List<BranchType>();
        foreach (var entry in value.Split([' ', ',', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            var needsTicket = !entry.EndsWith(NoTicketSuffix, StringComparison.OrdinalIgnoreCase);
            var name = (needsTicket ? entry : entry[..^NoTicketSuffix.Length]).ToLowerInvariant();
            if (!Regex.IsMatch(name, "^[a-z0-9]+([._-][a-z0-9]+)*$"))
                throw new GitPaException(
                    $"Invalid branch type '{entry}' in {Key}. Use lowercase letters, digits, '-', '_' or '.', " +
                    $"optionally followed by {NoTicketSuffix}, like \"feature chore{NoTicketSuffix}\".");
            if (types.Any(t => t.Name == name))
                throw new GitPaException($"Branch type '{name}' appears more than once in {Key}.");
            types.Add(new BranchType(name, needsTicket));
        }
        if (types.Count == 0)
            throw new GitPaException($"{Key} is empty. Unset it to use the defaults: {Format(Default)}");
        return types;
    }

    /// <summary>The value to store in pa.branchTypes for these types.</summary>
    public static string Format(IEnumerable<BranchType> types) => string.Join(' ', types);

    public static BranchType Find(IReadOnlyList<BranchType> types, string name)
    {
        name = name.Trim().ToLowerInvariant();
        return types.FirstOrDefault(t => t.Name == name)
            ?? throw new GitPaException(
                $"Unknown branch type '{name}'. Use one of: {string.Join(", ", types.Select(t => t.Name))}" +
                $" (set the list with git config {Key}).");
    }
}
