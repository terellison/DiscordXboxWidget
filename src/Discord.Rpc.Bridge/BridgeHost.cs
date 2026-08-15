using System.Text.Json;
using Discord.Rpc.Transport;

namespace Discord.Rpc.Bridge;

/// <summary>
/// The bridge's actual work: owns a <see cref="DiscordRpcSession"/> over the named pipe and
/// translates widget commands into session calls.
/// </summary>
/// <remarks>
/// Knows nothing about AppService. That keeps the whole command surface runnable from a
/// console self-test, so the Discord-facing half can be verified before any packaging or
/// AppService handshake exists to confuse the diagnosis.
/// </remarks>
internal sealed class BridgeHost : IDisposable
{
    private string? _clientId;
    private DiscordRpcSession? _session;

    /// <summary>Raised for unsolicited pushes to the widget: (eventName, jsonPayload).</summary>
    public event Action<string, string>? EventRaised;

    public BridgeHost(string? clientId) => _clientId = clientId;

    /// <summary>
    /// Connects, or reports why it cannot. Unlike throwing, this leaves the bridge serving
    /// the AppService, which matters because the settings widget has to be able to supply
    /// the missing application id through that same channel.
    /// </summary>
    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_clientId))
        {
            EventRaised?.Invoke(
                BridgeProtocol.EvtState,
                BridgePayloads.WriteState(
                    SessionState.Faulted, BridgeConfig.NotConfiguredMessage, SessionCapabilities.None, null));
            return;
        }

        await ConnectSessionAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ConnectSessionAsync(CancellationToken cancellationToken)
    {
        // Named pipe, not WebSocket: the bridge runs outside the AppContainer precisely so
        // it can use the transport the widget cannot.
        _session = new DiscordRpcSession(
            _clientId!,
            new DpapiTokenProvider(_clientId!),
            new NamedPipeTransport());

        _session.StateChanged += (_, e) => PushState(e.State, e.Detail);
        _session.VoiceChannelChanged += (_, channel) =>
            EventRaised?.Invoke(BridgeProtocol.EvtChannel, BridgePayloads.WriteChannel(channel));
        _session.SpeakingChanged += (_, e) =>
            EventRaised?.Invoke(BridgeProtocol.EvtSpeaking, BridgePayloads.WriteSpeaking(e.UserId, e.IsSpeaking));

        await _session.ConnectAsync(cancellationToken).ConfigureAwait(false);

        // Re-push after connect so a widget that attached late still learns the granted
        // capabilities and the local user id, which only exist post-AUTHENTICATE.
        PushState(_session.State, null);
    }

    private void PushState(SessionState state, string? detail) =>
        EventRaised?.Invoke(
            BridgeProtocol.EvtState,
            BridgePayloads.WriteState(state, detail, _session?.Capabilities ?? SessionCapabilities.None, _session?.CurrentUserId));

    /// <summary>
    /// Executes one widget command. Returns the JSON payload, or throws; the caller maps
    /// the exception onto the error field of the response.
    /// </summary>
    public async Task<string> ExecuteAsync(string command, string? stringArg, bool boolArg, CancellationToken cancellationToken)
    {
        // Configuration commands are served without a session on purpose: when nothing is
        // configured there is no session, and these are precisely the commands needed to
        // fix that.
        switch (command)
        {
            case BridgeProtocol.CmdGetConfig:
                return JsonSerializer.Serialize(new { clientId = _clientId ?? string.Empty });

            case BridgeProtocol.CmdSetConfig:
                if (string.IsNullOrWhiteSpace(stringArg))
                    throw new ArgumentException("setConfig requires a clientId.");
                BridgeConfig.Save(stringArg!);
                _clientId = stringArg!.Trim();
                Program.Log($"application id set to {_clientId}");
                await ReconnectAsync(cancellationToken).ConfigureAwait(false);
                return "null";

            case BridgeProtocol.CmdReconnect:
                await ReconnectAsync(cancellationToken).ConfigureAwait(false);
                return "null";
        }

        var session = _session ?? throw new InvalidOperationException("Bridge is not connected to Discord.");

        switch (command)
        {
            case BridgeProtocol.CmdGetChannel:
                return BridgePayloads.WriteChannel(
                    await session.GetCurrentVoiceChannelAsync(cancellationToken).ConfigureAwait(false));

            case BridgeProtocol.CmdGetVoiceSettings:
                return BridgePayloads.WriteVoiceSettings(
                    await session.GetVoiceSettingsAsync(cancellationToken).ConfigureAwait(false));

            case BridgeProtocol.CmdSetMuted:
                await session.SetMutedAsync(boolArg, cancellationToken).ConfigureAwait(false);
                return "null";

            case BridgeProtocol.CmdSetDeafened:
                await session.SetDeafenedAsync(boolArg, cancellationToken).ConfigureAwait(false);
                return "null";

            case BridgeProtocol.CmdJoinChannel:
                if (string.IsNullOrEmpty(stringArg))
                    throw new ArgumentException("joinChannel requires a channelId.");
                await session.JoinVoiceChannelAsync(stringArg!, cancellationToken).ConfigureAwait(false);
                return "null";

            case BridgeProtocol.CmdLeaveChannel:
                await session.LeaveVoiceChannelAsync(cancellationToken).ConfigureAwait(false);
                return "null";

            case BridgeProtocol.CmdGetGuilds:
                return BridgePayloads.WriteGuilds(
                    await session.GetGuildsAsync(cancellationToken).ConfigureAwait(false));

            case BridgeProtocol.CmdGetVoiceChannels:
                if (string.IsNullOrEmpty(stringArg))
                    throw new ArgumentException("getVoiceChannels requires a guildId.");
                return BridgePayloads.WriteVoiceChannels(
                    await session.GetVoiceChannelsAsync(stringArg!, cancellationToken).ConfigureAwait(false));

            default:
                throw new ArgumentException($"Unknown command '{command}'.");
        }
    }

    /// <summary>
    /// Drops any existing Discord session and connects again with the current
    /// configuration. Errors are pushed as state rather than thrown so a failed reconnect
    /// leaves the bridge alive and able to try again.
    /// </summary>
    private async Task ReconnectAsync(CancellationToken cancellationToken)
    {
        _session?.Dispose();
        _session = null;

        try
        {
            await ConnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException))
        {
            Program.Log($"reconnect failed: {ex}");
            EventRaised?.Invoke(
                BridgeProtocol.EvtState,
                BridgePayloads.WriteState(SessionState.Faulted, ex.Message, SessionCapabilities.None, null));
        }
    }

    public void Dispose()
    {
        _session?.Dispose();
        _session = null;
    }
}
