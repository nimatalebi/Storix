using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Drive.v3;
using Google.Apis.Http;
using Google.Apis.Services;
using Google.Apis.Util.Store;
using NT.Storix.Core.Models;

namespace NT.Storix.Core.Destinations;

/// <summary>Google credentials for the Drive destination (service account or user OAuth).</summary>
public static class GoogleDriveAuth
{
    private static readonly string[] Scopes = [DriveService.ScopeConstants.Drive];

    public static IConfigurableHttpClientInitializer CreateCredential(GoogleDriveOptions options)
    {
        if (options.AuthMode == GoogleDriveAuthMode.UserAccount)
        {
            if (string.IsNullOrWhiteSpace(options.OAuthClientId) || string.IsNullOrWhiteSpace(options.OAuthClientSecret))
            {
                throw new InvalidOperationException("Google Drive OAuth client id and secret are required.");
            }

            if (string.IsNullOrWhiteSpace(options.RefreshToken))
            {
                throw new InvalidOperationException("Not signed in to Google Drive. Use 'Sign in with Google' in the destination settings.");
            }

            return new UserCredential(CreateFlow(options.OAuthClientId, options.OAuthClientSecret), "storix", new TokenResponse { RefreshToken = options.RefreshToken });
        }

        if (string.IsNullOrWhiteSpace(options.ServiceAccountKeyPath) || !File.Exists(options.ServiceAccountKeyPath))
        {
            throw new InvalidOperationException("Google Drive service account key file was not found.");
        }

        return CredentialFactory
            .FromFile<ServiceAccountCredential>(options.ServiceAccountKeyPath)
            .ToGoogleCredential()
            .CreateScoped(Scopes);
    }

    /// <summary>
    /// Interactive sign-in (opens the browser, listens on a loopback port). Returns the refresh token and the account e-mail.
    /// </summary>
    public static async Task<(string RefreshToken, string? Email)> SignInAsync(string clientId, string clientSecret, CancellationToken cancellationToken)
    {
        var flow = CreateFlow(clientId, clientSecret);
        var app = new AuthorizationCodeInstalledApp(flow, new LocalServerCodeReceiver());
        var credential = await app.AuthorizeAsync("storix", cancellationToken);
        var refreshToken = credential.Token.RefreshToken
                           ?? throw new InvalidOperationException("Google did not return a refresh token. Remove Storix from your Google account's third-party access and sign in again.");

        using var service = new DriveService(new BaseClientService.Initializer { HttpClientInitializer = credential, ApplicationName = "Storix" });
        var about = service.About.Get();
        about.Fields = "user(emailAddress)";
        var email = (await about.ExecuteAsync(cancellationToken)).User?.EmailAddress;
        return (refreshToken, email);
    }

    private static GoogleAuthorizationCodeFlow CreateFlow(string clientId, string clientSecret) => new(new GoogleAuthorizationCodeFlow.Initializer
    {
        ClientSecrets = new ClientSecrets { ClientId = clientId.Trim(), ClientSecret = clientSecret.Trim() },
        Scopes = Scopes,

        // Tokens are stored (encrypted) in the Storix configuration, not in the user profile.
        DataStore = new NullDataStore(),

        // Always ask for consent so Google returns a refresh token.
        Prompt = "consent",
    });
}
