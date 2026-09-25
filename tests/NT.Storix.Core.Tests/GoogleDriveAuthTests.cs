using NT.Storix.Core.Destinations;
using NT.Storix.Core.Models;

namespace NT.Storix.Core.Tests;

public class GoogleDriveAuthTests
{
    [Fact]
    public void User_account_requires_client_and_sign_in()
    {
        var options = new GoogleDriveOptions { AuthMode = GoogleDriveAuthMode.UserAccount };
        Assert.Contains("client id", Assert.Throws<InvalidOperationException>(() => GoogleDriveAuth.CreateCredential(options)).Message);

        options.OAuthClientId = "id";
        options.OAuthClientSecret = "secret";
        Assert.Contains("Not signed in", Assert.Throws<InvalidOperationException>(() => GoogleDriveAuth.CreateCredential(options)).Message);

        options.RefreshToken = "1//refresh";
        Assert.NotNull(GoogleDriveAuth.CreateCredential(options));
    }

    [Fact]
    public void Service_account_requires_key_file()
    {
        var options = new GoogleDriveOptions { ServiceAccountKeyPath = "missing.json" };
        Assert.Throws<InvalidOperationException>(() => GoogleDriveAuth.CreateCredential(options));
    }

    [Fact]
    public void Refresh_token_and_client_secret_are_secrets()
    {
        var job = new BackupJob
        {
            Destinations = [new DestinationDefinition { Kind = DestinationKind.GoogleDrive, GoogleDrive = { OAuthClientSecret = "cs", RefreshToken = "rt", OAuthClientId = "id" } }],
        };

        var json = Configuration.ConfigurationPorter.Export([job], null, passphrase: null);
        Assert.DoesNotContain("\"rt\"", json);
        Assert.DoesNotContain("\"cs\"", json);
        Assert.Contains("\"id\"", json);
    }
}
