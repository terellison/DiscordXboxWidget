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
    private readonly string _clientId;
    private DiscordRpcSession? _session;

    /// <summary>Raised for unsolicited pushes to the widget: (eventName, jsonPayload).</summary>
    public event Action<string, string>? EventRaised;

    public BridgeHost(string clientId) => _clientId = clientId;

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        // Named pipe, not WebSocket: the bridge runs outside the AppContainer precisely so
        // it can use the transport the widget cannot.
        _session = new DiscordRpcSession(
            _clientId,
            new DpapiTokenProvider(_clientId),
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

            default:
                throw new ArgumentException($"Unknown command '{command}'.");
        }
    }

    public void Dispose()
    {
        _session?.Dispose();
        _session = null;
    }
}
