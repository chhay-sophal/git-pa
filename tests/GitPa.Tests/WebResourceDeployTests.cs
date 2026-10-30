using System.Net;
using System.Text;
using GitPa.Commands;
using Spectre.Console.Testing;

namespace GitPa.Tests;

/// <summary>An environment holding web resources in memory; records uploads and publishes.</summary>
sealed class FakeDataverse : IDataverse
{
    public static readonly Guid Me = Guid.NewGuid();
    public static readonly Guid Colleague = Guid.NewGuid();

    public List<WebResourceRecord> Records { get; } = [];
    public List<(Guid Id, string Content)> Uploads { get; } = [];
    public List<IReadOnlyCollection<Guid>> Publishes { get; } = [];
    public string? FailUploadOf { get; set; }

    public Guid Add(string name, string content, bool managed = false, Guid? modifiedBy = null)
    {
        var id = Guid.NewGuid();
        Records.Add(new WebResourceRecord(id, name, managed, Encoding.UTF8.GetBytes(content), DateTime.UtcNow,
            modifiedBy ?? Me, modifiedBy == Colleague ? "A Colleague" : "Me"));
        return id;
    }

    public Guid UserId() => Me;

    public IReadOnlyList<WebResourceRecord> WebResources(IReadOnlyCollection<string> names) =>
        Records.Where(r => names.Contains(r.Name, StringComparer.OrdinalIgnoreCase)).ToList();

    public void UpdateContent(Guid id, byte[] content)
    {
        if (Records.Single(r => r.Id == id).Name == FailUploadOf)
            throw new GitPaException("refused");
        Uploads.Add((id, Encoding.UTF8.GetString(content)));
    }

    public void Publish(IReadOnlyCollection<Guid> ids) => Publishes.Add(ids);
}

public class WebResourceDeployTests
{
    const string Config = """
        {
          "environment": "https://contoso-dev.crm.dynamics.com",
          "webResources": {
            "links": [
              { "name": "new_/scripts/account.js", "file": "scripts/account.js" },
              { "name": "new_/scripts/contact.js", "file": "scripts/contact.js" },
              { "name": "new_/scripts/lead.js", "file": "scripts/lead.js" },
              { "name": "new_/scripts/gone.js", "file": "scripts/gone.js" },
              { "name": "new_/scripts/missing.js", "file": "scripts/missing.js" },
              { "name": "mso_/managed.js", "file": "scripts/managed.js" }
            ]
          }
        }
        """;

    static (TestRepo Repo, FakeDataverse Dataverse) Setup()
    {
        var repo = new TestRepo();
        Directory.CreateDirectory(Path.Combine(repo.Git.WorkingDirectory, "scripts"));
        TestRepo.Write(repo.Git, DeployConfig.FileName, Config);
        TestRepo.Write(repo.Git, "scripts/account.js", "account v2\n");
        TestRepo.Write(repo.Git, "scripts/contact.js", "﻿contact\r\nline 2\r\n");
        TestRepo.Write(repo.Git, "scripts/lead.js", "lead v2\n");
        TestRepo.Write(repo.Git, "scripts/missing.js", "not in Dataverse\n");
        TestRepo.Write(repo.Git, "scripts/managed.js", "changed\n");

        var dataverse = new FakeDataverse();
        dataverse.Add("new_/scripts/account.js", "account v1\n");
        dataverse.Add("new_/scripts/Contact.js", "contact\nline 2\n"); // only BOM and line endings differ
        dataverse.Add("new_/scripts/lead.js", "lead v1\n", modifiedBy: FakeDataverse.Colleague);
        dataverse.Add("mso_/managed.js", "original\n", managed: true);
        return (repo, dataverse);
    }

    [Fact]
    public void LoadsLinksWithoutPlugins()
    {
        var (repo, _) = Setup();
        using var _repo = repo;

        var deploy = DeployConfig.Load(repo.Git.WorkingDirectory);

        Assert.Empty(deploy.Assemblies);
        Assert.Equal(6, deploy.WebResources.Count);
        Assert.Equal(new WebResourceLink("new_/scripts/account.js", "scripts/account.js"), deploy.WebResources[0]);
    }

    [Fact]
    public void ComparesLinkedFilesWithDataverse()
    {
        var (repo, dataverse) = Setup();
        using var _repo = repo;
        var deploy = DeployConfig.Load(repo.Git.WorkingDirectory);

        var comparison = WebResourceDeploy.Compare(repo.Git.WorkingDirectory, deploy.WebResources, dataverse);

        Assert.Equal(["new_/scripts/account.js", "new_/scripts/lead.js"], comparison.Changed.Select(c => c.Link.Name));
        Assert.Equal("new_/scripts/gone.js", Assert.Single(comparison.MissingFiles).Name);
        Assert.Equal("new_/scripts/missing.js", Assert.Single(comparison.NotInEnvironment).Name);
        Assert.Equal("mso_/managed.js", Assert.Single(comparison.Managed).Name);
        Assert.Equal(1, comparison.Unchanged);
    }

    [Theory]
    [InlineData("new_/scripts/ACCOUNT.js", "", "new_/scripts/account.js")]
    [InlineData("scripts\\contact.js", "", "new_/scripts/contact.js")]
    [InlineData(".\\lead.js", "scripts", "new_/scripts/lead.js")]
    [InlineData("new_/scripts/l*", "", "new_/scripts/lead.js")]
    [InlineData("scripts/?one.js", "", "new_/scripts/gone.js")]
    [InlineData("new_/*", "", "new_/scripts/account.js,new_/scripts/contact.js,new_/scripts/lead.js,new_/scripts/gone.js,new_/scripts/missing.js")]
    public void SelectsLinksByNameOrFile(string pattern, string folder, string expected)
    {
        var (repo, _) = Setup();
        using var _repo = repo;
        var root = repo.Git.WorkingDirectory;
        var links = DeployConfig.Load(root).WebResources;

        var selected = WebResourceDeploy.Select(links, [pattern], root, Path.Combine(root, folder));

        Assert.Equal(expected.Split(','), selected.Select(l => l.Name));
    }

    [Fact]
    public void RejectsPatternsThatMatchNothing()
    {
        var (repo, _) = Setup();
        using var _repo = repo;
        var root = repo.Git.WorkingDirectory;
        var links = DeployConfig.Load(root).WebResources;

        var error = Assert.Throws<GitPaException>(() => WebResourceDeploy.Select(links, ["scripts/account.js", "nope.js"], root, root));

        Assert.Contains("'nope.js'", error.Message);
        Assert.DoesNotContain("account", error.Message);
    }

    [Fact]
    public void PublishesWhatWasUploaded_WhenAnUploadFails()
    {
        var (repo, dataverse) = Setup();
        using var _repo = repo;
        var deploy = DeployConfig.Load(repo.Git.WorkingDirectory);
        var changes = WebResourceDeploy.Compare(repo.Git.WorkingDirectory, deploy.WebResources, dataverse).Changed;
        dataverse.FailUploadOf = "new_/scripts/lead.js";

        var error = Assert.Throws<GitPaException>(() => WebResourceDeploy.Publish(changes, dataverse, repo.Reporter));

        Assert.Contains("Uploading new_/scripts/lead.js failed", error.Message);
        Assert.Equal([changes[0].Record.Id], Assert.Single(dataverse.Publishes));
    }

    int Run(TestRepo repo, FakeDataverse dataverse, TestConsole console, params string[] args) =>
        GitPaCli.Build(new Cli(console, repo.Git.WorkingDirectory), _ => new DeployTools("msbuild", "pac", "nuget"),
                new FakeToolRunner("unused"), (_, _, _) => dataverse)
            .Parse(args).Invoke();

    [Fact]
    public void Cli_PublishesChangedWebResources()
    {
        var (repo, dataverse) = Setup();
        using var _repo = repo;
        var console = new TestConsole();
        console.Profile.Width = 200;

        Assert.Equal(0, Run(repo, dataverse, console, "deploy", "--web-resources", "--yes"));

        Assert.Equal(["account v2\n", "lead v2\n"], dataverse.Uploads.Select(u => u.Content));
        Assert.Equal(2, Assert.Single(dataverse.Publishes).Count);
        Assert.Contains("Someone else changed", console.Output);
        Assert.Contains("Published 2 web resources", console.Output);
    }

    [Fact]
    public void Cli_PublishesOnlyTheNamedOnes()
    {
        var (repo, dataverse) = Setup();
        using var _repo = repo;

        Assert.Equal(0, Run(repo, dataverse, new TestConsole(), "deploy", "-w", "scripts/lead.js", "--yes", "--skip-sync"));

        Assert.Equal(["lead v2\n"], dataverse.Uploads.Select(u => u.Content));
        Assert.Single(Assert.Single(dataverse.Publishes));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Cli_PublishesOnlyTheTickedOnes(bool untickAll)
    {
        var (repo, dataverse) = Setup();
        using var _repo = repo;
        var console = new TestConsole().Interactive();
        console.Profile.Width = 200;
        console.Input.PushKey(ConsoleKey.Spacebar); // untick account.js, the first
        if (untickAll)
        {
            console.Input.PushKey(ConsoleKey.DownArrow);
            console.Input.PushKey(ConsoleKey.Spacebar); // and lead.js
        }
        console.Input.PushKey(ConsoleKey.Enter);

        var exitCode = Run(repo, dataverse, console, "deploy", "-w", "--skip-sync");

        Assert.Equal(untickAll ? 1 : 0, exitCode);
        Assert.Equal(untickAll ? [] : ["lead v2\n"], dataverse.Uploads.Select(u => u.Content));
        Assert.Contains("untick them to leave them out", console.Output);
    }

    [Fact]
    public void Cli_DryRunAndConfirmationChangeNothing()
    {
        var (repo, dataverse) = Setup();
        using var _repo = repo;

        var dryRun = new TestConsole();
        dryRun.Profile.Width = 200;
        Assert.Equal(0, Run(repo, dataverse, dryRun, "deploy", "-w", "--dry-run"));
        Assert.Contains("Would publish", dryRun.Output);
        Assert.Contains("new_/scripts/account.js", dryRun.Output);

        var refused = new TestConsole();
        Assert.Equal(1, Run(repo, dataverse, refused, "deploy", "-w", "--skip-sync"));
        Assert.Contains("--yes", refused.Output);

        Assert.Empty(dataverse.Uploads);
        Assert.Empty(dataverse.Publishes);
    }

    [Fact]
    public void Cli_SaysSo_WhenNothingChanged()
    {
        var (repo, dataverse) = Setup();
        using var _repo = repo;
        TestRepo.Write(repo.Git, "scripts/account.js", "account v1\n");
        TestRepo.Write(repo.Git, "scripts/lead.js", "lead v1\n");
        var console = new TestConsole();

        Assert.Equal(0, Run(repo, dataverse, console, "deploy", "-w", "--skip-sync"));

        Assert.Contains("Nothing to publish", console.Output);
        Assert.Empty(dataverse.Publishes);
    }

    [Fact]
    public void Cli_AsksWhatToDeploy_WhenThereAreAssembliesAndWebResources()
    {
        var (repo, dataverse) = Setup();
        using var _repo = repo;
        TestRepo.Write(repo.Git, DeployConfig.FileName, Config.Replace(
            "\"webResources\"",
            "\"plugins\": { \"assemblies\": [ { \"project\": \"P/P.csproj\", \"file\": \"P/bin/P.dll\", " +
            "\"pluginId\": \"a515c00c-60a6-458b-8319-159420f8a19b\" } ] }, \"webResources\""));
        var console = new TestConsole().Interactive();
        console.Profile.Width = 200;
        console.Input.PushKey(ConsoleKey.DownArrow); // "Web resources", after the assembly
        console.Input.PushKey(ConsoleKey.Enter);

        Assert.Equal(0, Run(repo, dataverse, console, "deploy", "--yes", "--skip-sync"));

        Assert.Single(dataverse.Publishes);
        var nonInteractive = new TestConsole();
        Assert.Equal(1, Run(repo, dataverse, nonInteractive, "deploy", "--yes"));
        Assert.Contains("--web-resources", nonInteractive.Output);

        var webResourceName = new TestConsole();
        webResourceName.Profile.Width = 200;
        Assert.Equal(1, Run(repo, dataverse, webResourceName, "deploy", "new_/scripts/lead.js", "--yes"));
        Assert.Contains("git pa deploy --web-resources new_/scripts/lead.js", webResourceName.Output);
    }
}

public class DataverseWebApiTests
{
    sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Url, string? Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Method, request.RequestUri!.ToString(),
                request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));
            Assert.Equal("Bearer token", request.Headers.Authorization!.ToString());
            return respond(request);
        }
    }

    static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [Fact]
    public void ReadsUpdatesAndPublishes()
    {
        var id = Guid.Parse("8c1b2a0e-3f5d-4c7a-9b6e-1d2f3a4b5c6d");
        var handler = new Handler(request => request.Method == HttpMethod.Get
            ? Json($$"""
                { "value": [ { "webresourceid": "{{id}}", "name": "new_/a'b.js", "ismanaged": false,
                  "content": "{{Convert.ToBase64String("x"u8.ToArray())}}", "modifiedon": "2026-10-30T01:02:03Z",
                  "_modifiedby_value": "{{FakeDataverse.Me}}",
                  "_modifiedby_value@OData.Community.Display.V1.FormattedValue": "Sophal" } ] }
                """)
            : new HttpResponseMessage(HttpStatusCode.NoContent));
        var api = new DataverseWebApi("https://contoso.crm.dynamics.com", new HttpClient(handler), () => "token");

        var record = Assert.Single(api.WebResources(["new_/a'b.js"]));
        api.UpdateContent(id, "y"u8.ToArray());
        api.Publish([id]);

        Assert.Equal((id, "new_/a'b.js", false, "x", FakeDataverse.Me, "Sophal"),
            (record.Id, record.Name, record.IsManaged, Encoding.UTF8.GetString(record.Content), record.ModifiedById, record.ModifiedByName));
        Assert.Equal(new DateTime(2026, 10, 30, 1, 2, 3, DateTimeKind.Utc), record.ModifiedOn?.ToUniversalTime());
        Assert.Contains("$filter=name eq 'new_/a''b.js'", Uri.UnescapeDataString(handler.Requests[0].Url));
        Assert.Equal((HttpMethod.Patch, $"https://contoso.crm.dynamics.com/api/data/v9.2/webresourceset({id})"),
            (handler.Requests[1].Method, handler.Requests[1].Url));
        Assert.Contains(Convert.ToBase64String("y"u8.ToArray()), handler.Requests[1].Body);
        Assert.EndsWith("/PublishXml", handler.Requests[2].Url);
        Assert.Equal($"<importexportxml><webresources><webresource>{{{id}}}</webresource></webresources></importexportxml>",
            System.Text.Json.JsonDocument.Parse(handler.Requests[2].Body!).RootElement.GetProperty("ParameterXml").GetString());
    }

    [Fact]
    public void ReportsDataverseErrors()
    {
        var handler = new Handler(_ => Json("""{ "error": { "code": "0x80040220", "message": "Principal user is missing prvWriteWebResource" } }""",
            HttpStatusCode.Forbidden));
        var api = new DataverseWebApi("https://contoso.crm.dynamics.com", new HttpClient(handler), () => "token");

        var error = Assert.Throws<GitPaException>(() => api.UpdateContent(Guid.NewGuid(), []));

        Assert.Contains("403", error.Message);
        Assert.Contains("prvWriteWebResource", error.Message);
    }

    [Theory]
    [InlineData("Bearer authorization_uri=https://login.microsoftonline.com/5f2b1c3d-0000-4000-8000-000000000000/oauth2/authorize, resource_id=https://contoso.crm.dynamics.com/",
        "https://login.microsoftonline.com/5f2b1c3d-0000-4000-8000-000000000000")]
    [InlineData("Bearer authorization_uri=\"https://login.microsoftonline.us/tenant-id/oauth2/authorize\"", "https://login.microsoftonline.us/tenant-id")]
    [InlineData("Basic realm=x", null)]
    [InlineData(null, null)]
    public void FindsTheSignInAuthorityInTheChallenge(string? challenge, string? authority) =>
        Assert.Equal(authority, DataverseWebApi.AuthorityFrom(challenge));
}
