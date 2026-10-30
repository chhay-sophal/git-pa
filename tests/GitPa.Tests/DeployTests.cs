using GitPa.Commands;
using Spectre.Console.Testing;

namespace GitPa.Tests;

/// <summary>
/// Records tool calls. "Builds" by copying this test assembly (which contains the fake plug-in classes)
/// to the configured output file, and answers pac org fetch with the configured registered classes.
/// </summary>
sealed class FakeToolRunner(string outputFile) : IToolRunner
{
    public List<(string Tool, IReadOnlyList<string> Args)> Calls { get; } = [];
    public int BuildExitCode { get; set; }
    public bool BuildWritesOutput { get; set; } = true;

    /// <summary>Answers for successive pac org fetch calls; null means the query fails.</summary>
    public Queue<IReadOnlyCollection<string>?> Registered { get; } = new();

    /// <summary>The answer once <see cref="Registered"/> is empty: exactly the classes in the build.</summary>
    public IReadOnlyCollection<string>? RegisteredDefault { get; set; } = PluginClassesTests.FakePluginClasses;

    public int Run(string fileName, IReadOnlyList<string> args, string workingDirectory)
    {
        Calls.Add((fileName, args));
        if (fileName != "msbuild")
            return 0;
        if (BuildWritesOutput)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputFile)!);
            File.Copy(typeof(FakeToolRunner).Assembly.Location, outputFile, overwrite: true);
            File.SetLastWriteTimeUtc(outputFile, DateTime.UtcNow);
        }
        return BuildExitCode;
    }

    /// <summary>Answers captured calls first, when set and returning non-null (e.g. pac org list in setup).</summary>
    public Func<IReadOnlyList<string>, ToolResult?>? CaptureHandler { get; set; }

    public List<IReadOnlyList<string>> CaptureCalls { get; } = [];

    public ToolResult Capture(string fileName, IReadOnlyList<string> args, string workingDirectory)
    {
        CaptureCalls.Add(args);
        if (CaptureHandler?.Invoke(args) is { } handled)
            return handled;
        var registered = Registered.Count > 0 ? Registered.Dequeue() : RegisteredDefault;
        if (registered is null)
            return new ToolResult(1, "Error: not signed in");
        var rows = string.Concat(registered.Select(name => $"{name}   {Guid.NewGuid()}\n"));
        return new ToolResult(0, $"Connected as someone\nConnected to... DEV\n\ntypename   plugintypeid\n{rows}");
    }
}

public class DeployTests
{
    const string Config = """
        {
          // what git pa deploy builds and pushes
          "environment": "https://contoso-dev.crm.dynamics.com/",
          "plugins": {
            "solution": "Plugins.sln",
            "assemblies": [
              { "project": "Main/Main.csproj", "file": "Main/bin/Release/ILMerge/Main.dll",
                "pluginId": "a515c00c-60a6-458b-8319-159420f8a19b" },
              { "name": "Workflow", "project": "Wf/Wf.csproj", "file": "Wf/bin/Release/ILMerge/Wf.dll",
                "pluginId": "3c0a4998-5183-4048-b8df-34b3727f92ea" },
            ],
          },
        }
        """;

    static readonly DeployTools Tools = new("msbuild", "pac", "nuget");

    static TestRepo RepoWithConfig(string config = Config)
    {
        var repo = new TestRepo();
        TestRepo.Write(repo.Git, DeployConfig.FileName, config);
        return repo;
    }

    static FakeToolRunner RunnerFor(TestRepo repo, string file = "Main/bin/Release/ILMerge/Main.dll") =>
        new(DeployConfig.Resolve(repo.Git.WorkingDirectory, file));

    [Fact]
    public void LoadsConfig_WithDefaults()
    {
        using var repo = RepoWithConfig();

        var deploy = DeployConfig.Load(repo.Git.WorkingDirectory);

        Assert.Equal("https://contoso-dev.crm.dynamics.com", deploy.Environment);
        Assert.Equal("Release", deploy.Configuration);
        Assert.Equal(["Main", "Workflow"], deploy.Assemblies.Select(a => a.Name));
        Assert.Equal(Guid.Parse("3c0a4998-5183-4048-b8df-34b3727f92ea"), deploy.Find("workflow").PluginId);
        Assert.Throws<GitPaException>(() => deploy.Find("Other"));
    }

    [Theory]
    [InlineData("""{ "environment": "not a url", "plugins": { "assemblies": [] } }""", "environment")]
    [InlineData("""{ "environment": "https://x.crm.dynamics.com", "plugins": { "assemblies": [] } }""", "at least one")]
    [InlineData("""{ "environment": "https://x.crm.dynamics.com", "plugins": { "assemblies": [ { "project": "a.csproj", "file": "a.dll", "pluginId": "nope" } ] } }""", "pluginId")]
    [InlineData("""{ "environment": """, "not valid JSON")]
    [InlineData("""{ "environment": "https://x.crm.dynamics.com", "webResources": {} }""", "no \"plugins\" section")]
    public void RejectsInvalidConfig(string config, string message)
    {
        using var repo = RepoWithConfig(config);

        var error = Assert.Throws<GitPaException>(() => DeployConfig.Load(repo.Git.WorkingDirectory));

        Assert.Contains(message, error.Message);
    }

    const string LegacyConfig = """
        { "deploy": { "environment": "https://contoso-dev.crm.dynamics.com", "solution": "Plugins.sln",
          "assemblies": [ { "project": "Main/Main.csproj", "file": "Main/bin/Release/ILMerge/Main.dll",
            "pluginId": "a515c00c-60a6-458b-8319-159420f8a19b" } ] } }
        """;

    [Fact]
    public void ReadsTheOldGitPaJson()
    {
        using var repo = new TestRepo();
        TestRepo.Write(repo.Git, DeployConfig.LegacyFileName, LegacyConfig);

        var deploy = DeployConfig.Load(repo.Git.WorkingDirectory);

        Assert.True(DeployConfig.UsesLegacyFile(repo.Git.WorkingDirectory));
        Assert.Equal("https://contoso-dev.crm.dynamics.com", deploy.Environment);
        Assert.Equal("Plugins.sln", deploy.Solution);
        Assert.Equal("Main", Assert.Single(deploy.Assemblies).Name);
    }

    [Fact]
    public void Cli_MovesTheOldGitPaJson_WhenAsked()
    {
        using var repo = new TestRepo();
        TestRepo.Write(repo.Git, DeployConfig.LegacyFileName, LegacyConfig);
        var before = DeployConfig.Load(repo.Git.WorkingDirectory);
        var runner = RunnerFor(repo);
        var console = new TestConsole().Interactive();
        console.Input.PushTextWithEnter("y");

        var exitCode = GitPaCli.Build(new Cli(console, repo.Git.WorkingDirectory), _ => Tools, runner)
            .Parse(["deploy", "--yes", "--skip-sync"]).Invoke();

        Assert.Equal(0, exitCode);
        Assert.False(File.Exists(Path.Combine(repo.Git.WorkingDirectory, DeployConfig.LegacyFileName)));
        Assert.False(DeployConfig.UsesLegacyFile(repo.Git.WorkingDirectory));
        var after = DeployConfig.Load(repo.Git.WorkingDirectory);
        Assert.Equal(before.Environment, after.Environment);
        Assert.Equal(before.Assemblies, after.Assemblies);
        Assert.Equal("pac", runner.Calls[^1].Tool);
    }

    [Fact]
    public void SyncsRestoresBuildsAndPushes()
    {
        using var repo = RepoWithConfig();
        var teammate = repo.Clone("teammate");
        teammate.Run("switch", "--quiet", "dev");
        TestRepo.Write(teammate, "teammate.txt", "teammate's plugin change\n");
        TestRepo.Commit(teammate, "teammate change");
        teammate.Run("push", "--quiet", "origin", "dev");
        var deploy = DeployConfig.Load(repo.Git.WorkingDirectory);
        var runner = RunnerFor(repo);

        DeployOperation.Run(repo.Git, repo.Config, deploy, deploy.Find("Main"), Tools, sync: true, runner, repo.Reporter);

        Assert.True(File.Exists(Path.Combine(repo.Git.WorkingDirectory, "teammate.txt")));
        Assert.Equal(["nuget", "msbuild", "pac"], runner.Calls.Select(c => c.Tool));
        Assert.Equal(["restore", "Plugins.sln"], runner.Calls[0].Args);
        Assert.Contains("-t:Rebuild", runner.Calls[1].Args);
        Assert.Contains("-p:Configuration=Release", runner.Calls[1].Args);
        Assert.Equal(
            ["plugin", "push", "--environment", "https://contoso-dev.crm.dynamics.com",
                "--pluginId", "a515c00c-60a6-458b-8319-159420f8a19b",
                "--pluginFile", "Main/bin/Release/ILMerge/Main.dll", "--type", "Assembly"],
            runner.Calls[2].Args);
    }

    [Fact]
    public void StopsBeforePushing_WhenARegisteredClassIsMissingFromTheBuild()
    {
        using var repo = RepoWithConfig();
        var deploy = DeployConfig.Load(repo.Git.WorkingDirectory);
        var runner = RunnerFor(repo);
        runner.RegisteredDefault = [.. PluginClassesTests.FakePluginClasses, "GitPa.Tests.FakePlugins.RemovedPlugin"];

        var error = Assert.Throws<GitPaException>(() =>
            DeployOperation.Run(repo.Git, repo.Config, deploy, deploy.Assemblies[0], Tools, false, runner, repo.Reporter));

        Assert.Contains("GitPa.Tests.FakePlugins.RemovedPlugin", error.Message);
        Assert.DoesNotContain(runner.Calls, c => c.Tool == "pac");
    }

    [Theory]
    [InlineData(false, "not registered yet")]
    [InlineData(true, "now registered by the push")]
    public void ReportsNewClassesAfterPushing(bool pushRegistersThem, string message)
    {
        using var repo = RepoWithConfig();
        var deploy = DeployConfig.Load(repo.Git.WorkingDirectory);
        var runner = RunnerFor(repo);
        var withoutNew = PluginClassesTests.FakePluginClasses.Where(c => !c.EndsWith("ContactPlugin")).ToList();
        runner.Registered.Enqueue(withoutNew);
        runner.Registered.Enqueue(pushRegistersThem ? PluginClassesTests.FakePluginClasses : withoutNew);

        DeployOperation.Run(repo.Git, repo.Config, deploy, deploy.Assemblies[0], Tools, false, runner, repo.Reporter);

        Assert.Equal("pac", runner.Calls[^1].Tool);
        var warning = Assert.Single(repo.Reporter.Warnings);
        Assert.Contains(message, warning);
        Assert.Contains("GitPa.Tests.FakePlugins.ContactPlugin", warning);
    }

    [Fact]
    public void StillPushes_WhenRegisteredClassesCantBeRead()
    {
        using var repo = RepoWithConfig();
        var deploy = DeployConfig.Load(repo.Git.WorkingDirectory);
        var runner = RunnerFor(repo);
        runner.RegisteredDefault = null;

        DeployOperation.Run(repo.Git, repo.Config, deploy, deploy.Assemblies[0], Tools, false, runner, repo.Reporter);

        Assert.Equal("pac", runner.Calls[^1].Tool);
        Assert.Contains("Couldn't read the registered", Assert.Single(repo.Reporter.Warnings));
    }

    [Fact]
    public void DoesNotPush_WhenBuildFails()
    {
        using var repo = RepoWithConfig();
        var deploy = DeployConfig.Load(repo.Git.WorkingDirectory);
        var runner = RunnerFor(repo);
        runner.BuildExitCode = 1;

        Assert.Throws<GitPaException>(() =>
            DeployOperation.Run(repo.Git, repo.Config, deploy, deploy.Assemblies[0], Tools, false, runner, repo.Reporter));

        Assert.DoesNotContain(runner.Calls, c => c.Tool == "pac");
    }

    [Fact]
    public void DoesNotPush_WhenBuildDidNotProduceTheFile()
    {
        using var repo = RepoWithConfig();
        var deploy = DeployConfig.Load(repo.Git.WorkingDirectory);
        var runner = RunnerFor(repo);
        runner.BuildWritesOutput = false;

        var error = Assert.Throws<GitPaException>(() =>
            DeployOperation.Run(repo.Git, repo.Config, deploy, deploy.Assemblies[0], Tools, false, runner, repo.Reporter));

        Assert.Contains("didn't produce", error.Message);
        Assert.DoesNotContain(runner.Calls, c => c.Tool == "pac");
    }

    [Fact]
    public void Cli_RequiresConfirmation_AndSupportsDryRun()
    {
        using var repo = RepoWithConfig();
        var runner = RunnerFor(repo);
        int Run(TestConsole console, params string[] args) =>
            GitPaCli.Build(new Cli(console, repo.Git.WorkingDirectory), _ => Tools, runner).Parse(args).Invoke();

        var refused = new TestConsole();
        Assert.Equal(1, Run(refused, "deploy", "Main"));
        Assert.Contains("--yes", refused.Output);

        var dryRun = new TestConsole();
        dryRun.Profile.Width = 300;
        Assert.Equal(0, Run(dryRun, "deploy", "Workflow", "--dry-run"));
        Assert.Contains("pac plugin push", dryRun.Output);
        Assert.Contains("3c0a4998-5183-4048-b8df-34b3727f92ea", dryRun.Output);

        Assert.Empty(runner.Calls);

        Assert.Equal(0, Run(new TestConsole(), "deploy", "Main", "--yes", "--skip-sync"));
        Assert.Equal("pac", runner.Calls[^1].Tool);
    }
}
