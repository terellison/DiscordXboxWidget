using System;
using System.Threading;
using System.Threading.Tasks;
using Discord.Rpc;
using Windows.Security.Credentials;
using Windows.Storage;

namespace DiscordWidget
{
    /// <summary>
    /// Local configuration for the widget. The client ID is not a secret; the client secret
    /// is, which is why it is read from local settings rather than compiled in.
    /// </summary>
    public static class WidgetConfig
    {
        /// <summary>
        /// From https://discord.com/developers/applications. Safe to commit.
        /// </summary>
        public const string ClientId = "REPLACE_WITH_YOUR_CLIENT_ID";

        public static bool IsConfigured =>
            !string.IsNullOrWhiteSpace(ClientId) && ClientId != "REPLACE_WITH_YOUR_CLIENT_ID";
    }

    /// <summary>
    /// Stores the Discord access token in the Windows credential locker rather than in
    /// app-local settings, so it is encrypted at rest per user.
    /// </summary>
    /// <remarks>
    /// The client secret still has to live somewhere on the machine for the code exchange,
    /// and a locally-installed widget cannot keep it genuinely secret. That is tolerable for
    /// a sideloaded personal build and is one of the reasons this design does not target
    /// Store distribution. See IOAuthTokenProvider for the wider discussion.
    /// </remarks>
    public sealed class VaultTokenProvider : IOAuthTokenProvider
    {
        private const string ResourceName = "DiscordXboxWidget";
        private const string SecretSettingKey = "DiscordClientSecret";

        private readonly string _clientId;

        public VaultTokenProvider(string clientId)
        {
            _clientId = clientId;
        }

        public Task<string> TryGetCachedTokenAsync(CancellationToken cancellationToken)
        {
            try
            {
                var vault = new PasswordVault();
                var credential = vault.Retrieve(ResourceName, _clientId);
                credential.RetrievePassword();
                return Task.FromResult(credential.Password);
            }
            catch (Exception)
            {
                // PasswordVault throws rather than returning null when nothing is stored.
                return Task.FromResult<string>(null);
            }
        }

        public async Task<string> ExchangeCodeAsync(string authorizationCode, CancellationToken cancellationToken)
        {
            var secret = ApplicationData.Current.LocalSettings.Values[SecretSettingKey] as string;
            if (string.IsNullOrWhiteSpace(secret))
            {
                throw new InvalidOperationException(
                    $"No client secret stored. Set ApplicationData LocalSettings['{SecretSettingKey}'] " +
                    "before first launch.");
            }

            using (var http = new System.Net.Http.HttpClient())
            using (var content = new System.Net.Http.FormUrlEncodedContent(
                new System.Collections.Generic.Dictionary<string, string>
                {
                    ["client_id"] = _clientId,
                    ["client_secret"] = secret,
                    ["grant_type"] = "authorization_code",
                    ["code"] = authorizationCode,
                    ["redirect_uri"] = "http://localhost",
                }))
            {
                var response = await http.PostAsync("https://discord.com/api/oauth2/token", content)
                    .ConfigureAwait(false);

                var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException($"Token exchange failed ({(int)response.StatusCode}): {body}");

                using (var doc = System.Text.Json.JsonDocument.Parse(body))
                {
                    var token = doc.RootElement.GetProperty("access_token").GetString();
                    if (string.IsNullOrEmpty(token))
                        throw new InvalidOperationException("Token response contained no access_token.");

                    var vault = new PasswordVault();
                    vault.Add(new PasswordCredential(ResourceName, _clientId, token));

                    return token;
                }
            }
        }
    }
}
