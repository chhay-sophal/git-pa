namespace GitPa;

/// <summary>Where `git pa init` saves settings.</summary>
public enum ConfigScope
{
    /// <summary>This repository's own git config.</summary>
    Local,

    /// <summary>The committed .gitpa file, shared with the team.</summary>
    Shared,

    /// <summary>Your global git config, for all your repositories.</summary>
    Global,
}

/// <summary>
/// git-pa settings, under the "pa" section. Each one is read from, in order: this repository's git config
/// (a personal override), the committed .gitpa file (the team's settings), then the global and system git
/// config.
/// </summary>
public sealed record PaConfig(string Main, string Dev, string Remote, bool AutoPush)
{
    /// <summary>
    /// Branches between dev and main that features are promoted through, in order (like uat), from pa.stages.
    /// Empty when features go straight from dev to main.
    /// </summary>
    public IReadOnlyList<string> Stages { get; init; } = [];

    /// <summary>main, dev and the stages: the shared branches that aren't feature branches.</summary>
    public bool IsLongLived(string branch) => branch == Main || branch == Dev || Stages.Contains(branch);

    public const string MainKey = "pa.main";
    public const string DevKey = "pa.dev";
    public const string RemoteKey = "pa.remote";
    public const string AutoPushKey = "pa.autoPush";
    public const string StagesKey = "pa.stages";
    public const string DefaultRemote = "origin";

    /// <summary>The team's settings, committed at the repository root in git config format.</summary>
    public const string SharedFileName = ".gitpa";

    public static PaConfig Load(GitRunner git)
    {
        var main = Get(git, MainKey);
        var dev = Get(git, DevKey);
        if (main is null || dev is null)
            throw new GitPaException(
                "git-pa is not configured for this repository. Run `git pa init` first " +
                $"(with --shared to save it in {SharedFileName} for your team).");

        return new PaConfig(main, dev, Get(git, RemoteKey) ?? DefaultRemote, LoadAutoPush(git))
        {
            Stages = Get(git, StagesKey) is { } stages ? ParseStages(git, stages, main, dev) : [],
        };
    }

    /// <summary>Branch names separated by spaces or commas; "none" (or nothing) means no stages.</summary>
    public static IReadOnlyList<string> ParseStages(GitRunner git, string value, string main, string dev)
    {
        var stages = new List<string>();
        foreach (var name in value.Split([' ', ',', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (name == "none")
                continue;
            if (!git.IsValidBranchName(name))
                throw new GitPaException($"'{name}' in {StagesKey} is not a valid branch name.");
            if (name == main || name == dev)
                throw new GitPaException($"{StagesKey} lists the branches between {dev} and {main}, so it can't include '{name}'.");
            if (stages.Contains(name))
                throw new GitPaException($"'{name}' appears more than once in {StagesKey}.");
            stages.Add(name);
        }
        return stages;
    }

    public static string? Get(GitRunner git, string key)
    {
        var result = Lookup(git, key);
        return result.Success && result.Output.Length > 0 ? result.Output : null;
    }

    /// <summary>The .gitpa file of the repository <paramref name="git"/> runs in, or null outside one.</summary>
    public static string? SharedFile(GitRunner git)
    {
        var top = git.TryRun("rev-parse", "--show-toplevel");
        return top.Success ? Path.Combine(Path.GetFullPath(top.Output), SharedFileName) : null;
    }

    public static void Save(GitRunner git, string main, string dev, ConfigScope scope)
    {
        Set(git, scope, MainKey, main);
        Set(git, scope, DevKey, dev);
    }

    public static void Set(GitRunner git, ConfigScope scope, string key, string value) =>
        git.Run(["config", .. ScopeArgs(git, scope), key, value]);

    /// <summary>Removes the setting from that scope; fine when it isn't there.</summary>
    public static void Unset(GitRunner git, ConfigScope scope, string key) =>
        git.TryRun(["config", .. ScopeArgs(git, scope), "--unset", key]);

    static string[] ScopeArgs(GitRunner git, ConfigScope scope) => scope switch
    {
        ConfigScope.Local => ["--local"],
        ConfigScope.Global => ["--global"],
        _ => ["--file", SharedFile(git) ?? throw new GitPaException("Not inside a git repository.")],
    };

    /// <summary>
    /// Finds the setting in the first place that has it. Exit code 1 means it isn't set anywhere; any other
    /// failure (like an invalid boolean) is returned as is.
    /// </summary>
    static GitResult Lookup(GitRunner git, string key, string? type = null)
    {
        string[] typeArgs = type is null ? [] : [$"--type={type}"];
        var shared = SharedFile(git);
        if (shared is not null)
        {
            foreach (var source in new[] { new[] { "--local" }, ["--file", shared] })
            {
                if (source[0] == "--file" && !File.Exists(shared))
                    continue;
                var result = git.TryRun(["config", .. source, .. typeArgs, "--get", key]);
                if (result.ExitCode != 1)
                    return result;
            }
        }
        // Without a scope git reads every config file; the local one has nothing by now.
        return git.TryRun(["config", .. typeArgs, "--get", key]);
    }

    static bool LoadAutoPush(GitRunner git)
    {
        var result = Lookup(git, AutoPushKey, "bool");
        return result.ExitCode switch
        {
            0 => result.Output == "true",
            1 => true, // not set
            _ => throw new GitPaException($"Invalid {AutoPushKey} value: {result.Message}"),
        };
    }
}
