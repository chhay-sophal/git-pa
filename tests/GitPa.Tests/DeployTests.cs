using GitPa.Commands;
using Spectre.Console.Testing;

namespace GitPa.Tests;

/// <summary>Records tool calls; "builds" by writing the configured output file.</summary>
sealed class FakeToolRunner(string outputFile) : IToolRunner
{
    public List<(string Tool, IReadOnlyList<string> Args)> Calls { get; } = [];
    public int BuildExitCode { get; set; }
    public bool BuildWritesOutput { get; set; } = true;

    public int Run(string fileName, IReadOnlyList<string> args, string workingDirectory)
    {
        Calls.Add((fileName, args));
        if (fileName != "msbuild")
            return 0;
        if (BuildWritesOutput)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputFile)!);
            File.WriteAllText(outputFile, "dll");
        }
        return BuildExitCode;
    }
}

public class DeployTests
{
    const string Config = """
        {
          // what git pa deploy builds and pushes
          "deploy": {
            "environment": "https://contoso-dev.crm.dynamics.com/",
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
    [InlineData("""{ "deploy": { "environment": "not a url", "assemblies": [] } }""", "environment")]
    [InlineData("""{ "deploy": { "environment": "https://x.crm.dynamics.com", "assemblies": [] } }""", "at least one")]
    [InlineData("""{ "deploy": { "environment": "https://x.crm.dynamics.com", "assemblies": [ { "project": "a.csproj", "file": "a.dll", "pluginId": "nope" } ] } }""", "pluginId")]
    [InlineData("""{ "deploy": """, "not valid JSON")]
    public void RejectsInvalidConfig(string config, string message)
    {
        using var repo = RepoWithConfig(config);

        var error = Assert.Throws<GitPaException>(() => DeployConfig.Load(repo.Git.WorkingDirectory));

        Assert.Contains(message, error.Message);
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
