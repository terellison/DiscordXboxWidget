using System;
using System.Threading;
using System.Threading.Tasks;

namespace Discord.Rpc
{
    /// <summary>
    /// Capability-oriented view of a Discord connection, deliberately stated in terms of
    /// what the widget needs rather than how RPC delivers it. This is the swap point:
    /// <see cref="DiscordRpcSession"/> implements it over the local IPC pipe, and a
    /// WebView2-backed session could implement a degraded subset if the project ever
    /// targets Store distribution (where approved RPC scopes are not obtainable).
    ///
    /// Consumers must check <see cref="Capabilities"/> before invoking an operation.
    /// </summary>
    public interface IDiscordSession : IDisposable
    {
        SessionState State { get; }

        SessionCapabilities Capabilities { get; }

        Task ConnectAsync(CancellationToken cancellationToken);

        Task<VoiceChannelSnapshot?> GetCurrentVoiceChannelAsync(CancellationToken cancellationToken);

        Task<LocalVoiceSettings> GetVoiceSettingsAsync(CancellationToken cancellationToken);

        Task SetMutedAsync(bool muted, CancellationToken cancellationToken);

        Task SetDeafenedAsync(bool deafened, CancellationToken cancellationToken);

        Task JoinVoiceChannelAsync(string channelId, CancellationToken cancellationToken);

        Task LeaveVoiceChannelAsync(CancellationToken cancellationToken);

        event EventHandler<SpeakingEventArgs>? SpeakingChanged;

        event EventHandler<VoiceChannelSnapshot?>? VoiceChannelChanged;

        event EventHandler<SessionStateEventArgs>? StateChanged;
    }
}
