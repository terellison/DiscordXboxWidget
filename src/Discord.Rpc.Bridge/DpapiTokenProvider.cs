using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Discord.Rpc.Bridge;

/// <summary>
/// Caches the Discord access token in a DPAPI-protected file scoped to the current user.
/// </summary>
/// <remarks>
/// DPAPI rather than PasswordVault: PasswordVault requires package identity, which the
/// bridge only has when Game Bar launches it. Self-test runs from a plain console have no
/// identity, and a token store that only works in one of the two modes would mean the
/// tested path is not the shipped path.
///
/// No client secret is involved; the exchange uses PKCE.
/// </remarks>
internal sealed class DpapiTokenProvider(string clientId) : IOAuthTokenProvider
{
    private static readonly HttpClient Http = new();

    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DiscordXboxWidget", $"token-{clientId}.bin");

    public async Task<string?> TryGetCachedTokenAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path)) return null;

        try
        {
            var protectedBytes = await File.ReadAllBytesAsync(_path, cancellationToken);
            var plain = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
            var token = Encoding.UTF8.GetString(plain);
            return string.IsNullOrWhiteSpace(token) ? null : token;
        }
        catch (CryptographicException)
        {
            // Written by a different user, or corrupt. Fall back to re-authorizing.
            return null;
        }
    }

    public async Task<string> ExchangeCodeAsync(
        string authorizationCode, string codeVerifier, CancellationToken cancellationToken)
    {
        using var response = await Http.PostAsync(
            "https://discord.com/api/oauth2/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["grant_type"] = "authorization_code",
                ["code"] = authorizationCode,
                // Replaces client_secret; requires PUBLIC_OAUTH2_CLIENT on the application.
                ["code_verifier"] = codeVerifier,
                ["redirect_uri"] = "http://localhost",
            }),
            cancellationToken);

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Token exchange failed ({(int)response.StatusCode}): {body}");

        using var doc = JsonDocument.Parse(body);
        var token = doc.RootElement.GetProperty("access_token").GetString()
                    ?? throw new InvalidOperationException("Token response contained no access_token.");

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var sealed_ = ProtectedData.Protect(Encoding.UTF8.GetBytes(token), null, DataProtectionScope.CurrentUser);
        await File.WriteAllBytesAsync(_path, sealed_, cancellationToken);

        return token;
    }
}
