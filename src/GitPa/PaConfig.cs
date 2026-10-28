namespace GitPa;

/// <summary>git-pa settings, stored in git config under the "pa" section.</summary>
public sealed record PaConfig(string Main, string Dev, string Remote, bool AutoPush)
{
    public const string MainKey = "pa.main";
    public const string DevKey = "pa.dev";
    public const string RemoteKey = "pa.remote";
    public const string AutoPushKey = "pa.autoPush";
    public const string DefaultRemote = "origin";

    public static PaConfig Load(GitRunner git)
    {
        var main = Get(git, MainKey);
        var dev = Get(git, DevKey);
        if (main is null || dev is null)
            throw new GitPaException("git-pa is not configured for this repository. Run `git pa init` first.");

        return new PaConfig(main, dev, Get(git, RemoteKey) ?? DefaultRemote, LoadAutoPush(git));
    }

    public static string? Get(GitRunner git, string key)
    {
        var result = git.TryRun("config", "--get", key);
        return result.Success && result.Output.Length > 0 ? result.Output : null;
    }

    public static void Save(GitRunner git, string main, string dev, bool global)
    {
        var scope = global ? "--global" : "--local";
        git.Run("config", scope, MainKey, main);
        git.Run("config", scope, DevKey, dev);
    }

    static bool LoadAutoPush(GitRunner git)
    {
        var result = git.TryRun("config", "--type=bool", "--get", AutoPushKey);
        return result.ExitCode switch
        {
            0 => result.Output == "true",
            1 => true, // not set
            _ => throw new GitPaException($"Invalid {AutoPushKey} value: {result.Message}"),
        };
    }
}
