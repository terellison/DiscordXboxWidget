using System;
using System.Threading;
using System.Threading.Tasks;
using Discord.Rpc;
using Discord.Rpc.Bridge;
using Windows.ApplicationModel;
using Windows.ApplicationModel.AppService;
using Windows.Foundation.Collections;
using Windows.Foundation.Metadata;

namespace DiscordWidget
{
    /// <summary>
    /// An <see cref="IDiscordSession"/> that forwards everything to the full-trust bridge
    /// over an AppServiceConnection.
    /// </summary>
    /// <remarks>
    /// The widget cannot reach Discord itself: named pipes are unavailable inside an
    /// AppContainer, and Discord rejects the RPC WebSocket with 4001 Invalid Origin unless
    /// the application has an rpc_origins allowlist the portal no longer exposes.
    ///
    /// Because the session interface is stated in terms of what the widget needs rather
    /// than how RPC delivers it, swapping the direct session for this one leaves
    /// WidgetViewModel and the XAML untouched.
    /// </remarks>
    public sealed class AppServiceSession : IDiscordSession
    {
        // AppServiceConnection.SendMessageAsync already correlates its own response, so
        // unlike the raw RPC session this needs no nonce bookkeeping.
        private readonly TaskCompletionSource<bool> _bridgeAttached =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        private AppServiceConnection _connection;
        private int _disposed;

        public SessionState State { get; private set; } = SessionState.Disconnected;
        public SessionCapabilities Capabilities { get; private set; } = SessionCapabilities.None;
        public string CurrentUserId { get; private set; }

        public event EventHandler<SpeakingEventArgs> SpeakingChanged;
        public event EventHandler<VoiceChannelSnapshot> VoiceChannelChanged;
        public event EventHandler<SessionStateEventArgs> StateChanged;

        /// <summary>
        /// Called from App.OnBackgroundActivated when the bridge connects back to us.
        /// </summary>
        public void AttachBridge(AppServiceConnection connection)
        {
            _connection = connection;
            connection.RequestReceived += OnBridgeMessage;
            connection.ServiceClosed += (_, __) =>
                SetState(SessionState.Disconnected, "The Discord bridge stopped.");

            _bridgeAttached.TrySetResult(true);
        }

        public async Task ConnectAsync(CancellationToken cancellationToken)
        {
            SetState(SessionState.Connecting);

            if (!ApiInformation.IsApiContractPresent("Windows.ApplicationModel.FullTrustAppContract", 1, 0))
            {
                SetState(SessionState.Faulted, "Full-trust components are not available on this device.");
                return;
            }

            // Launched without parameters: the API only accepts a parameter group declared
            // in the manifest, so rather than spread the application id across the manifest
            // and here, the bridge owns it. The widget no longer talks to Discord directly.
            //
            // The bridge calls back into our app service once it starts, so the connection
            // arrives asynchronously rather than being something we can open ourselves.
            await FullTrustProcessLauncher.LaunchFullTrustProcessForCurrentAppAsync();

            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                try
                {
                    await WithCancellation(_bridgeAttached.Task, timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    SetState(SessionState.Faulted,
                        "The Discord bridge did not start. See bridge.log in the app's local data folder.");
                    return;
                }
            }

            // The bridge pushes its state as soon as it is connected to Discord, so there is
            // nothing further to do here; StateChanged drives the rest.
        }

        public async Task<VoiceChannelSnapshot> GetCurrentVoiceChannelAsync(CancellationToken cancellationToken)
        {
            var payload = await SendAsync(BridgeProtocol.CmdGetChannel, null, null, cancellationToken);
            return BridgePayloads.ReadChannel(payload);
        }

        public async Task<LocalVoiceSettings> GetVoiceSettingsAsync(CancellationToken cancellationToken)
        {
            var payload = await SendAsync(BridgeProtocol.CmdGetVoiceSettings, null, null, cancellationToken);
            return BridgePayloads.ReadVoiceSettings(payload);
        }

        public Task SetMutedAsync(bool muted, CancellationToken cancellationToken) =>
            SendAsync(BridgeProtocol.CmdSetMuted, muted, null, cancellationToken);

        public Task SetDeafenedAsync(bool deafened, CancellationToken cancellationToken) =>
            SendAsync(BridgeProtocol.CmdSetDeafened, deafened, null, cancellationToken);

        public Task JoinVoiceChannelAsync(string channelId, CancellationToken cancellationToken) =>
            SendAsync(BridgeProtocol.CmdJoinChannel, null, channelId, cancellationToken);

        public Task LeaveVoiceChannelAsync(CancellationToken cancellationToken) =>
            SendAsync(BridgeProtocol.CmdLeaveChannel, null, null, cancellationToken);

        private async Task<string> SendAsync(
            string command, bool? boolArg, string channelId, CancellationToken cancellationToken)
        {
            var connection = _connection;
            if (connection == null) throw new InvalidOperationException("The Discord bridge is not connected.");

            var message = new ValueSet { [BridgeProtocol.KeyCommand] = command };
            if (boolArg.HasValue) message[BridgeProtocol.ArgValue] = boolArg.Value;
            if (channelId != null) message[BridgeProtocol.ArgChannelId] = channelId;

            var response = await connection.SendMessageAsync(message).AsTask(cancellationToken);
            if (response.Status != AppServiceResponseStatus.Success)
                throw new InvalidOperationException($"Bridge call '{command}' failed: {response.Status}");

            var result = response.Message;
            var ok = result.TryGetValue(BridgeProtocol.KeySuccess, out var okValue) && okValue is bool b && b;
            if (!ok)
            {
                var error = result.TryGetValue(BridgeProtocol.KeyError, out var e) ? e as string : null;
                throw new InvalidOperationException(error ?? $"Bridge call '{command}' failed.");
            }

            return result.TryGetValue(BridgeProtocol.KeyPayload, out var payload) ? payload as string : "null";
        }

        /// <summary>Unsolicited pushes from the bridge: state, channel and speaking events.</summary>
        private void OnBridgeMessage(AppServiceConnection sender, AppServiceRequestReceivedEventArgs args)
        {
            var deferral = args.GetDeferral();
            try
            {
                var message = args.Request.Message;
                if (!message.TryGetValue(BridgeProtocol.KeyEvent, out var evtObj) || !(evtObj is string evt))
                    return;

                var payload = message.TryGetValue(BridgeProtocol.KeyPayload, out var p) ? p as string : null;
                if (payload == null) return;

                switch (evt)
                {
                    case BridgeProtocol.EvtState:
                        BridgePayloads.ReadState(payload, out var state, out var detail, out var caps, out var userId);
                        Capabilities = caps;
                        if (userId != null) CurrentUserId = userId;
                        SetState(state, detail);
                        break;

                    case BridgeProtocol.EvtChannel:
                        VoiceChannelChanged?.Invoke(this, BridgePayloads.ReadChannel(payload));
                        break;

                    case BridgeProtocol.EvtSpeaking:
                        SpeakingChanged?.Invoke(this, BridgePayloads.ReadSpeaking(payload));
                        break;
                }
            }
            finally
            {
                deferral.Complete();
            }
        }

        private void SetState(SessionState state, string detail = null)
        {
            if (State == state && detail == null) return;
            State = state;
            StateChanged?.Invoke(this, new SessionStateEventArgs(state, detail));
        }

        private static async Task<T> WithCancellation<T>(Task<T> task, CancellationToken cancellationToken)
        {
            var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (cancellationToken.Register(s => ((TaskCompletionSource<bool>)s).TrySetResult(true), cancelled))
            {
                if (await Task.WhenAny(task, cancelled.Task) != task)
                    throw new OperationCanceledException(cancellationToken);
            }
            return await task;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            // Disposing the connection signals ServiceClosed on the bridge, which is how it
            // learns to shut down; there is no separate stop command.
            _connection?.Dispose();
            _connection = null;
        }
    }
}
