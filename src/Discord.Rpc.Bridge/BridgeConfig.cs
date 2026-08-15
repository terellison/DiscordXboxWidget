using System.Text.Json;

namespace Discord.Rpc.Bridge;

/// <summary>
/// Per-user configuration, read from %LOCALAPPDATA%\DiscordXboxWidget\config.json.
/// </summary>
/// <remarks>
/// The Discord application id is supplied by the user and cannot be shipped in the package.
/// Discord's Developer Terms of Service name the Application ID as a developer credential
/// and state that developer credentials may not be embedded in open source projects, so a
/// built-in default would be a licence violation regardless of whether it worked.
///
/// It would mostly not work anyway: the rpc scope is restricted to an application's owner
/// plus its 50-slot tester allowlist unless approved for general RPC access. The two
/// constraints point the same way, and the result is better for users, who grant scopes to
/// an application they control rather than to someone else's.
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

    /// <summary>Shown in the widget when no application id has been configured.</summary>
    public static string NotConfiguredMessage =>
        $"No Discord application configured. Edit {Path_} and set clientId. Create an app at " +
        "discord.com/developers/applications, enable Public Client on its OAuth2 tab, and add " +
        "http://localhost as a redirect URI.";
}
