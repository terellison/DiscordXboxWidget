using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Discord.Rpc.Protocol;
using Discord.Rpc.Transport;

namespace Discord.Rpc
{
    /// <summary>
    /// Implements <see cref="IDiscordSession"/> over the local Discord IPC pipe.
    /// </summary>
    /// <remarks>
    /// Requires the rpc, rpc.voice.read and rpc.voice.write OAuth2 scopes. Discord restricts
    /// these to the application owner plus its tester whitelist (50 slots) unless the app has
    /// been approved for general RPC access. Outside that whitelist, commands fail with
    /// code 4006 and <see cref="State"/> becomes <see cref="SessionState.Unauthorized"/>.
    /// </remarks>
    public sealed class DiscordRpcSession : IDiscordSession
    {
        /// <summary>
        /// SELECT_VOICE_CHANNEL and GET_GUILDS require the full "rpc" scope; there is no
        /// granular equivalent, so channel navigation forces the broad scope. Since "rpc"
        /// already encompasses rpc.voice.read and rpc.voice.write, requesting those as well
        /// would only widen the consent dialog without granting anything extra.
        ///
        /// "identify" is what makes the AUTHENTICATE response include a user object, which
        /// is how we work out which participant is the local user.
        /// </summary>
        private static readonly string[] RequiredScopes = { "rpc", "identify" };

        private readonly string _clientId;
        private readonly IDiscordTransport _transport;
        private readonly IOAuthTokenProvider _tokenProvider;

        private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending =
            new ConcurrentDictionary<string, TaskCompletionSource<JsonElement>>();

        private readonly CancellationTokenSource _shutdown = new CancellationTokenSource();
        private readonly HashSet<string> _subscribedChannels = new HashSet<string>();

        private Task? _readLoop;
        private string? _currentChannelId;
        private int _disposed;
        private int _refreshPending;
        private int _refreshDirty;

        public SessionState State { get; private set; } = SessionState.Disconnected;

        /// <summary>
        /// Derived from the scopes Discord actually granted, not from what we asked for.
        /// Stays <see cref="SessionCapabilities.None"/> until AUTHENTICATE succeeds, so a
        /// widget that gates its controls on this shows nothing actionable while
        /// unauthorized rather than offering buttons that will fail.
        /// </summary>
        public SessionCapabilities Capabilities { get; private set; } = SessionCapabilities.None;

        /// <summary>
        /// The local user's ID, used to distinguish "me" from other participants.
        /// Null unless the identify scope was granted.
        /// </summary>
        public string? CurrentUserId { get; private set; }

        /// <summary>
        /// Every dispatch payload as received, before parsing. Diagnostics only: the field
        /// shapes here were derived from documentation, and this is how they get checked
        /// against what Discord actually sends.
        /// </summary>
        public event EventHandler<string>? FrameReceived;

        public event EventHandler<SpeakingEventArgs>? SpeakingChanged;
        public event EventHandler<VoiceChannelSnapshot?>? VoiceChannelChanged;
        public event EventHandler<SessionStateEventArgs>? StateChanged;

        public DiscordRpcSession(string clientId, IOAuthTokenProvider tokenProvider, IDiscordTransport? transport = null)
        {
            if (string.IsNullOrWhiteSpace(clientId))
                throw new ArgumentException("A Discord application client ID is required.", nameof(clientId));

            _clientId = clientId;
            _tokenProvider = tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
            _transport = transport ?? new NamedPipeTransport();
        }

        public async Task ConnectAsync(CancellationToken cancellationToken)
        {
            SetState(SessionState.Connecting);

            try
            {
                await _transport.ConnectAsync(cancellationToken).ConfigureAwait(false);

                // The READY dispatch arrives as an unsolicited frame, so the read loop has to
                // be running before the handshake is sent or the response is lost.
                var ready = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
                _pending["__ready__"] = ready;

                _readLoop = Task.Run(() => ReadLoopAsync(_shutdown.Token));

                // The WebSocket transport authenticates via its query string and would be
                // disconnected for sending a handshake frame it never expects.
                if (_transport.RequiresHandshakeFrame)
                {
                    var handshake = JsonSerializer.Serialize(new Dictionary<string, object>
                    {
                        ["v"] = 1,
                        ["client_id"] = _clientId,
                    });
                    await _transport.SendAsync(new RpcFrame(RpcOpcode.Handshake, handshake), cancellationToken)
                        .ConfigureAwait(false);
                }

                await WithCancellation(ready.Task, cancellationToken).ConfigureAwait(false);

                await AuthenticateAsync(cancellationToken).ConfigureAwait(false);

                SetState(SessionState.Connected);
                await SubscribeAsync("VOICE_CHANNEL_SELECT", null, cancellationToken).ConfigureAwait(false);
                await RefreshVoiceChannelAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DiscordRpcException ex) when (ex.IsScopeDenial)
            {
                SetState(SessionState.Unauthorized, ex.Message);
                throw;
            }
            catch (Exception ex)
            {
                SetState(SessionState.Faulted, ex.Message);
                throw;
            }
        }

        private async Task AuthenticateAsync(CancellationToken cancellationToken)
        {
            var token = await _tokenProvider.TryGetCachedTokenAsync(cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrEmpty(token))
            {
                // PKCE stands in for the client secret: the challenge goes out with the
                // authorization request, the verifier comes back on the exchange.
                var pkce = PkceChallenge.Create();

                // Shows a consent dialog in the Discord client; blocks until the user responds.
                var authorize = await SendCommandAsync("AUTHORIZE", new Dictionary<string, object?>
                {
                    ["client_id"] = _clientId,
                    ["scopes"] = RequiredScopes,
                    ["code_challenge"] = pkce.Challenge,
                    ["code_challenge_method"] = pkce.Method,
                }, cancellationToken).ConfigureAwait(false);

                var code = authorize.GetProperty("code").GetString()
                           ?? throw new DiscordRpcException(0, "AUTHORIZE returned no code.");

                token = await _tokenProvider.ExchangeCodeAsync(code, pkce.Verifier, cancellationToken)
                    .ConfigureAwait(false);
            }

            var authenticated = await SendCommandAsync("AUTHENTICATE", new Dictionary<string, object?>
            {
                ["access_token"] = token!,
            }, cancellationToken).ConfigureAwait(false);

            ApplyGrantedScopes(authenticated);
        }

        /// <summary>
        /// A cached token can predate a scope change, and Discord will happily authenticate
        /// it with the older, narrower grant. Trusting <see cref="RequiredScopes"/> here
        /// would leave the widget offering controls the token cannot actually drive.
        /// </summary>
        private void ApplyGrantedScopes(JsonElement authenticated)
        {
            if (authenticated.TryGetProperty("user", out var user))
                CurrentUserId = GetString(user, "id");

            var granted = new HashSet<string>(StringComparer.Ordinal);
            if (authenticated.TryGetProperty("scopes", out var scopes) && scopes.ValueKind == JsonValueKind.Array)
            {
                foreach (var scope in scopes.EnumerateArray())
                {
                    var name = scope.GetString();
                    if (name != null) granted.Add(name);
                }
            }

            var hasRpc = granted.Contains("rpc");
            var capabilities = SessionCapabilities.None;

            if (hasRpc || granted.Contains("rpc.voice.read"))
                capabilities |= SessionCapabilities.ReadVoiceState | SessionCapabilities.SpeakingEvents;

            if (hasRpc || granted.Contains("rpc.voice.write"))
                capabilities |= SessionCapabilities.SetVoiceState;

            // SELECT_VOICE_CHANNEL has no granular scope; only full rpc unlocks it.
            if (hasRpc)
                capabilities |= SessionCapabilities.ChannelNavigation;

            Capabilities = capabilities;
        }

        public async Task<VoiceChannelSnapshot?> GetCurrentVoiceChannelAsync(CancellationToken cancellationToken)
        {
            var result = await SendCommandAsync("GET_SELECTED_VOICE_CHANNEL", null, cancellationToken)
                .ConfigureAwait(false);

            return ParseChannel(result);
        }

        public async Task<LocalVoiceSettings> GetVoiceSettingsAsync(CancellationToken cancellationToken)
        {
            var result = await SendCommandAsync("GET_VOICE_SETTINGS", null, cancellationToken).ConfigureAwait(false);

            return new LocalVoiceSettings(
                isMuted: GetBool(result, "mute"),
                isDeafened: GetBool(result, "deaf"));
        }

        public Task SetMutedAsync(bool muted, CancellationToken cancellationToken) =>
            SendCommandAsync("SET_VOICE_SETTINGS", new Dictionary<string, object?> { ["mute"] = muted }, cancellationToken);

        public Task SetDeafenedAsync(bool deafened, CancellationToken cancellationToken) =>
            SendCommandAsync("SET_VOICE_SETTINGS", new Dictionary<string, object?> { ["deaf"] = deafened }, cancellationToken);

        public async Task<IReadOnlyList<GuildSummary>> GetGuildsAsync(CancellationToken cancellationToken)
        {
            var result = await SendCommandAsync("GET_GUILDS", null, cancellationToken).ConfigureAwait(false);

            var guilds = new List<GuildSummary>();
            if (result.ValueKind == JsonValueKind.Object
                && result.TryGetProperty("guilds", out var array)
                && array.ValueKind == JsonValueKind.Array)
            {
                foreach (var guild in array.EnumerateArray())
                {
                    var id = GetString(guild, "id");
                    if (id != null) guilds.Add(new GuildSummary(id, GetString(guild, "name") ?? "Server"));
                }
            }

            return guilds;
        }

        public async Task<IReadOnlyList<VoiceChannelSummary>> GetVoiceChannelsAsync(
            string guildId, CancellationToken cancellationToken)
        {
            var result = await SendCommandAsync("GET_CHANNELS", new Dictionary<string, object?>
            {
                ["guild_id"] = guildId,
            }, cancellationToken).ConfigureAwait(false);

            var channels = new List<VoiceChannelSummary>();
            if (result.ValueKind == JsonValueKind.Object
                && result.TryGetProperty("channels", out var array)
                && array.ValueKind == JsonValueKind.Array)
            {
                foreach (var channel in array.EnumerateArray())
                {
                    var id = GetString(channel, "id");
                    if (id == null) continue;

                    // Type 2 is GUILD_VOICE. The widget can only join voice, so text and
                    // category entries are dropped here rather than in the UI.
                    if (!channel.TryGetProperty("type", out var type)
                        || type.ValueKind != JsonValueKind.Number
                        || type.GetInt32() != 2)
                    {
                        continue;
                    }

                    channels.Add(new VoiceChannelSummary(id, GetString(channel, "name") ?? "Voice", guildId));
                }
            }

            return channels;
        }

        public Task JoinVoiceChannelAsync(string channelId, CancellationToken cancellationToken) =>
            SendCommandAsync("SELECT_VOICE_CHANNEL", new Dictionary<string, object?>
            {
                ["channel_id"] = channelId,
                // Without force, Discord refuses the switch while the user is already
                // connected elsewhere instead of moving them.
                ["force"] = true,
            }, cancellationToken);

        public Task LeaveVoiceChannelAsync(CancellationToken cancellationToken) =>
            SendCommandAsync("SELECT_VOICE_CHANNEL", new Dictionary<string, object?>
            {
                ["channel_id"] = null,
            }, cancellationToken);

        private async Task RefreshVoiceChannelAsync(CancellationToken cancellationToken)
        {
            var channel = await GetCurrentVoiceChannelAsync(cancellationToken).ConfigureAwait(false);
            await ResubscribeChannelEventsAsync(channel?.Id, cancellationToken).ConfigureAwait(false);
            VoiceChannelChanged?.Invoke(this, channel);
        }

        /// <summary>
        /// Events scoped to a single voice channel.
        /// </summary>
        /// <remarks>
        /// The VOICE_STATE_* events are what keep the participant list live. Without them
        /// the snapshot only refreshes when the local user changes channel, so another
        /// person muting, joining or leaving is never noticed.
        /// </remarks>
        private static readonly string[] ChannelScopedEvents =
        {
            "SPEAKING_START",
            "SPEAKING_STOP",
            "VOICE_STATE_CREATE",
            "VOICE_STATE_UPDATE",
            "VOICE_STATE_DELETE",
        };

        /// <summary>
        /// Per-channel subscriptions have to be re-pointed every time the user moves.
        /// Leaving the old ones in place would leak events from a channel they have left.
        /// </summary>
        private async Task ResubscribeChannelEventsAsync(string? channelId, CancellationToken cancellationToken)
        {
            if (_currentChannelId == channelId) return;

            if (_currentChannelId != null && _subscribedChannels.Remove(_currentChannelId))
            {
                var args = new Dictionary<string, object?> { ["channel_id"] = _currentChannelId };
                foreach (var evt in ChannelScopedEvents)
                    await TryUnsubscribeAsync(evt, args, cancellationToken).ConfigureAwait(false);
            }

            _currentChannelId = channelId;

            if (channelId != null && _subscribedChannels.Add(channelId))
            {
                var args = new Dictionary<string, object?> { ["channel_id"] = channelId };
                foreach (var evt in ChannelScopedEvents)
                    await SubscribeAsync(evt, args, cancellationToken).ConfigureAwait(false);
            }
        }

        private Task SubscribeAsync(string evt, IDictionary<string, object?>? args, CancellationToken cancellationToken) =>
            SendCommandAsync("SUBSCRIBE", args, cancellationToken, evt);

        private async Task TryUnsubscribeAsync(string evt, IDictionary<string, object?> args, CancellationToken cancellationToken)
        {
            // Best effort: we are already moving off this channel, and a failed cleanup
            // must not block the user's channel switch.
            try
            {
                await SendCommandAsync("UNSUBSCRIBE", args, cancellationToken, evt).ConfigureAwait(false);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
            }
        }

        private async Task<JsonElement> SendCommandAsync(
            string command,
            IDictionary<string, object?>? args,
            CancellationToken cancellationToken,
            string? evt = null)
        {
            // Without this a call made after disposal registers a pending completion that
            // nothing will ever complete, and the caller waits forever rather than failing.
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(DiscordRpcSession));

            var nonce = Guid.NewGuid().ToString();
            var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[nonce] = tcs;

            try
            {
                var envelope = new Dictionary<string, object?>
                {
                    ["cmd"] = command,
                    ["nonce"] = nonce,
                };
                if (evt != null) envelope["evt"] = evt;
                if (args != null) envelope["args"] = args;

                var json = JsonSerializer.Serialize(envelope);
                await _transport.SendAsync(new RpcFrame(RpcOpcode.Frame, json), cancellationToken).ConfigureAwait(false);

                return await WithCancellation(tcs.Task, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _pending.TryRemove(nonce, out _);
            }
        }

        private async Task ReadLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var frame = await _transport.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                    if (frame == null) break;

                    switch (frame.Value.Opcode)
                    {
                        case RpcOpcode.Ping:
                            await _transport.SendAsync(new RpcFrame(RpcOpcode.Pong, frame.Value.Payload), cancellationToken)
                                .ConfigureAwait(false);
                            break;

                        case RpcOpcode.Close:
                            SetState(SessionState.Disconnected, frame.Value.Payload);
                            return;

                        case RpcOpcode.Frame:
                            DispatchFrame(frame.Value.Payload);
                            break;
                    }
                }

                SetState(SessionState.Disconnected, "Discord closed the connection.");
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown.
            }
            catch (Exception ex)
            {
                SetState(SessionState.Faulted, ex.Message);
            }
            finally
            {
                // Once the read loop stops, nothing can ever complete an outstanding
                // request. Faulting here rather than only on the exception path matters:
                // a clean close — Discord quitting, or rejecting the client id — left every
                // subsequent command waiting forever instead of failing.
                FaultAllPending(new InvalidOperationException(
                    "The Discord connection closed before this command completed."));
            }
        }

        private void DispatchFrame(string payload)
        {
            FrameReceived?.Invoke(this, payload);

            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement.Clone();

            var evt = GetString(root, "evt");
            var nonce = GetString(root, "nonce");
            var data = root.TryGetProperty("data", out var d) ? d : default;

            if (evt == "ERROR")
            {
                var code = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("code", out var c)
                    ? c.GetInt32() : 0;
                var message = data.ValueKind == JsonValueKind.Object ? GetString(data, "message") : null;
                var error = new DiscordRpcException(code, message ?? "Discord returned an error.");

                if (nonce != null && _pending.TryRemove(nonce, out var failed))
                {
                    failed.TrySetException(error);
                }
                else
                {
                    // Unsolicited error, e.g. the connection itself was rejected.
                    SetState(error.IsScopeDenial ? SessionState.Unauthorized : SessionState.Faulted, error.Message);
                    FaultAllPending(error);
                }
                return;
            }

            if (evt == "READY" && _pending.TryRemove("__ready__", out var ready))
            {
                ready.TrySetResult(data);
                return;
            }

            if (nonce != null && _pending.TryRemove(nonce, out var pending))
            {
                pending.TrySetResult(data);
                return;
            }

            switch (evt)
            {
                case "SPEAKING_START":
                case "SPEAKING_STOP":
                    var userId = GetString(data, "user_id");
                    if (userId != null)
                        SpeakingChanged?.Invoke(this, new SpeakingEventArgs(userId, evt == "SPEAKING_START"));
                    break;

                case "VOICE_CHANNEL_SELECT":
                case "VOICE_STATE_CREATE":
                case "VOICE_STATE_UPDATE":
                case "VOICE_STATE_DELETE":
                    QueueChannelRefresh();
                    break;
            }
        }

        /// <summary>
        /// Schedules a channel refresh, coalescing bursts into a single trailing fetch.
        /// </summary>
        /// <remarks>
        /// Runs off the read loop because the response to the refresh arrives on that very
        /// loop, so awaiting inline would deadlock. Coalesced because VOICE_STATE_UPDATE
        /// fires per participant per change: a room of eight unmuting together would
        /// otherwise trigger eight full channel fetches.
        /// </remarks>
        private void QueueChannelRefresh()
        {
            // Another refresh is already running; it will pick up the newer state on its
            // trailing pass rather than starting a second concurrent fetch.
            if (Interlocked.CompareExchange(ref _refreshPending, 1, 0) != 0)
            {
                Volatile.Write(ref _refreshDirty, 1);
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    do
                    {
                        Volatile.Write(ref _refreshDirty, 0);
                        await RefreshVoiceChannelAsync(_shutdown.Token).ConfigureAwait(false);
                    }
                    while (Volatile.Read(ref _refreshDirty) == 1);
                }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    SetState(SessionState.Faulted, ex.Message);
                }
                finally
                {
                    Volatile.Write(ref _refreshPending, 0);
                }
            });
        }

        private static VoiceChannelSnapshot? ParseChannel(JsonElement data)
        {
            if (data.ValueKind != JsonValueKind.Object) return null;

            var id = GetString(data, "id");
            if (id == null) return null;

            var participants = new List<VoiceUser>();
            if (data.TryGetProperty("voice_states", out var states) && states.ValueKind == JsonValueKind.Array)
            {
                foreach (var state in states.EnumerateArray())
                {
                    if (!state.TryGetProperty("user", out var user)) continue;

                    var userId = GetString(user, "id");
                    if (userId == null) continue;

                    var voice = state.TryGetProperty("voice_state", out var vs) ? vs : default;

                    participants.Add(new VoiceUser(
                        id: userId,
                        username: GetString(user, "username") ?? "unknown",
                        nickname: GetString(state, "nick"),
                        isMuted: GetBool(voice, "mute") || GetBool(voice, "self_mute"),
                        isDeafened: GetBool(voice, "deaf") || GetBool(voice, "self_deaf"),
                        avatarUrl: DiscordCdn.AvatarUrl(
                            userId, GetString(user, "avatar"), GetString(user, "discriminator"))));
                }
            }

            return new VoiceChannelSnapshot(
                id: id,
                name: GetString(data, "name") ?? "Voice",
                guildId: GetString(data, "guild_id"),
                participants: participants);
        }

        private static string? GetString(JsonElement element, string property) =>
            element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        private static bool GetBool(JsonElement element, string property) =>
            element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.True;

        private void FaultAllPending(Exception ex)
        {
            foreach (var key in new List<string>(_pending.Keys))
            {
                if (_pending.TryRemove(key, out var tcs)) tcs.TrySetException(ex);
            }
        }

        private void SetState(SessionState state, string? detail = null)
        {
            if (State == state) return;
            State = state;
            StateChanged?.Invoke(this, new SessionStateEventArgs(state, detail));
        }

        /// <summary>
        /// netstandard2.0 has no Task.WaitAsync, and an abandoned command would otherwise
        /// hang forever if Discord never answers.
        /// </summary>
        private static async Task<T> WithCancellation<T>(Task<T> task, CancellationToken cancellationToken)
        {
            var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (cancellationToken.Register(static s => ((TaskCompletionSource<bool>)s!).TrySetResult(true), cancelled))
            {
                if (await Task.WhenAny(task, cancelled.Task).ConfigureAwait(false) != task)
                    throw new OperationCanceledException(cancellationToken);
            }

            return await task.ConfigureAwait(false);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            _shutdown.Cancel();
            FaultAllPending(new ObjectDisposedException(nameof(DiscordRpcSession)));
            _transport.Dispose();
            _shutdown.Dispose();
        }
    }
}
