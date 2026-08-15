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

        case "wsprobe":
            await WebSocketProbeAsync(RequireClientId(args), cts.Token);
            break;

        case "handshake":
            await HandshakeAsync(RequireClientId(args), cts.Token);
            break;

        case "watch":
            await WatchAsync(RequireClientId(args), cts.Token);
            break;

        case "toggle":
            await ToggleAsync(RequireClientId(args), cts.Token);
            break;

        case "rawchannel":
            await RawChannelAsync(RequireClientId(args), cts.Token);
            break;

        case "mutediag":
            await MuteDiagAsync(RequireClientId(args), cts.Token);
            break;

        case "channels":
            await ChannelsAsync(RequireClientId(args), cts.Token);
            break;

        default:
            Console.Error.WriteLine($"Unknown mode '{mode}'. Expected: probe | wsprobe | handshake | watch | toggle");
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

// Verifies the transport the UWP widget will actually use. Runs unrestricted here;
// inside an AppContainer the same code additionally needs a loopback exemption.
static async Task WebSocketProbeAsync(string clientId, CancellationToken ct)
{
    using var transport = new WebSocketTransport(clientId);

    var sw = Stopwatch.StartNew();
    await transport.ConnectAsync(ct);
    Console.WriteLine($"[ok]  connected to ws://127.0.0.1:{transport.ConnectedPort} in {sw.ElapsedMilliseconds}ms");

    // No handshake frame: the client ID was supplied in the query string.
    var reply = await transport.ReceiveAsync(ct);
    if (reply == null)
    {
        Console.WriteLine("[!!]  socket closed with no reply");
        return;
    }

    Console.WriteLine($"[ok]  received {reply.Value.Opcode} frame, {reply.Value.Payload.Length} bytes");
    Console.WriteLine($"      {Truncate(reply.Value.Payload, 400)}");
}

static string Truncate(string value, int max) =>
    value.Length <= max ? value : value.Substring(0, max) + "...";

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
    {
        if (channel == null)
        {
            Console.WriteLine("[channel]  left voice");
            return;
        }

        Console.WriteLine($"[channel]  {channel.Name} ({channel.Participants.Count} present)");

        // Printed per participant so the ParseChannel field mapping is visible: a bare
        // count looks identical whether or not names and voice state actually parsed.
        foreach (var p in channel.Participants)
        {
            var flags = new List<string>();
            if (p.IsMuted) flags.Add("muted");
            if (p.IsDeafened) flags.Add("deafened");
            if (p.Id == session.CurrentUserId) flags.Add("self");

            var suffix = flags.Count > 0 ? $"  [{string.Join(", ", flags)}]" : string.Empty;
            var nick = p.Nickname == null ? "" : $" (nick: {p.Nickname})";

            Console.WriteLine($"           - {p.DisplayName}{nick}  id={p.Id}{suffix}");
            Console.WriteLine($"             avatar: {p.AvatarUrl}");
        }
    };

    await session.ConnectAsync(ct);
    Console.WriteLine("Watching. Ctrl+C to stop.");
    await Task.Delay(Timeout.Infinite, ct);
}

// Lists guilds and their voice channels, printing the raw payload beside the parsed
// result so a wrong field shape shows up immediately rather than as an empty picker.
static async Task ChannelsAsync(string clientId, CancellationToken ct)
{
    using var session = new DiscordRpcSession(clientId, new ConsoleTokenProvider(clientId));

    string? lastRaw = null;
    session.FrameReceived += (_, payload) =>
    {
        if (payload.Contains("GET_GUILDS") || payload.Contains("GET_CHANNELS")) lastRaw = payload;
    };

    await session.ConnectAsync(ct);

    if (!session.Capabilities.HasFlag(SessionCapabilities.ChannelNavigation))
    {
        Console.WriteLine("ChannelNavigation not granted; GET_GUILDS requires the full rpc scope.");
        return;
    }

    var guilds = await session.GetGuildsAsync(ct);
    Console.WriteLine($"[raw guilds] {Truncate(lastRaw ?? "<none>", 400)}");
    Console.WriteLine($"[parsed] {guilds.Count} guild(s)");

    foreach (var guild in guilds)
    {
        var channels = await session.GetVoiceChannelsAsync(guild.Id, ct);
        Console.WriteLine($"  {guild.Name} ({guild.Id}) -> {channels.Count} voice channel(s)");
        foreach (var channel in channels)
            Console.WriteLine($"    - {channel.Name}  id={channel.Id}");
    }

    Console.WriteLine();
    Console.WriteLine($"[raw last channels] {Truncate(lastRaw ?? "<none>", 500)}");
}

// Prints every dispatch frame around a self-mute, in one process so there is no timing
// race, to establish whether Discord actually emits VOICE_STATE_UPDATE for this change.
static async Task MuteDiagAsync(string clientId, CancellationToken ct)
{
    using var session = new DiscordRpcSession(clientId, new ConsoleTokenProvider(clientId));

    // Prints the parsed mute flag on every refresh, which is what the widget renders.
    session.VoiceChannelChanged += (_, channel) =>
    {
        if (channel == null) { Console.WriteLine("[parsed] not in voice"); return; }
        foreach (var p in channel.Participants)
        {
            if (p.Id != session.CurrentUserId) continue;
            Console.WriteLine($"[parsed] {p.DisplayName} muted={p.IsMuted} deafened={p.IsDeafened}");
        }
    };

    await session.ConnectAsync(ct);
    Console.WriteLine("--- connected; toggling mute ---");

    var original = await session.GetVoiceSettingsAsync(ct);
    try
    {
        await session.SetMutedAsync(!original.IsMuted, ct);
        await Task.Delay(2500, ct);
        Console.WriteLine("--- restoring ---");
    }
    finally
    {
        await session.SetMutedAsync(original.IsMuted, CancellationToken.None);
    }

    await Task.Delay(2500, CancellationToken.None);
    Console.WriteLine("--- done ---");
}

// Dumps the unparsed GET_SELECTED_VOICE_CHANNEL payload with the local user muted and
// unmuted, so the participant voice-state field names can be read off real data rather
// than inferred from docs.
static async Task RawChannelAsync(string clientId, CancellationToken ct)
{
    using var session = new DiscordRpcSession(clientId, new ConsoleTokenProvider(clientId));
    await session.ConnectAsync(ct);

    var original = await session.GetVoiceSettingsAsync(ct);

    try
    {
        foreach (var muted in new[] { false, true })
        {
            await session.SetMutedAsync(muted, ct);
            // Discord applies the change asynchronously to the voice state it reports.
            await Task.Delay(600, ct);

            Console.WriteLine($"===== local user muted={muted} =====");
            var raw = await CaptureChannelJsonAsync(session, ct);
            Console.WriteLine(raw ?? "<not in a voice channel>");
            Console.WriteLine();
        }
    }
    finally
    {
        await session.SetMutedAsync(original.IsMuted, CancellationToken.None);
    }
}

// GetCurrentVoiceChannelAsync returns a parsed snapshot, which is exactly the layer under
// suspicion, so the raw dispatch is captured instead.
static async Task<string?> CaptureChannelJsonAsync(DiscordRpcSession session, CancellationToken ct)
{
    string? captured = null;
    void OnFrame(object? _, string payload)
    {
        if (payload.Contains("GET_SELECTED_VOICE_CHANNEL")) captured = payload;
    }

    session.FrameReceived += OnFrame;
    try
    {
        await session.GetCurrentVoiceChannelAsync(ct);
        return captured;
    }
    finally
    {
        session.FrameReceived -= OnFrame;
    }
}

// The only mode that writes to Discord. Every other mode is read-only, so this is the
// first exercise of SET_VOICE_SETTINGS -- worth proving here rather than inside a Game Bar
// widget, where attaching a debugger is considerably more painful.
static async Task ToggleAsync(string clientId, CancellationToken ct)
{
    using var session = new DiscordRpcSession(clientId, new ConsoleTokenProvider(clientId));
    await session.ConnectAsync(ct);

    if (!session.Capabilities.HasFlag(SessionCapabilities.SetVoiceState))
    {
        Console.WriteLine("SetVoiceState not granted; cannot write voice settings.");
        return;
    }

    var original = await session.GetVoiceSettingsAsync(ct);
    Console.WriteLine($"[before]  muted={original.IsMuted} deafened={original.IsDeafened}");

    var target = !original.IsMuted;
    try
    {
        await session.SetMutedAsync(target, ct);

        // Read back rather than trusting the command's success response: this proves
        // Discord applied the change, not merely that it accepted the frame.
        var after = await session.GetVoiceSettingsAsync(ct);
        Console.WriteLine($"[set]     muted={after.IsMuted} (wanted {target})");
        Console.WriteLine(after.IsMuted == target
            ? "[ok]      write took effect"
            : "[FAIL]    Discord accepted the command but state did not change");
    }
    finally
    {
        // Always hand the mic back the way we found it, even if the read-back threw.
        await session.SetMutedAsync(original.IsMuted, CancellationToken.None);
        var restored = await session.GetVoiceSettingsAsync(CancellationToken.None);
        Console.WriteLine($"[restore] muted={restored.IsMuted}");
    }
}

/// <summary>
/// Minimal token provider for local testing. Caches to disk so the Discord consent dialog
/// only appears once. Uses PKCE, so there is no client secret to supply. Not suitable for
/// distribution: the cached token is stored unencrypted.
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
                // Replaces client_secret. Requires the PUBLIC_OAUTH2_CLIENT flag on the app;
                // without the flag Discord rejects the exchange with invalid_client.
                ["code_verifier"] = codeVerifier,
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
