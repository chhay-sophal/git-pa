using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GitPa;

/// <param name="Content">The saved content (which may not be published yet).</param>
/// <param name="ModifiedById">The user who last changed the record, if known.</param>
public sealed record WebResourceRecord(
    Guid Id, string Name, bool IsManaged, byte[] Content, DateTime? ModifiedOn, Guid? ModifiedById, string? ModifiedByName);

/// <summary>The Dataverse calls web resource deploy needs; tests pass a fake.</summary>
public interface IDataverse
{
    /// <summary>The signed-in user's systemuserid.</summary>
    Guid UserId();

    /// <summary>The web resources with these names (compared case-insensitively); missing names are left out.</summary>
    IReadOnlyList<WebResourceRecord> WebResources(IReadOnlyCollection<string> names);

    void UpdateContent(Guid id, byte[] content);

    void Publish(IReadOnlyCollection<Guid> ids);
}

/// <summary>Connects to an environment; may sign in (in a browser, when <paramref name="interactive"/>).</summary>
public delegate IDataverse DataverseConnector(string environment, bool interactive, IReporter reporter);

/// <summary>The Dataverse Web API, called with a bearer token.</summary>
public sealed class DataverseWebApi(string environment, HttpClient http, Func<string> token) : IDataverse
{
    const string FormattedValue = "@OData.Community.Display.V1.FormattedValue";

    static readonly JsonSerializerOptions PlainJson = new();

    /// <summary>Names per request: each adds its length to the URL, which Dataverse limits.</summary>
    const int NameBatchSize = 20;

    public Guid UserId() => Send(HttpMethod.Get, "WhoAmI").GetProperty("UserId").GetGuid();

    public IReadOnlyList<WebResourceRecord> WebResources(IReadOnlyCollection<string> names) =>
        names.Chunk(NameBatchSize).SelectMany(batch =>
        {
            var filter = string.Join(" or ", batch.Select(name => $"name eq '{name.Replace("'", "''")}'"));
            var result = Send(HttpMethod.Get,
                "webresourceset?$select=webresourceid,name,ismanaged,content,modifiedon,_modifiedby_value" +
                $"&$filter={Uri.EscapeDataString(filter)}");
            return result.GetProperty("value").EnumerateArray().Select(Record).ToList();
        }).ToList();

    public void UpdateContent(Guid id, byte[] content) =>
        Send(HttpMethod.Patch, $"webresourceset({id})", new { content = Convert.ToBase64String(content) });

    public void Publish(IReadOnlyCollection<Guid> ids)
    {
        var resources = string.Concat(ids.Select(id => $"<webresource>{{{id}}}</webresource>"));
        Send(HttpMethod.Post, "PublishXml",
            new { ParameterXml = $"<importexportxml><webresources>{resources}</webresources></importexportxml>" });
    }

    static WebResourceRecord Record(JsonElement row) => new(
        row.GetProperty("webresourceid").GetGuid(),
        row.GetProperty("name").GetString()!,
        row.TryGetProperty("ismanaged", out var managed) && managed.ValueKind == JsonValueKind.True,
        row.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String
            ? Convert.FromBase64String(content.GetString()!) : [],
        row.TryGetProperty("modifiedon", out var on) && on.ValueKind == JsonValueKind.String ? on.GetDateTime() : null,
        row.TryGetProperty("_modifiedby_value", out var by) && by.ValueKind == JsonValueKind.String ? by.GetGuid() : null,
        row.TryGetProperty("_modifiedby_value" + FormattedValue, out var byName) ? byName.GetString() : null);

    JsonElement Send(HttpMethod method, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(method, $"{environment}/api/data/v9.2/{path}");
        request.Headers.Authorization = new("Bearer", token());
        request.Headers.Add("Accept", "application/json");
        request.Headers.Add("OData-MaxVersion", "4.0");
        request.Headers.Add("OData-Version", "4.0");
        request.Headers.Add("Prefer", $"odata.include-annotations=\"{FormattedValue[1..]}\"");
        if (body is not null)
            request.Content = JsonContent.Create(body, options: PlainJson); // names as written: action parameters are case-sensitive

        string text;
        try
        {
            using var response = http.SendAsync(request).GetAwaiter().GetResult();
            text = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
                throw new GitPaException($"Dataverse refused the request ({(int)response.StatusCode}): {ErrorMessage(text)}");
        }
        catch (HttpRequestException e)
        {
            throw new GitPaException($"Couldn't reach {environment}: {e.Message}");
        }
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    static string ErrorMessage(string body)
    {
        try
        {
            return JsonDocument.Parse(body).RootElement.GetProperty("error").GetProperty("message").GetString() ?? body;
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return body.Length > 300 ? body[..300] + "..." : body;
        }
    }

    /// <summary>
    /// The sign-in authority for an environment, from the authorization_uri in the challenge its Web API
    /// returns without a token. Using the environment's own tenant lets guest accounts sign in.
    /// </summary>
    public static string? AuthorityFrom(string? wwwAuthenticate) =>
        wwwAuthenticate is not null && Regex.Match(wwwAuthenticate, @"authorization_uri=""?(https://[^/""\s,]+/[^/""\s,]+)") is { Success: true } m
            ? m.Groups[1].Value
            : null;
}
