using System;
using System.Collections.Generic;
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

        /// <summary>
        /// The local user's ID, used to tell "me" apart from other participants.
        /// Null unless the identify scope was granted.
        /// </summary>
        string? CurrentUserId { get; }

        /// <summary>
        /// Begins connecting.
        /// </summary>
        /// <remarks>
        /// Completing does NOT mean the session is usable, and callers must not read
        /// <see cref="Capabilities"/>, <see cref="CurrentUserId"/> or issue commands purely
        /// because it returned. The bridge implementation completes as soon as the
        /// out-of-process host attaches, with Discord authentication still to come.
        /// Wait for <see cref="StateChanged"/> to report
        /// <see cref="SessionState.Connected"/> instead; that holds for every implementation.
        /// </remarks>
        Task ConnectAsync(CancellationToken cancellationToken);

        Task<VoiceChannelSnapshot?> GetCurrentVoiceChannelAsync(CancellationToken cancellationToken);

        Task<LocalVoiceSettings> GetVoiceSettingsAsync(CancellationToken cancellationToken);

        Task SetMutedAsync(bool muted, CancellationToken cancellationToken);

        Task SetDeafenedAsync(bool deafened, CancellationToken cancellationToken);

        /// <summary>
        /// Servers the user is in. Requires <see cref="SessionCapabilities.ChannelNavigation"/>.
        /// </summary>
        Task<IReadOnlyList<GuildSummary>> GetGuildsAsync(CancellationToken cancellationToken);

        /// <summary>
        /// Voice channels within a server, text channels excluded. Requires
        /// <see cref="SessionCapabilities.ChannelNavigation"/>.
        /// </summary>
        Task<IReadOnlyList<VoiceChannelSummary>> GetVoiceChannelsAsync(string guildId, CancellationToken cancellationToken);

        Task JoinVoiceChannelAsync(string channelId, CancellationToken cancellationToken);

        Task LeaveVoiceChannelAsync(CancellationToken cancellationToken);

        event EventHandler<SpeakingEventArgs>? SpeakingChanged;

        event EventHandler<VoiceChannelSnapshot?>? VoiceChannelChanged;

        event EventHandler<SessionStateEventArgs>? StateChanged;
    }
}
