namespace Discord.Rpc.Tests;

public class DiscordRpcSessionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Every call is bounded. A session waiting on a reply that never arrives blocks
    /// forever, which turns a failing test into a hung test run.
    /// </summary>
    private static CancellationToken Bounded => new CancellationTokenSource(Timeout).Token;

    private static async Task<(DiscordRpcSession session, FakeDiscordTransport transport)> ConnectedAsync(
        FakeDiscordTransport? transport = null, FakeTokenProvider? tokens = null)
    {
        transport ??= new FakeDiscordTransport();
        var session = new DiscordRpcSession("client-id", tokens ?? new FakeTokenProvider(), transport);
        await session.ConnectAsync(Bounded);
        return (session, transport);
    }

    [Fact]
    public async Task ConnectAsync_AuthenticatesAndReportsConnected()
    {
        var (session, _) = await ConnectedAsync();
        using var _s = session;

        Assert.Equal(SessionState.Connected, session.State);
        Assert.Equal("299410719856787459", session.CurrentUserId);
    }

    [Fact]
    public async Task ConnectAsync_SendsPkceChallengeOnAuthorize()
    {
        var tokens = new FakeTokenProvider();
        var (session, transport) = await ConnectedAsync(tokens: tokens);
        using var _s = session;

        var authorize = transport.Sent.Single(s => s.Contains("\"AUTHORIZE\""));

        Assert.Contains("code_challenge", authorize);
        Assert.Contains("\"S256\"", authorize);
        // Absence of a secret is the whole point; the verifier must never be sent here.
        Assert.DoesNotContain("code_verifier", authorize);
        Assert.Equal(1, tokens.ExchangeCount);
    }

    [Fact]
    public async Task ConnectAsync_SkipsAuthorizeWhenATokenIsCached()
    {
        // Discord shows a consent dialog for every AUTHORIZE, so a cached token must not
        // trigger one on each launch.
        var tokens = new FakeTokenProvider { CachedToken = "cached" };
        var (session, transport) = await ConnectedAsync(tokens: tokens);
        using var _s = session;

        Assert.DoesNotContain(transport.Sent, s => s.Contains("\"AUTHORIZE\""));
        Assert.Equal(0, tokens.ExchangeCount);
    }

    [Fact]
    public async Task Capabilities_ComeFromGrantedScopesNotRequestedOnes()
    {
        // A cached token can predate a scope change, so trusting the request would leave
        // the widget offering controls the token cannot drive.
        var transport = new FakeDiscordTransport { GrantedScopes = new[] { "rpc.voice.read" } };
        var (session, _) = await ConnectedAsync(transport);
        using var _s = session;

        Assert.True(session.Capabilities.HasFlag(SessionCapabilities.ReadVoiceState));
        Assert.True(session.Capabilities.HasFlag(SessionCapabilities.SpeakingEvents));
        Assert.False(session.Capabilities.HasFlag(SessionCapabilities.SetVoiceState));
        // SELECT_VOICE_CHANNEL has no granular scope; only full rpc unlocks it.
        Assert.False(session.Capabilities.HasFlag(SessionCapabilities.ChannelNavigation));
    }

    [Fact]
    public async Task Capabilities_AreFullWhenTheRpcScopeIsGranted()
    {
        var (session, _) = await ConnectedAsync();
        using var _s = session;

        Assert.Equal(SessionCapabilities.Full, session.Capabilities);
    }

    [Fact]
    public async Task ScopeDenial_SurfacesAsUnauthorizedRatherThanAGenericFault()
    {
        var transport = new FakeDiscordTransport { FailEveryCommandWith = 4006 };
        using var session = new DiscordRpcSession("client-id", new FakeTokenProvider(), transport);

        var states = new List<SessionState>();
        session.StateChanged += (_, e) => states.Add(e.State);

        var ex = await Assert.ThrowsAsync<DiscordRpcException>(
            () => session.ConnectAsync(Bounded));

        Assert.True(ex.IsScopeDenial);
        Assert.Contains(SessionState.Unauthorized, states);
    }

    [Fact]
    public async Task GetCurrentVoiceChannelAsync_ParsesParticipantsAndSelfMute()
    {
        var (session, _) = await ConnectedAsync();
        using var _s = session;

        var channel = await session.GetCurrentVoiceChannelAsync(Bounded);

        Assert.NotNull(channel);
        Assert.Equal("Lounge", channel!.Name);
        Assert.Equal(2, channel.Participants.Count);

        // Nickname wins over username when present.
        Assert.Equal("Ross", channel.Participants[0].DisplayName);
        Assert.False(channel.Participants[0].IsMuted);

        // self_mute must count as muted; only voice_state.mute would miss a self-mute.
        Assert.Equal("spikenade", channel.Participants[1].DisplayName);
        Assert.True(channel.Participants[1].IsMuted);
    }

    [Fact]
    public async Task ConnectAsync_SubscribesToVoiceStateEventsNotJustSpeaking()
    {
        // Without these the participant list never notices a mute, join or leave.
        var (session, transport) = await ConnectedAsync();
        using var _s = session;

        foreach (var evt in new[]
                 {
                     "SPEAKING_START", "SPEAKING_STOP",
                     "VOICE_STATE_CREATE", "VOICE_STATE_UPDATE", "VOICE_STATE_DELETE",
                 })
        {
            Assert.Contains(transport.Sent, s => s.Contains("\"SUBSCRIBE\"") && s.Contains(evt));
        }
    }

    [Fact]
    public async Task VoiceStateUpdate_TriggersAChannelRefresh()
    {
        var (session, transport) = await ConnectedAsync();
        using var _s = session;

        var refreshed = new TaskCompletionSource<VoiceChannelSnapshot?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        session.VoiceChannelChanged += (_, channel) => refreshed.TrySetResult(channel);

        transport.PushEvent("""{"cmd":"DISPATCH","evt":"VOICE_STATE_UPDATE","data":{"user":{"id":"1"}}}""");

        var result = await refreshed.Task.WaitAsync(Timeout);
        Assert.Equal("Lounge", result?.Name);
    }

    [Fact]
    public async Task SpeakingEvents_AreRaisedWithTheUserAndDirection()
    {
        var (session, transport) = await ConnectedAsync();
        using var _s = session;

        var speaking = new TaskCompletionSource<SpeakingEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        session.SpeakingChanged += (_, e) => speaking.TrySetResult(e);

        transport.PushEvent(
            """{"cmd":"DISPATCH","evt":"SPEAKING_START","data":{"user_id":"755304153961594881"}}""");

        var result = await speaking.Task.WaitAsync(Timeout);
        Assert.Equal("755304153961594881", result.UserId);
        Assert.True(result.IsSpeaking);
    }

    [Fact]
    public async Task GetVoiceChannelsAsync_KeepsOnlyVoiceChannels()
    {
        // GET_CHANNELS returns text channels and categories too.
        var (session, _) = await ConnectedAsync();
        using var _s = session;

        var channels = await session.GetVoiceChannelsAsync("g1", Bounded);

        Assert.Single(channels);
        Assert.Equal("Lounge", channels[0].Name);
        Assert.Equal("g1", channels[0].GuildId);
    }

    [Fact]
    public async Task GetGuildsAsync_ParsesTheGuildList()
    {
        var (session, _) = await ConnectedAsync();
        using var _s = session;

        var guilds = await session.GetGuildsAsync(Bounded);

        Assert.Single(guilds);
        Assert.Equal("XBOX", guilds[0].Name);
    }

    [Fact]
    public async Task NotInAVoiceChannel_ReportsNullRatherThanThrowing()
    {
        var transport = new FakeDiscordTransport { ChannelJson = null };
        var (session, _) = await ConnectedAsync(transport);
        using var _s = session;

        Assert.Null(await session.GetCurrentVoiceChannelAsync(Bounded));
    }

    [Fact]
    public async Task Dispose_FaultsPendingCallsInsteadOfHangingThem()
    {
        var transport = new FakeDiscordTransport();
        var session = new DiscordRpcSession("client-id", new FakeTokenProvider(), transport);
        await session.ConnectAsync(Bounded);

        session.Dispose();

        // Must fail fast rather than register a pending call nothing will ever complete.
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => session.GetVoiceSettingsAsync(Bounded));
    }
}
