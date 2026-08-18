using Discord.Rpc.Bridge;

namespace Discord.Rpc.Tests;

/// <summary>
/// The widget and the bridge are separately deployed binaries that only agree through these
/// payloads, so every round trip is asserted rather than assumed.
/// </summary>
public class BridgePayloadsTests
{
    private static VoiceUser User(string id, string name, string? nick = null,
        bool muted = false, bool deafened = false) =>
        new(id, name, nick, muted, deafened, DiscordCdn.AvatarUrl(id, null, "0"));

    [Fact]
    public void Channel_RoundTripsParticipantsAndFlags()
    {
        var original = new VoiceChannelSnapshot("chan1", "Lounge", "guild1", new[]
        {
            User("1", "rasengo", "Ross"),
            User("2", "spikenade", muted: true, deafened: true),
        });

        var result = BridgePayloads.ReadChannel(BridgePayloads.WriteChannel(original))!;

        Assert.Equal("chan1", result.Id);
        Assert.Equal("Lounge", result.Name);
        Assert.Equal("guild1", result.GuildId);
        Assert.Equal(2, result.Participants.Count);

        Assert.Equal("Ross", result.Participants[0].DisplayName);
        Assert.Equal("rasengo", result.Participants[0].Username);
        Assert.False(result.Participants[0].IsMuted);

        // DisplayName must fall back to username when there is no nickname.
        Assert.Equal("spikenade", result.Participants[1].DisplayName);
        Assert.True(result.Participants[1].IsMuted);
        Assert.True(result.Participants[1].IsDeafened);
    }

    [Fact]
    public void Channel_SurvivesNonBmpCharactersInNicknames()
    {
        // Emoji in nicknames are common and encode as surrogate pairs.
        var original = new VoiceChannelSnapshot("c", "Lounge", "g", new[]
        {
            User("1", "tennie1004", "Tennieeeeee\U0001F496✨"),
        });

        var result = BridgePayloads.ReadChannel(BridgePayloads.WriteChannel(original))!;

        Assert.Equal("Tennieeeeee\U0001F496✨", result.Participants[0].DisplayName);
    }

    [Fact]
    public void Channel_RoundTripsNullAsNotInVoice()
    {
        Assert.Null(BridgePayloads.ReadChannel(BridgePayloads.WriteChannel(null)));
    }

    [Fact]
    public void Channel_MissingAvatarFallsBackRatherThanReturningNull()
    {
        // An older bridge would omit the field; a null Uri source would break the row.
        const string json = """
            {"id":"c","name":"Lounge","participants":[{"id":"5","username":"u","muted":false,"deafened":false}]}
            """;

        var result = BridgePayloads.ReadChannel(json)!;

        Assert.False(string.IsNullOrEmpty(result.Participants[0].AvatarUrl));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void VoiceSettings_RoundTrips(bool muted, bool deafened)
    {
        var result = BridgePayloads.ReadVoiceSettings(
            BridgePayloads.WriteVoiceSettings(new LocalVoiceSettings(muted, deafened)));

        Assert.Equal(muted, result.IsMuted);
        Assert.Equal(deafened, result.IsDeafened);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Speaking_RoundTrips(bool speaking)
    {
        var result = BridgePayloads.ReadSpeaking(BridgePayloads.WriteSpeaking("299410719856787459", speaking));

        Assert.Equal("299410719856787459", result.UserId);
        Assert.Equal(speaking, result.IsSpeaking);
    }

    [Fact]
    public void State_RoundTripsCapabilitiesAndUser()
    {
        var json = BridgePayloads.WriteState(
            SessionState.Connected, null, SessionCapabilities.Full, "299410719856787459");

        BridgePayloads.ReadState(json, out var state, out var detail, out var caps, out var userId);

        Assert.Equal(SessionState.Connected, state);
        Assert.Null(detail);
        Assert.Equal(SessionCapabilities.Full, caps);
        Assert.Equal("299410719856787459", userId);
    }

    [Fact]
    public void State_RoundTripsPartialCapabilities()
    {
        // The widget gates its buttons on these, so a lossy round trip disables controls.
        const SessionCapabilities partial =
            SessionCapabilities.ReadVoiceState | SessionCapabilities.SpeakingEvents;

        var json = BridgePayloads.WriteState(SessionState.Connected, null, partial, null);
        BridgePayloads.ReadState(json, out _, out _, out var caps, out var userId);

        Assert.Equal(partial, caps);
        Assert.Null(userId);
    }

    [Fact]
    public void State_CarriesTheDetailThatExplainsAFailure()
    {
        var json = BridgePayloads.WriteState(
            SessionState.Disconnected, "No Discord IPC pipe found (checked discord-ipc-0 through discord-ipc-9).",
            SessionCapabilities.None, null);

        BridgePayloads.ReadState(json, out var state, out var detail, out _, out _);

        Assert.Equal(SessionState.Disconnected, state);
        Assert.Equal("No Discord IPC pipe found (checked discord-ipc-0 through discord-ipc-9).", detail);
    }

    [Fact]
    public void Guilds_RoundTrip()
    {
        var result = BridgePayloads.ReadGuilds(BridgePayloads.WriteGuilds(new[]
        {
            new GuildSummary("1", "XBOX"),
            new GuildSummary("2", "rasengo's hangout"),
        }));

        Assert.Equal(2, result.Count);
        Assert.Equal("XBOX", result[0].Name);
        Assert.Equal("rasengo's hangout", result[1].Name);
    }

    [Fact]
    public void VoiceChannels_RoundTrip()
    {
        var result = BridgePayloads.ReadVoiceChannels(BridgePayloads.WriteVoiceChannels(new[]
        {
            new VoiceChannelSummary("c1", "Lounge", "g1"),
        }));

        Assert.Single(result);
        Assert.Equal("Lounge", result[0].Name);
        Assert.Equal("g1", result[0].GuildId);
    }

    [Fact]
    public void EmptyCollections_RoundTripAsEmptyNotNull()
    {
        Assert.Empty(BridgePayloads.ReadGuilds(BridgePayloads.WriteGuilds(Array.Empty<GuildSummary>())));
        Assert.Empty(BridgePayloads.ReadVoiceChannels(
            BridgePayloads.WriteVoiceChannels(Array.Empty<VoiceChannelSummary>())));
    }
}
