using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Discord.Rpc;
using Discord.Rpc.Transport;
using Windows.UI.Core;

namespace DiscordWidget
{
    public sealed class ParticipantViewModel : INotifyPropertyChanged
    {
        private bool _isSpeaking;

        public string Id { get; }
        public string DisplayName { get; }
        public bool IsMuted { get; }
        public bool IsDeafened { get; }
        public bool IsSelf { get; }
        public Uri AvatarUri { get; }

        public bool IsSpeaking
        {
            get => _isSpeaking;
            set
            {
                if (_isSpeaking == value) return;
                _isSpeaking = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSpeaking)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SpeakingOpacity)));
            }
        }

        /// <summary>
        /// Drives the speaking ring directly. Binding opacity avoids a converter class and
        /// keeps the indicator's layout slot stable, so rows don't shift as people talk.
        /// </summary>
        public double SpeakingOpacity => _isSpeaking ? 1.0 : 0.0;

        /// <summary>Segoe MDL2 glyph for the participant's mute/deafen state, empty if neither.</summary>
        public string StateGlyph =>
            IsDeafened ? "" :
            IsMuted ? "" :
            string.Empty;

        public Windows.UI.Text.FontWeight SelfWeight =>
            IsSelf ? Windows.UI.Text.FontWeights.SemiBold : Windows.UI.Text.FontWeights.Normal;

        public ParticipantViewModel(VoiceUser user, bool isSelf)
        {
            Id = user.Id;
            DisplayName = user.DisplayName;
            IsMuted = user.IsMuted;
            IsDeafened = user.IsDeafened;
            IsSelf = isSelf;

            // A malformed URL must not take down the whole participant list, so this
            // degrades to no image rather than throwing inside the binding.
            AvatarUri = Uri.TryCreate(user.AvatarUrl, UriKind.Absolute, out var uri) ? uri : null;
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }

    /// <summary>One row in the channel picker: either a server, or a voice channel in one.</summary>
    public sealed class PickerItem
    {
        public string Id { get; }
        public string Name { get; }
        public bool IsGuild { get; }

        public PickerItem(string id, string name, bool isGuild)
        {
            Id = id;
            Name = name;
            IsGuild = isGuild;
        }
    }

    public sealed class WidgetViewModel : INotifyPropertyChanged, IDisposable
    {
        private readonly CoreDispatcher _dispatcher;
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();

        // Typed as the interface, not the RPC session: the widget now reaches Discord
        // through the full-trust bridge, and nothing here had to change to accommodate that.
        private readonly IDiscordSession _session;
        private string _channelName = "Not connected";
        private string _status = "Connecting to Discord...";
        private bool _isMuted;
        private bool _isDeafened;
        private bool _isInVoice;
        private bool _canControlVoice;
        private bool _canNavigate;

        public ObservableCollection<ParticipantViewModel> Participants { get; } =
            new ObservableCollection<ParticipantViewModel>();

        /// <summary>Servers, or the voice channels of one, depending on picker depth.</summary>
        public ObservableCollection<PickerItem> PickerItems { get; } =
            new ObservableCollection<PickerItem>();

        private bool _isBrowsing;
        private bool _isPickerBusy;
        private string _pickerTitle = "Servers";

        public bool IsBrowsing
        {
            get => _isBrowsing;
            private set => Set(ref _isBrowsing, value);
        }

        public bool IsPickerBusy
        {
            get => _isPickerBusy;
            private set => Set(ref _isPickerBusy, value);
        }

        public string PickerTitle
        {
            get => _pickerTitle;
            private set => Set(ref _pickerTitle, value);
        }

        public string ChannelName
        {
            get => _channelName;
            private set => Set(ref _channelName, value);
        }

        public string Status
        {
            get => _status;
            private set => Set(ref _status, value);
        }

        public bool IsMuted
        {
            get => _isMuted;
            private set => Set(ref _isMuted, value);
        }

        public bool IsDeafened
        {
            get => _isDeafened;
            private set => Set(ref _isDeafened, value);
        }

        /// <summary>True while the user is actually in a voice channel.</summary>
        public bool IsInVoice
        {
            get => _isInVoice;
            private set => Set(ref _isInVoice, value);
        }

        /// <summary>Gates the mute/deafen buttons on the granted scopes, not on hope.</summary>
        public bool CanControlVoice
        {
            get => _canControlVoice;
            private set => Set(ref _canControlVoice, value);
        }

        public bool CanNavigate
        {
            get => _canNavigate;
            private set => Set(ref _canNavigate, value);
        }

        /// <summary>Raised when the user enters or leaves voice, so the page can start or
        /// stop the Game Bar activity that prevents idle shutdown.</summary>
        public event EventHandler<bool> VoicePresenceChanged;

        public WidgetViewModel(CoreDispatcher dispatcher, IDiscordSession session)
        {
            _dispatcher = dispatcher;
            _session = session;
        }

        public async Task ConnectAsync()
        {
            try
            {
                _session.StateChanged += OnStateChanged;
                _session.SpeakingChanged += OnSpeakingChanged;
                _session.VoiceChannelChanged += OnVoiceChannelChanged;

                // Deliberately reads nothing off the session afterwards. ConnectAsync
                // returning does not mean the session is usable: over the bridge it means
                // only that the bridge process attached, and Discord authentication happens
                // after that. Capabilities and voice settings are picked up from
                // StateChanged instead, which is true for both implementations.
                await _session.ConnectAsync(_lifetime.Token);
            }
            catch (DiscordRpcException ex) when (ex.IsScopeDenial)
            {
                await RunOnUiAsync(() => Status = "This Discord account is not on the app's tester list.");
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                await RunOnUiAsync(() => Status = $"Could not reach Discord: {ex.Message}");
            }
        }

        public async Task ToggleMuteAsync()
        {
            if (_session == null || !CanControlVoice) return;

            var target = !IsMuted;
            try
            {
                await _session.SetMutedAsync(target, _lifetime.Token);
                await RunOnUiAsync(() => IsMuted = target);
            }
            catch (Exception ex)
            {
                await RunOnUiAsync(() => Status = ex.Message);
            }
        }

        public async Task ToggleDeafenAsync()
        {
            if (_session == null || !CanControlVoice) return;

            var target = !IsDeafened;
            try
            {
                await _session.SetDeafenedAsync(target, _lifetime.Token);
                await RunOnUiAsync(() => IsDeafened = target);
            }
            catch (Exception ex)
            {
                await RunOnUiAsync(() => Status = ex.Message);
            }
        }

        /// <summary>Opens the picker at the server list.</summary>
        public async Task BrowseServersAsync()
        {
            if (!CanNavigate) return;

            IsBrowsing = true;
            PickerTitle = "Servers";
            await LoadPickerAsync(async ct =>
            {
                var guilds = await _session.GetGuildsAsync(ct);
                return guilds.Select(g => new PickerItem(g.Id, g.Name, isGuild: true)).ToList();
            });
        }

        /// <summary>
        /// Drills into a server, or joins a channel and closes the picker.
        /// </summary>
        public async Task SelectPickerItemAsync(PickerItem item)
        {
            if (item == null) return;

            if (item.IsGuild)
            {
                PickerTitle = item.Name;
                // Channels are fetched per server rather than up front: some accounts are in
                // dozens of servers, and preloading would be one round trip each.
                await LoadPickerAsync(async ct =>
                {
                    var channels = await _session.GetVoiceChannelsAsync(item.Id, ct);
                    return channels.Select(c => new PickerItem(c.Id, c.Name, isGuild: false)).ToList();
                });
                return;
            }

            try
            {
                await _session.JoinVoiceChannelAsync(item.Id, _lifetime.Token);
                // The resulting VOICE_STATE/CHANNEL_SELECT events refresh the participant
                // list, so the picker just needs to get out of the way.
                await RunOnUiAsync(() => IsBrowsing = false);
            }
            catch (Exception ex)
            {
                await RunOnUiAsync(() => Status = ex.Message);
            }
        }

        public void CancelBrowsing()
        {
            IsBrowsing = false;
            PickerItems.Clear();
        }

        private async Task LoadPickerAsync(Func<CancellationToken, Task<System.Collections.Generic.List<PickerItem>>> load)
        {
            await RunOnUiAsync(() =>
            {
                PickerItems.Clear();
                IsPickerBusy = true;
            });

            try
            {
                var items = await load(_lifetime.Token);
                await RunOnUiAsync(() =>
                {
                    foreach (var item in items) PickerItems.Add(item);
                });
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                await RunOnUiAsync(() => Status = ex.Message);
            }
            finally
            {
                await RunOnUiAsync(() => IsPickerBusy = false);
            }
        }

        /// <summary>
        /// Asks the bridge to drop its Discord session and connect again. Deliberately not
        /// gated on capabilities: the case where reconnecting helps most is the one where
        /// the session never connected and there are no capabilities at all.
        /// </summary>
        public async Task ReconnectAsync()
        {
            if (!(_session is AppServiceSession bridge)) return;

            await RunOnUiAsync(() =>
            {
                Status = "Reconnecting...";
                Participants.Clear();
                ChannelName = "Not connected";
            });

            try
            {
                await bridge.ReconnectAsync(_lifetime.Token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                await RunOnUiAsync(() => Status = ex.Message);
            }
        }

        public async Task LeaveAsync()
        {
            if (_session == null || !CanNavigate) return;

            try
            {
                await _session.LeaveVoiceChannelAsync(_lifetime.Token);
            }
            catch (Exception ex)
            {
                await RunOnUiAsync(() => Status = ex.Message);
            }
        }

        private void OnStateChanged(object sender, SessionStateEventArgs e)
        {
            _ = RunOnUiAsync(() =>
            {
                // Recomputed on every state change: capabilities are only known once the
                // session authenticates, which happens after ConnectAsync has returned.
                CanControlVoice = _session.Capabilities.HasFlag(SessionCapabilities.SetVoiceState);
                CanNavigate = _session.Capabilities.HasFlag(SessionCapabilities.ChannelNavigation);

                switch (e.State)
                {
                    case SessionState.Connected:
                        Status = string.Empty;
                        _ = RefreshVoiceSettingsAsync();
                        break;
                    case SessionState.Unauthorized:
                        Status = Detail("Not authorized for RPC on this account.", e.Detail);
                        break;
                    case SessionState.Disconnected:
                        // e.Detail carries Discord's close reason. Dropping it turns every
                        // distinct failure into the same useless "disconnected" message.
                        Status = Detail("Discord disconnected.", e.Detail);
                        SetVoicePresence(false);
                        break;
                    case SessionState.Faulted:
                        Status = e.Detail ?? "Connection faulted.";
                        SetVoicePresence(false);
                        break;
                }
            });
        }

        private void OnSpeakingChanged(object sender, SpeakingEventArgs e)
        {
            _ = RunOnUiAsync(() =>
            {
                var participant = Participants.FirstOrDefault(p => p.Id == e.UserId);
                if (participant != null) participant.IsSpeaking = e.IsSpeaking;
            });
        }

        private void OnVoiceChannelChanged(object sender, VoiceChannelSnapshot channel)
        {
            _ = RunOnUiAsync(() =>
            {
                Participants.Clear();

                if (channel == null)
                {
                    ChannelName = "Not in voice";
                    SetVoicePresence(false);
                    return;
                }

                ChannelName = channel.Name;
                foreach (var user in channel.Participants)
                    Participants.Add(new ParticipantViewModel(user, user.Id == _session?.CurrentUserId));

                SetVoicePresence(true);
            });
        }

        /// <summary>
        /// Pulls the real mute/deafen state once the session is authenticated, so the
        /// button labels start out matching Discord rather than defaulting to unmuted.
        /// </summary>
        private async Task RefreshVoiceSettingsAsync()
        {
            try
            {
                var settings = await _session.GetVoiceSettingsAsync(_lifetime.Token);
                await RunOnUiAsync(() =>
                {
                    IsMuted = settings.IsMuted;
                    IsDeafened = settings.IsDeafened;
                });
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                await RunOnUiAsync(() => Status = ex.Message);
            }
        }

        private static string Detail(string summary, string detail) =>
            string.IsNullOrWhiteSpace(detail) ? summary : $"{summary} {detail}";

        private void SetVoicePresence(bool inVoice)
        {
            if (IsInVoice == inVoice) return;
            IsInVoice = inVoice;
            VoicePresenceChanged?.Invoke(this, inVoice);
        }

        /// <summary>
        /// Session events arrive on the RPC read loop, never the UI thread, so every
        /// observable mutation has to be marshalled back or the binding layer throws.
        /// </summary>
        private async Task RunOnUiAsync(Action action)
        {
            if (_dispatcher.HasThreadAccess)
            {
                Guarded(action);
                return;
            }

            await _dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => Guarded(action));
        }

        /// <summary>
        /// Dispatcher callbacks have no caller to catch them, so an exception raised inside
        /// one — including from a handler subscribed to a property change — reaches the app
        /// as an unhandled crash rather than as a failed operation.
        /// </summary>
        private static void Guarded(Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                WidgetLog.Write("UI callback failed", ex);
            }
        }

        private void Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public void Dispose()
        {
            _lifetime.Cancel();

            if (_session != null)
            {
                _session.StateChanged -= OnStateChanged;
                _session.SpeakingChanged -= OnSpeakingChanged;
                _session.VoiceChannelChanged -= OnVoiceChannelChanged;
                // Not disposed here: the session is owned by App and outlives this page, so
                // that a re-navigated widget reuses the already-running bridge.
            }

            _lifetime.Dispose();
        }
    }
}
