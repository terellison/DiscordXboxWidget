using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Discord.Rpc.Protocol;

namespace Discord.Rpc.Transport
{
    /// <summary>
    /// Connects to a local Discord client over its named pipe.
    /// Discord exposes discord-ipc-0 through discord-ipc-9; multiple clients
    /// (stable, PTB, Canary) can be running at once, each claiming the next free slot.
    /// </summary>
    /// <remarks>
    /// NOT USABLE FROM THE UWP WIDGET. An AppContainer process cannot open a named pipe
    /// whose DACL does not grant ALL APPLICATION PACKAGES, and Discord owns that pipe, so
    /// the ACL cannot be changed from our side. Connect() fails with
    /// "This functionality is not supported in the context of an app container".
    /// The Game Bar docs list named pipes as a supported IPC option, but
    /// https://github.com/microsoft/XboxGameBarSamples/issues/44 records that they are not.
    /// The widget therefore does not connect to Discord at all: the full-trust bridge does,
    /// using this transport, and the widget talks to the bridge over AppServiceConnection.
    /// </remarks>
    public sealed class NamedPipeTransport : IDiscordTransport
    {
        private const int MaxPipeIndex = 9;

        private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);
        private NamedPipeClientStream? _pipe;
        private bool _disposed;

        /// <summary>Index of the pipe we actually connected to, or -1 if not connected.</summary>
        public int ConnectedPipeIndex { get; private set; } = -1;

        public bool IsConnected => _pipe?.IsConnected == true;

        public async Task ConnectAsync(CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            if (IsConnected) return;

            for (var i = 0; i <= MaxPipeIndex; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var candidate = new NamedPipeClientStream(
                    serverName: ".",
                    pipeName: $"discord-ipc-{i}",
                    direction: PipeDirection.InOut,
                    options: PipeOptions.Asynchronous);

                try
                {
                    // Short timeout: a missing pipe should fall through to the next index
                    // quickly rather than stalling the widget's startup.
                    await candidate.ConnectAsync(500, cancellationToken).ConfigureAwait(false);

                    _pipe = candidate;
                    ConnectedPipeIndex = i;
                    return;
                }
                catch (TimeoutException)
                {
                    candidate.Dispose();
                }
                catch (IOException)
                {
                    // Pipe exists but is already claimed by another consumer.
                    candidate.Dispose();
                }
                catch
                {
                    candidate.Dispose();
                    throw;
                }
            }

            throw new IOException(
                "No Discord IPC pipe found (checked discord-ipc-0 through discord-ipc-9). Is the Discord desktop client running?");
        }

        public async Task SendAsync(RpcFrame frame, CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            var pipe = _pipe ?? throw new InvalidOperationException("Transport is not connected.");

            var bytes = frame.ToBytes();

            // Frames must not interleave on the wire; serialize concurrent senders.
            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await pipe.WriteAsync(bytes, 0, bytes.Length, cancellationToken).ConfigureAwait(false);
                await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        public async Task<RpcFrame?> ReceiveAsync(CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            var pipe = _pipe ?? throw new InvalidOperationException("Transport is not connected.");

            var header = new byte[RpcFrame.HeaderSize];
            if (!await ReadExactAsync(pipe, header, header.Length, cancellationToken).ConfigureAwait(false))
                return null;

            RpcFrame.ParseHeader(header, out var opcode, out var payloadLength);

            if (payloadLength == 0)
                return new RpcFrame(opcode, string.Empty);

            var payload = new byte[payloadLength];
            if (!await ReadExactAsync(pipe, payload, payloadLength, cancellationToken).ConfigureAwait(false))
                throw new EndOfStreamException($"Pipe closed mid-frame; expected {payloadLength} payload bytes.");

            return new RpcFrame(opcode, Encoding.UTF8.GetString(payload));
        }

        /// <summary>
        /// Fills <paramref name="count"/> bytes. A single ReadAsync can return a partial
        /// buffer, which would silently corrupt frame alignment if we trusted it.
        /// Returns false on a clean EOF at a frame boundary.
        /// </summary>
        private static async Task<bool> ReadExactAsync(
            Stream stream, byte[] buffer, int count, CancellationToken cancellationToken)
        {
            var offset = 0;
            while (offset < count)
            {
                var read = await stream
                    .ReadAsync(buffer, offset, count - offset, cancellationToken)
                    .ConfigureAwait(false);

                if (read == 0)
                {
                    if (offset == 0) return false;
                    throw new EndOfStreamException($"Pipe closed after {offset} of {count} expected bytes.");
                }

                offset += read;
            }

            return true;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(NamedPipeTransport));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _pipe?.Dispose();
            _pipe = null;
            ConnectedPipeIndex = -1;
            _writeLock.Dispose();
        }
    }
}
