using System.Text.Json;

namespace Discord.Rpc.Bridge;

/// <summary>
/// Per-user configuration, read from %LOCALAPPDATA%\DiscordXboxWidget\config.json.
/// </summary>
/// <remarks>
/// A built-in application id ships as the default, and this file overrides it.
///
/// Whether the default works for anyone other than its author depends on Discord: the rpc
/// scope is restricted to an application's owner plus its 50-slot tester allowlist unless
/// the app is approved for general RPC access. If the shipped app holds that approval the
/// default works for everybody and this file is never needed; if it does not, users point
/// it at an application they registered themselves.
///
/// Supporting both means the package does not have to be rebuilt when that answer changes,
/// and users who would rather grant scopes to their own app can always do so.
/// </remarks>
internal sealed class BridgeConfig
{
    public string? ClientId { get; init; }

    public static string Directory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DiscordXboxWidget");

    public static string Path_ => System.IO.Path.Combine(Directory, "config.json");

    /// <summary>
    /// Loads the config, writing a commented template first if none exists so the user has
    /// something concrete to edit rather than having to create the file from scratch.
    /// </summary>
    public static BridgeConfig Load()
    {
        try
        {
            if (!File.Exists(Path_))
            {
                WriteTemplate();
                return new BridgeConfig();
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(Path_));
            var id = doc.RootElement.TryGetProperty("clientId", out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;

            // Treat the placeholder as unset; otherwise the user gets an opaque Discord
            // "Invalid Client ID" instead of being told to fill the file in.
            if (string.IsNullOrWhiteSpace(id) || id == Placeholder) return new BridgeConfig();

            return new BridgeConfig { ClientId = id };
        }
        catch (Exception ex)
        {
            Program.Log($"could not read {Path_}: {ex.Message}");
            return new BridgeConfig();
        }
    }

    private const string Placeholder = "YOUR_DISCORD_APPLICATION_ID";

    private static void WriteTemplate()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);

            // JSON has no comments, so the guidance goes in an ignored field rather than
            // leaving the user a bare key with no idea what belongs in it.
            var template = new
            {
                _readme = new[]
                {
                    "1. Create an application at https://discord.com/developers/applications",
                    "2. On its OAuth2 tab, enable Public Client",
                    "3. On the same tab, add http://localhost as a redirect URI",
                    "4. Paste the Application ID into clientId below",
                },
                clientId = Placeholder,
            };

            File.WriteAllText(Path_, JsonSerializer.Serialize(
                template, new JsonSerializerOptions { WriteIndented = true }));
            Program.Log($"wrote config template to {Path_}");
        }
        catch (Exception ex)
        {
            Program.Log($"could not write config template: {ex.Message}");
        }
    }

    /// <summary>
    /// Shown when Discord refuses the scope for the built-in application. That means this
    /// account is neither its owner nor on its tester allowlist, so the fix is to register
    /// an application of their own rather than anything the user did wrong locally.
    /// </summary>
    public static string ScopeDeniedOnDefaultAppMessage =>
        "This Discord account is not authorized for the built-in application. Create your " +
        $"own at discord.com/developers/applications, enable Public Client on its OAuth2 tab, " +
        $"add http://localhost as a redirect URI, then set clientId in {Path_}.";
}
