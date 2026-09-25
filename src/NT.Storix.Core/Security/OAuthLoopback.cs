using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NT.Storix.Core.Security;

public sealed record OAuthTokens(string AccessToken, string? RefreshToken, DateTimeOffset ExpiresAt);

/// <summary>
/// OAuth 2.0 authorization code flow with PKCE for desktop apps: opens the browser and receives the code on
/// <see cref="RedirectUri"/>. No client secret is needed.
/// </summary>
public static class OAuthLoopback
{
    public const int Port = 53682;
    public const string RedirectUri = "http://localhost:53682/";

    public static async Task<OAuthTokens> AuthorizeAsync(
        string authorizeEndpoint,
        string tokenEndpoint,
        string clientId,
        string? scope,
        IReadOnlyDictionary<string, string>? extraParameters,
        HttpClient http,
        CancellationToken cancellationToken)
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(16));

        var query = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["response_type"] = "code",
            ["redirect_uri"] = RedirectUri,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["state"] = state,
        };
        if (!string.IsNullOrEmpty(scope))
        {
            query["scope"] = scope;
        }

        foreach (var (key, value) in extraParameters ?? new Dictionary<string, string>())
        {
            query[key] = value;
        }

        using var listener = new HttpListener();
        listener.Prefixes.Add(RedirectUri);
        listener.Start();

        var url = authorizeEndpoint + "?" + string.Join("&", query.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

        var context = await listener.GetContextAsync().WaitAsync(cancellationToken);
        var code = context.Request.QueryString["code"];
        var error = context.Request.QueryString["error_description"] ?? context.Request.QueryString["error"];
        var ok = code is not null && context.Request.QueryString["state"] == state;

        var page = Encoding.UTF8.GetBytes(ok
            ? "<html><body style=\"font-family:sans-serif\"><h2>Storix is connected.</h2>You can close this window.</body></html>"
            : $"<html><body style=\"font-family:sans-serif\"><h2>Sign-in failed</h2>{WebUtility.HtmlEncode(error)}</body></html>");
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.OutputStream.WriteAsync(page, cancellationToken);
        context.Response.Close();

        if (!ok)
        {
            throw new InvalidOperationException($"Sign-in failed: {error ?? "invalid response"}.");
        }

        return await RequestTokenAsync(http, tokenEndpoint, new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code!,
            ["redirect_uri"] = RedirectUri,
            ["client_id"] = clientId,
            ["code_verifier"] = verifier,
        }, cancellationToken);
    }

    public static Task<OAuthTokens> RefreshAsync(HttpClient http, string tokenEndpoint, string clientId, string refreshToken, string? scope, CancellationToken cancellationToken)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = clientId,
        };
        if (!string.IsNullOrEmpty(scope))
        {
            form["scope"] = scope;
        }

        return RequestTokenAsync(http, tokenEndpoint, form, cancellationToken);
    }

    private static async Task<OAuthTokens> RequestTokenAsync(HttpClient http, string endpoint, Dictionary<string, string> form, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsync(endpoint, new FormUrlEncodedContent(form), cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            // The body describes the error; it never contains the token we sent.
            throw new InvalidOperationException($"Token request failed ({(int)response.StatusCode}): {(body.Length > 300 ? body[..300] : body)}");
        }

        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        var expires = root.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var seconds) ? seconds : 3600;
        return new OAuthTokens(
            root.GetProperty("access_token").GetString()!,
            root.TryGetProperty("refresh_token", out var r) ? r.GetString() : null,
            DateTimeOffset.UtcNow.AddSeconds(expires - 60));
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>
/// Stores refresh tokens that the provider rotated (OneDrive), keyed by destination id. The service plugs in a
/// persistent store; the default keeps them in memory.
/// </summary>
public static class OAuthTokenStore
{
    private static readonly Dictionary<string, string> Memory = [];

    public static Func<string, string?> Load { get; set; } = key => { lock (Memory) { return Memory.GetValueOrDefault(key); } };

    public static Action<string, string> Save { get; set; } = (key, value) => { lock (Memory) { Memory[key] = value; } };

    /// <summary>Keeps rotated tokens in the metadata database, encrypted like every other secret.</summary>
    public static void UseDatabase(Persistence.SettingsRepository settings, ISecretProtector protector)
    {
        Load = key => protector.Unprotect(settings.GetValue("oauth:" + key));
        Save = (key, value) => settings.SetValue("oauth:" + key, protector.Protect(value)!);
    }
}
