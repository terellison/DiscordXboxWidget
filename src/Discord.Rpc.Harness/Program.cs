using System.Diagnostics;
using Discord.Rpc;
using Discord.Rpc.Protocol;
using Discord.Rpc.Transport;

// Console harness for the RPC layer. Lets the transport and codec be exercised against a
// real Discord client without the UWP widget, which cannot build until the Visual Studio
// UWP workload is installed.
//
//   dotnet run --project src/Discord.Rpc.Harness -- probe
//   dotnet run --project src/Discord.Rpc.Harness -- handshake <clientId>
//   dotnet run --project src/Discord.Rpc.Harness -- watch <clientId>

var mode = args.Length > 0 ? args[0].ToLowerInvariant() : "probe";
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

try
{
    switch (mode)
    {
        case "probe":
            await ProbeAsync(cts.Token);
            break;

        case "handshake":
            await HandshakeAsync(RequireClientId(args), cts.Token);
            break;

        case "watch":
            await WatchAsync(RequireClientId(args), cts.Token);
            break;

        default:
            Console.Error.WriteLine($"Unknown mode '{mode}'. Expected: probe | handshake | watch");
            return 2;
    }

    return 0;
}
catch (OperationCanceledException)
{
    Console.WriteLine("Cancelled.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FAILED: {ex.GetType().Name}: {ex.Message}");
    return 1;
}

static string RequireClientId(string[] args)
{
    if (args.Length < 2)
        throw new ArgumentException("This mode needs a Discord application client ID as the second argument.");
    return args[1];
}

// Verifies transport + codec end to end without needing a registered application.
// An invalid client ID still proves the round trip: Discord parses our frame and
// answers with a structured error rather than dropping the pipe.
static async Task ProbeAsync(CancellationToken ct)
{
    using var transport = new NamedPipeTransport();

    var sw = Stopwatch.StartNew();
    await transport.ConnectAsync(ct);
    Console.WriteLine($"[ok]  connected to discord-ipc-{transport.ConnectedPipeIndex} in {sw.ElapsedMilliseconds}ms");

    await transport.SendAsync(new RpcFrame(RpcOpcode.Handshake, """{"v":1,"client_id":"0"}"""), ct);
    Console.WriteLine("[..]  sent HANDSHAKE with a deliberately invalid client_id");

    var reply = await transport.ReceiveAsync(ct);
    if (reply == null)
    {
        Console.WriteLine("[!!]  pipe closed with no reply");
        return;
    }

    Console.WriteLine($"[ok]  received {reply.Value.Opcode} frame, {reply.Value.Payload.Length} bytes");
    Console.WriteLine($"      {reply.Value.Payload}");
    Console.WriteLine();
    Console.WriteLine("Framing works: Discord read our header, parsed the JSON, and replied in kind.");
}

static async Task HandshakeAsync(string clientId, CancellationToken ct)
{
    using var session = new DiscordRpcSession(clientId, new ConsoleTokenProvider(clientId));
    session.StateChanged += (_, e) => Console.WriteLine($"[state] {e.State} {e.Detail}");

    await session.ConnectAsync(ct);

    Console.WriteLine($"Granted capabilities: {session.Capabilities}");
    Console.WriteLine($"Local user id: {session.CurrentUserId ?? "<none - identify scope not granted>"}");

    if (!session.Capabilities.HasFlag(SessionCapabilities.ChannelNavigation))
        Console.WriteLine("WARN: no ChannelNavigation - the full 'rpc' scope was not granted.");

    var channel = await session.GetCurrentVoiceChannelAsync(ct);
    Console.WriteLine(channel == null
        ? "Not currently in a voice channel."
        : $"In '{channel.Name}' with {channel.Participants.Count} participant(s).");

    var settings = await session.GetVoiceSettingsAsync(ct);
    Console.WriteLine($"muted={settings.IsMuted} deafened={settings.IsDeafened}");
}

static async Task WatchAsync(string clientId, CancellationToken ct)
{
    using var session = new DiscordRpcSession(clientId, new ConsoleTokenProvider(clientId));

    session.StateChanged += (_, e) => Console.WriteLine($"[state]    {e.State} {e.Detail}");
    session.SpeakingChanged += (_, e) =>
        Console.WriteLine($"[speaking] {e.UserId} {(e.IsSpeaking ? "started" : "stopped")}");
    session.VoiceChannelChanged += (_, channel) =>
        Console.WriteLine(channel == null
            ? "[channel]  left voice"
            : $"[channel]  {channel.Name} ({channel.Participants.Count} present)");

    await session.ConnectAsync(ct);
    Console.WriteLine("Watching. Ctrl+C to stop.");
    await Task.Delay(Timeout.Infinite, ct);
}

/// <summary>
/// Minimal token provider for local testing. Caches to disk so the Discord consent
/// dialog only appears once. Not suitable for distribution: the client secret comes from
/// an environment variable and the cached token is stored unencrypted.
/// </summary>
internal sealed class ConsoleTokenProvider(string clientId) : IOAuthTokenProvider
{
    private static readonly HttpClient Http = new();

    private readonly string _cachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DiscordXboxWidget", $"token-{clientId}.txt");

    public async Task<string?> TryGetCachedTokenAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_cachePath)) return null;

        var token = (await File.ReadAllTextAsync(_cachePath, cancellationToken)).Trim();
        return string.IsNullOrEmpty(token) ? null : token;
    }

    public async Task<string> ExchangeCodeAsync(string authorizationCode, CancellationToken cancellationToken)
    {
        var secret = Environment.GetEnvironmentVariable("DISCORD_CLIENT_SECRET");
        if (string.IsNullOrWhiteSpace(secret))
            throw new InvalidOperationException(
                "Set DISCORD_CLIENT_SECRET to the secret from your app at https://discord.com/developers/applications");

        using var response = await Http.PostAsync(
            "https://discord.com/api/oauth2/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["client_secret"] = secret,
                ["grant_type"] = "authorization_code",
                ["code"] = authorizationCode,
                // Discord requires this to be present and to match a redirect URI
                // registered on the application, even though RPC never redirects.
                ["redirect_uri"] = "http://localhost",
            }),
            cancellationToken);

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Token exchange failed ({(int)response.StatusCode}): {body}");

        using var doc = System.Text.Json.JsonDocument.Parse(body);
        var token = doc.RootElement.GetProperty("access_token").GetString()
                    ?? throw new InvalidOperationException("Token response contained no access_token.");

        Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
        await File.WriteAllTextAsync(_cachePath, token, cancellationToken);

        return token;
    }
}
