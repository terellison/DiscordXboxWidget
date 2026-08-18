using System.Text.Json;
using System.Threading.Channels;
using Discord.Rpc.Protocol;
using Discord.Rpc.Transport;

namespace Discord.Rpc.Tests;

/// <summary>
/// A scripted stand-in for a Discord client, so the session can be tested without one running.
/// </summary>
/// <remarks>
/// Replies are keyed off the command in each sent frame and echo its nonce, which exercises
/// the session's request correlation rather than bypassing it.
///
/// Templates use placeholder substitution rather than interpolation: JSON is dense with
/// braces and interpolated raw strings treat those as delimiters.
/// </remarks>
internal sealed class FakeDiscordTransport : IDiscordTransport
{
    private readonly Channel<RpcFrame> _incoming = Channel.CreateUnbounded<RpcFrame>();

    /// <summary>Every payload the session sent, in order.</summary>
    public List<string> Sent { get; } = new();

    /// <summary>Scopes the fake reports back from AUTHENTICATE.</summary>
    public string[] GrantedScopes { get; init; } = { "rpc", "identify" };

    /// <summary>When set, commands other than the auth pair are answered with this error code.</summary>
    public int? FailEveryCommandWith { get; init; }

    public string CurrentUserId { get; init; } = "299410719856787459";

    /// <summary>Null models the user not being in a voice channel.</summary>
    public string? ChannelJson { get; set; } =
        """
        {"id":"chan1","name":"Lounge","guild_id":"guild1","voice_states":[
          {"nick":"Ross","user":{"id":"299410719856787459","username":"rasengo","avatar":"abc","discriminator":"0"},
           "voice_state":{"mute":false,"deaf":false,"self_mute":false,"self_deaf":false}},
          {"nick":null,"user":{"id":"755304153961594881","username":"spikenade","avatar":null,"discriminator":"0"},
           "voice_state":{"mute":false,"deaf":false,"self_mute":true,"self_deaf":false}}]}
        """;

    public bool IsConnected { get; private set; }

    public Task ConnectAsync(CancellationToken cancellationToken)
    {
        IsConnected = true;
        return Task.CompletedTask;
    }

    public Task SendAsync(RpcFrame frame, CancellationToken cancellationToken)
    {
        Sent.Add(frame.Payload);

        if (frame.Opcode == RpcOpcode.Handshake)
        {
            Push(Fill("""{"cmd":"DISPATCH","evt":"READY","data":{"v":1,"user":{"id":"__USER__"}}}""", null));
            return Task.CompletedTask;
        }

        using var doc = JsonDocument.Parse(frame.Payload);
        var root = doc.RootElement;
        var cmd = root.GetProperty("cmd").GetString()!;
        var nonce = root.TryGetProperty("nonce", out var n) ? n.GetString() : null;

        if (FailEveryCommandWith is { } code && cmd is not ("AUTHORIZE" or "AUTHENTICATE"))
        {
            Push(Fill(
                """{"cmd":"__CMD__","evt":"ERROR","nonce":"__NONCE__","data":{"code":__CODE__,"message":"scripted failure"}}""",
                nonce, cmd).Replace("__CODE__", code.ToString()));
            return Task.CompletedTask;
        }

        var template = cmd switch
        {
            "AUTHORIZE" =>
                """{"cmd":"AUTHORIZE","nonce":"__NONCE__","data":{"code":"auth-code"}}""",

            "AUTHENTICATE" =>
                """{"cmd":"AUTHENTICATE","nonce":"__NONCE__","data":{"user":{"id":"__USER__","username":"rasengo"},"scopes":[__SCOPES__]}}""",

            "GET_SELECTED_VOICE_CHANNEL" =>
                """{"cmd":"GET_SELECTED_VOICE_CHANNEL","nonce":"__NONCE__","data":__CHANNEL__}""",

            "GET_VOICE_SETTINGS" =>
                """{"cmd":"GET_VOICE_SETTINGS","nonce":"__NONCE__","data":{"mute":false,"deaf":false}}""",

            "GET_GUILDS" =>
                """{"cmd":"GET_GUILDS","nonce":"__NONCE__","data":{"guilds":[{"id":"g1","name":"XBOX"}]}}""",

            // Deliberately mixes a text channel (0) and a category (4) with the voice one.
            "GET_CHANNELS" =>
                """{"cmd":"GET_CHANNELS","nonce":"__NONCE__","data":{"channels":[{"id":"t1","name":"general","type":0},{"id":"cat","name":"Voice Channels","type":4},{"id":"v1","name":"Lounge","type":2}]}}""",

            _ => """{"cmd":"__CMD__","nonce":"__NONCE__","data":null}""",
        };

        Push(Fill(template, nonce, cmd));
        return Task.CompletedTask;
    }

    private string Fill(string template, string? nonce, string? cmd = null) =>
        template
            .Replace("__NONCE__", nonce ?? string.Empty)
            .Replace("__CMD__", cmd ?? string.Empty)
            .Replace("__USER__", CurrentUserId)
            .Replace("__SCOPES__", string.Join(",", GrantedScopes.Select(s => "\"" + s + "\"")))
            .Replace("__CHANNEL__", ChannelJson ?? "null");

    public async Task<RpcFrame?> ReceiveAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _incoming.Reader.ReadAsync(cancellationToken);
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }

    /// <summary>Pushes an unsolicited dispatch, as Discord does for subscribed events.</summary>
    public void PushEvent(string json) => Push(json);

    private void Push(string payload) => _incoming.Writer.TryWrite(new RpcFrame(RpcOpcode.Frame, payload));

    public void Dispose()
    {
        IsConnected = false;
        _incoming.Writer.TryComplete();
    }
}

internal sealed class FakeTokenProvider : IOAuthTokenProvider
{
    public int ExchangeCount { get; private set; }
    public string? CachedToken { get; init; }

    public Task<string?> TryGetCachedTokenAsync(CancellationToken cancellationToken) =>
        Task.FromResult(CachedToken);

    public Task<string> ExchangeCodeAsync(string code, string verifier, CancellationToken cancellationToken)
    {
        ExchangeCount++;
        Assert.False(string.IsNullOrWhiteSpace(verifier), "PKCE verifier must reach the exchange.");
        return Task.FromResult("access-token");
    }
}
