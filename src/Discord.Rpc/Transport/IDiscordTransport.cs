using System;
using System.Threading;
using System.Threading.Tasks;
using Discord.Rpc.Protocol;

namespace Discord.Rpc.Transport
{
    /// <summary>
    /// Byte-level channel to a local Discord client. Abstracted so the session layer
    /// can be tested against a fake without a running Discord install.
    /// </summary>
    public interface IDiscordTransport : IDisposable
    {
        bool IsConnected { get; }

        Task ConnectAsync(CancellationToken cancellationToken);

        Task SendAsync(RpcFrame frame, CancellationToken cancellationToken);

        /// <summary>
        /// Reads exactly one frame, waiting as long as necessary.
        /// Returns null once the pipe closes cleanly.
        /// </summary>
        Task<RpcFrame?> ReceiveAsync(CancellationToken cancellationToken);
    }
}
