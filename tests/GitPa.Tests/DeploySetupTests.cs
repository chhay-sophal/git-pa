using GitPa.Commands;
using Spectre.Console.Testing;

namespace GitPa.Tests;

public class DeploySetupTests
{
    // Shaped like real pac output, including names with spaces and a mangled emoji.
    const string OrgList = """
        Connected as dev@contoso.com
        Active Display Name             Environment ID                       Environment URL                                    Unique Name
               Contoso UAT ??           f065c78f-f7b9-47e2-b29c-22b5b0f97659 https://contoso-uat.crm.dynamics.com/              orgaaaa
        *      Contoso DEV              7c38ff7b-e176-46c2-a59b-a581dd5cbe70 https://contoso-dev.crm.dynamics.com/              orgbbbb
        """;

    const string SolutionRows = """
        Connected as dev@contoso.com
        Connected to... Contoso DEV

        friendlyname                                                uniquename                              solutionid
        Plug-in Solution ?? ( DO NOT DEPLOY)                        PluginSolution                          29749c52-599d-ed11-aad1-000d3a85c377
        Main Solution                                               MainSolution                            ed7d461f-23ec-ea11-a815-000d3a82fbcb
        """;

    const string AssemblyRows = """
        Connected as dev@contoso.com
        Connected to... Contoso DEV

        name                          pluginassemblyid
        Contoso.Plugins               a515c00c-60a6-458b-8319-159420f8a19b
        Contoso.Workflows             3c0a4998-5183-4048-b8df-34b3727f92ea
        """;

    const string OldStyleProject = """
        <?xml version="1.0" encoding="utf-8"?>
        <Project ToolsVersion="4.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
          <Import Project="..\packages\ILMerge.3.0.41\build\ILMerge.props" />
          <PropertyGroup>
            <AssemblyName>Contoso.Plugins</AssemblyName>
          </PropertyGroup>
          <PropertyGroup Condition=" '$(Configuration)|$(Platform)' == 'Debug|AnyCPU' ">
            <OutputPath>bin\Debug\</OutputPath>
          </PropertyGroup>
          <PropertyGroup Condition=" '$(Configuration)|$(Platform)' == 'Release|AnyCPU' ">
            <OutputPath>bin\Release\</OutputPath>
          </PropertyGroup>
        </Project>
        """;

    const string SdkProject = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net462</TargetFramework>
          </PropertyGroup>
        </Project>
        """;

    static readonly ToolResult Fail = new(1, "error");

    static ToolResult? Answer(IReadOnlyList<string> args) => args switch
    {
        ["org", "list"] => new(0, OrgList),
        ["org", "fetch", .., var xml] when xml.Contains("'solution'") => new(0, SolutionRows),
        ["org", "fetch", .., var xml] when xml.Contains("'pluginassembly'") => new(0, AssemblyRows),
        _ => null,
    };

    static TestRepo RepoWithProjects()
    {
        var repo = new TestRepo();
        foreach (var dir in new[] { "Plugins", "Workflows", "Plugins/bin" })
            Directory.CreateDirectory(Path.Combine(repo.Git.WorkingDirectory, dir));
        TestRepo.Write(repo.Git, "Plugins/Plugins.csproj", OldStyleProject);
        TestRepo.Write(repo.Git, "Plugins/bin/Copy.csproj", OldStyleProject); // build output, must be ignored
        TestRepo.Write(repo.Git, "Workflows/Contoso.Workflows.csproj", SdkProject);
        TestRepo.Write(repo.Git, "Contoso.sln", "");
        return repo;
    }

    [Fact]
    public void ParsesPacOutput()
    {
        var runner = new FakeToolRunner("unused") { CaptureHandler = Answer };

        var environments = Pac.Environments(runner, "pac", ".")!;
        var solutions = Pac.Solutions(runner, "pac", "https://contoso-dev.crm.dynamics.com", ".")!;
        var assemblies = Pac.Assemblies(runner, "pac", "https://contoso-dev.crm.dynamics.com", null, ".")!;

        Assert.Equal(
            [new PacEnvironment("Contoso UAT ??", "https://contoso-uat.crm.dynamics.com", false),
                new PacEnvironment("Contoso DEV", "https://contoso-dev.crm.dynamics.com", true)],
            environments);
        Assert.Equal("Plug-in Solution ?? ( DO NOT DEPLOY)", solutions[0].Name);
        Assert.Equal("PluginSolution", solutions[0].UniqueName);
        Assert.Equal(Guid.Parse("ed7d461f-23ec-ea11-a815-000d3a82fbcb"), solutions[1].Id);
        Assert.Equal(["Contoso.Plugins", "Contoso.Workflows"], assemblies.Select(a => a.Name));

        runner.CaptureHandler = _ => Fail;
        Assert.Null(Pac.Environments(runner, "pac", "."));
    }

    [Fact]
    public void FindsProjectsAndTheirOutput()
    {
        using var repo = RepoWithProjects();
        var root = repo.Git.WorkingDirectory;

        Assert.Equal(["Plugins/Plugins.csproj", "Workflows/Contoso.Workflows.csproj"], ProjectFinder.Projects(root));
        Assert.Equal(new ProjectGuess("Plugins/Plugins.csproj", "Plugins/bin/Release/ILMerge/Contoso.Plugins.dll", true),
            ProjectFinder.ForAssembly(root, "contoso.plugins", "Release"));
        Assert.Equal("Workflows/bin/Release/net462/Contoso.Workflows.dll",
            ProjectFinder.ForAssembly(root, "Contoso.Workflows", "Release")!.File);
        Assert.Null(ProjectFinder.ForAssembly(root, "Unknown", "Release"));
    }

    [Fact]
    public void SavedConfigLoadsBack()
    {
        using var repo = new TestRepo();
        var config = new DeployConfig("https://contoso-dev.crm.dynamics.com", "Release", "Contoso.sln", null,
            [new DeployAssembly("Contoso.Plugins", "Plugins/Plugins.csproj", "Plugins/bin/Release/ILMerge/Contoso.Plugins.dll",
                Guid.Parse("a515c00c-60a6-458b-8319-159420f8a19b"))]);

        config.Save(repo.Git.WorkingDirectory);
        var loaded = DeployConfig.Load(repo.Git.WorkingDirectory);

        Assert.Equal(config.Environment, loaded.Environment);
        Assert.Equal(config.Solution, loaded.Solution);
        Assert.Equal(config.Assemblies, loaded.Assemblies);
        Assert.DoesNotContain("msbuild", TestRepo.Read(repo.Git, DeployConfig.FileName));
    }

    [Fact]
    public void WizardWritesConfigFromChoices()
    {
        using var repo = RepoWithProjects();
        var runner = new FakeToolRunner("unused") { CaptureHandler = Answer };
        var console = new TestConsole().Interactive();
        console.Profile.Width = 200;
        console.Input.PushKey(ConsoleKey.Enter); // environment: the active one, listed first
        console.Input.PushKey(ConsoleKey.DownArrow); // solution: Main Solution
        console.Input.PushKey(ConsoleKey.Enter);
        console.Input.PushKey(ConsoleKey.Enter); // assemblies: both, pre-selected
        console.Input.PushKey(ConsoleKey.Enter); // configuration: Release
        console.Input.PushKey(ConsoleKey.Enter); // Contoso.Plugins file: as guessed
        console.Input.PushKey(ConsoleKey.Enter); // Contoso.Workflows file: as guessed

        var exitCode = GitPaCli.Build(new Cli(console, repo.Git.WorkingDirectory), _ => new DeployTools("msbuild", "pac", "nuget"), runner)
            .Parse(["deploy", "--setup"]).Invoke();

        Assert.Equal(0, exitCode);
        var config = DeployConfig.Load(repo.Git.WorkingDirectory);
        Assert.Equal("https://contoso-dev.crm.dynamics.com", config.Environment);
        Assert.Equal("Contoso.sln", config.Solution);
        Assert.Equal(
            [new DeployAssembly("Contoso.Plugins", "Plugins/Plugins.csproj", "Plugins/bin/Release/ILMerge/Contoso.Plugins.dll",
                Guid.Parse("a515c00c-60a6-458b-8319-159420f8a19b")),
                new DeployAssembly("Contoso.Workflows", "Workflows/Contoso.Workflows.csproj", "Workflows/bin/Release/net462/Contoso.Workflows.dll",
                    Guid.Parse("3c0a4998-5183-4048-b8df-34b3727f92ea"))],
            config.Assemblies);
        Assert.Contains(runner.CaptureCalls, args => args[^1].Contains("ed7d461f-23ec-ea11-a815-000d3a82fbcb"));
    }
}
