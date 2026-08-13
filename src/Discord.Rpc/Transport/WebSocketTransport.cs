using System;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Discord.Rpc.Protocol;

namespace Discord.Rpc.Transport
{
    /// <summary>
    /// Connects to a local Discord client over its RPC WebSocket listener.
    /// </summary>
    /// <remarks>
    /// This is the transport the UWP widget must use: an AppContainer cannot open Discord's
    /// named pipe (see <see cref="NamedPipeTransport"/>).
    ///
    /// The WebSocket protocol differs from IPC in two ways that this class hides from the
    /// session layer: the client ID travels in the query string instead of a HANDSHAKE
    /// frame, and messages are bare JSON with no 8-byte length header.
    ///
    /// Loopback is blocked for packaged apps by default. A sideloaded build needs an
    /// exemption before this will connect at all:
    ///     CheckNetIsolation.exe LoopbackExempt -a -n=&lt;PackageFamilyName&gt;
    /// The exemption is not available to Store-distributed apps.
    /// </remarks>
    public sealed class WebSocketTransport : IDiscordTransport
    {
        // Discord binds the first free port in this range, so a client that started after
        // another app grabbed 6463 will be somewhere further up.
        private const int FirstPort = 6463;
        private const int LastPort = 6472;

        private readonly string _clientId;
        private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);
        private ClientWebSocket? _socket;
        private bool _disposed;

        public int ConnectedPort { get; private set; } = -1;

        public bool IsConnected => _socket?.State == WebSocketState.Open;

        /// <summary>The query string carries the client ID, so no handshake frame is sent.</summary>
        public bool RequiresHandshakeFrame => false;

        public WebSocketTransport(string clientId)
        {
            if (string.IsNullOrWhiteSpace(clientId))
                throw new ArgumentException("A Discord application client ID is required.", nameof(clientId));

            _clientId = clientId;
        }

        public async Task ConnectAsync(CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            if (IsConnected) return;

            Exception? last = null;

            for (var port = FirstPort; port <= LastPort; port++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var socket = new ClientWebSocket();

                // Discord rejects RPC sockets whose Origin is not an allowed web origin.
                // Native clients are expected to omit it entirely, which ClientWebSocket
                // does by default -- do not set one here.
                var uri = new Uri($"ws://127.0.0.1:{port}/?v=1&client_id={Uri.EscapeDataString(_clientId)}&encoding=json");

                try
                {
                    await socket.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);
                    _socket = socket;
                    ConnectedPort = port;
                    return;
                }
                catch (OperationCanceledException)
                {
                    socket.Dispose();
                    throw;
                }
                catch (Exception ex)
                {
                    last = ex;
                    socket.Dispose();
                }
            }

            throw new IOException(
                $"No Discord RPC WebSocket found on ports {FirstPort}-{LastPort}. " +
                "Is the Discord desktop client running, and does this package have a loopback exemption? " +
                $"Last error: {last?.Message}",
                last);
        }

        public async Task SendAsync(RpcFrame frame, CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            var socket = _socket ?? throw new InvalidOperationException("Transport is not connected.");

            // The opcode is an IPC-framing concept with no equivalent on the wire here;
            // only the JSON payload is sent.
            var bytes = Encoding.UTF8.GetBytes(frame.Payload);

            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await socket.SendAsync(
                    new ArraySegment<byte>(bytes), WebSocketMessageType.Text, endOfMessage: true, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        public async Task<RpcFrame?> ReceiveAsync(CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            var socket = _socket ?? throw new InvalidOperationException("Transport is not connected.");

            var buffer = new byte[8192];
            var message = new MemoryStream();

            while (true)
            {
                WebSocketReceiveResult result;
                try
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (WebSocketException) when (socket.State != WebSocketState.Open)
                {
                    return null;
                }

                if (result.MessageType == WebSocketMessageType.Close)
                    return new RpcFrame(RpcOpcode.Close, socket.CloseStatusDescription ?? string.Empty);

                message.Write(buffer, 0, result.Count);

                if (message.Length > RpcFrame.MaxPayloadSize)
                    throw new InvalidOperationException(
                        $"Message exceeded the {RpcFrame.MaxPayloadSize} byte limit.");

                // A single logical message can arrive across several frames.
                if (result.EndOfMessage) break;
            }

            var json = Encoding.UTF8.GetString(message.ToArray(), 0, (int)message.Length);
            return new RpcFrame(RpcOpcode.Frame, json);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(WebSocketTransport));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _socket?.Dispose();
            _socket = null;
            ConnectedPort = -1;
            _writeLock.Dispose();
        }
    }
}
