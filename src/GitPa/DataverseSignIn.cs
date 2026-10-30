using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;

namespace GitPa;

/// <summary>
/// Signs in to Dataverse with your Microsoft account, for the calls pac has no command for (like updating
/// web resources). Tokens are cached per user (encrypted with DPAPI on Windows, the keychain on macOS),
/// so the browser only opens the first time.
/// </summary>
public sealed class DataverseSignIn
{
    /// <summary>
    /// Microsoft's public client for Dataverse developer tools (used by pac, XrmToolBox and the wrsync
    /// extension), so no app registration or admin consent is needed.
    /// </summary>
    const string ClientId = "51f81489-12ee-4a9e-aaae-a2591f45987d";

    const string DefaultAuthority = "https://login.microsoftonline.com/organizations";

    static readonly string CacheFolder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "git-pa");

    readonly IPublicClientApplication app;
    readonly string[] scopes;

    DataverseSignIn(IPublicClientApplication app, string environment)
    {
        this.app = app;
        scopes = [$"{environment}/.default"];
    }

    /// <summary>Connects to the environment's Web API, signing in first if needed.</summary>
    public static IDataverse Connect(string environment, bool interactive, IReporter reporter)
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        var signIn = Create(environment, FindAuthority(http, environment));
        signIn.Token(interactive, reporter);
        return new DataverseWebApi(environment, http, () => signIn.Token(interactive: false, reporter));
    }

    static DataverseSignIn Create(string environment, string authority)
    {
        var app = PublicClientApplicationBuilder.Create(ClientId)
            .WithAuthority(authority)
            .WithRedirectUri("http://localhost")
            .Build();
        RegisterCache(app);
        return new DataverseSignIn(app, environment);
    }

    static void RegisterCache(IPublicClientApplication app)
    {
        var protectedStorage = new StorageCreationPropertiesBuilder("msal.cache", CacheFolder)
            .WithMacKeyChain("git-pa", "msal-cache")
            .WithLinuxKeyring("git-pa.msal.cache", MsalCacheHelper.LinuxKeyRingDefaultCollection, "git-pa sign-in",
                new("Product", "git-pa"), new("Version", "1"))
            .Build();
        try
        {
            var helper = MsalCacheHelper.CreateAsync(protectedStorage).GetAwaiter().GetResult();
            helper.VerifyPersistence();
            helper.RegisterCache(app.UserTokenCache);
        }
        catch (MsalCachePersistenceException)
        {
            // No keyring (e.g. Linux without a desktop session): a file readable only by this user.
            var file = new StorageCreationPropertiesBuilder("msal.cache.plain", CacheFolder).WithUnprotectedFile().Build();
            MsalCacheHelper.CreateAsync(file).GetAwaiter().GetResult().RegisterCache(app.UserTokenCache);
        }
    }

    /// <summary>The environment's own tenant (so guest accounts work), or "organizations" if it can't be found.</summary>
    static string FindAuthority(HttpClient http, string environment)
    {
        try
        {
            using var response = http.GetAsync($"{environment}/api/data/v9.2/").GetAwaiter().GetResult();
            return DataverseWebApi.AuthorityFrom(response.Headers.WwwAuthenticate.ToString()) ?? DefaultAuthority;
        }
        catch (HttpRequestException e)
        {
            throw new GitPaException($"Couldn't reach {environment}: {e.Message}");
        }
    }

    /// <summary>A token from the cache, refreshed as needed; signs in in the browser when allowed and necessary.</summary>
    public string Token(bool interactive, IReporter reporter)
    {
        try
        {
            foreach (var account in app.GetAccountsAsync().GetAwaiter().GetResult())
            {
                try
                {
                    return app.AcquireTokenSilent(scopes, account).ExecuteAsync().GetAwaiter().GetResult().AccessToken;
                }
                catch (MsalUiRequiredException)
                {
                    // this account can't get a token without signing in again; try the next
                }
            }

            if (!interactive)
                throw new GitPaException(
                    "You're not signed in to Dataverse. Run git pa deploy in a terminal once to sign in.");
            reporter.Step("Signing in to Dataverse in your browser");
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            return app.AcquireTokenInteractive(scopes)
                .WithPrompt(Prompt.SelectAccount)
                .WithUseEmbeddedWebView(false)
                .ExecuteAsync(timeout.Token).GetAwaiter().GetResult().AccessToken;
        }
        catch (MsalException e)
        {
            throw new GitPaException($"Signing in to Dataverse failed: {e.Message}");
        }
        catch (OperationCanceledException)
        {
            throw new GitPaException("Signing in to Dataverse timed out. Nothing was deployed.");
        }
    }

    /// <summary>Forgets every cached sign-in.</summary>
    public static int SignOut()
    {
        var app = PublicClientApplicationBuilder.Create(ClientId).WithAuthority(DefaultAuthority).Build();
        RegisterCache(app);
        var accounts = app.GetAccountsAsync().GetAwaiter().GetResult().ToList();
        foreach (var account in accounts)
            app.RemoveAsync(account).GetAwaiter().GetResult();
        return accounts.Count;
    }
}
