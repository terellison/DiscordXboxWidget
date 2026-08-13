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
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }

    public sealed class WidgetViewModel : INotifyPropertyChanged, IDisposable
    {
        private readonly CoreDispatcher _dispatcher;
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();

        private DiscordRpcSession _session;
        private string _channelName = "Not connected";
        private string _status = "Connecting to Discord...";
        private bool _isMuted;
        private bool _isDeafened;
        private bool _isInVoice;
        private bool _canControlVoice;
        private bool _canNavigate;

        public ObservableCollection<ParticipantViewModel> Participants { get; } =
            new ObservableCollection<ParticipantViewModel>();

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

        public WidgetViewModel(CoreDispatcher dispatcher)
        {
            _dispatcher = dispatcher;
        }

        public async Task ConnectAsync()
        {
            if (!WidgetConfig.IsConfigured)
            {
                Status = "Set WidgetConfig.ClientId to your Discord application ID.";
                return;
            }

            try
            {
                // WebSocket, not the named pipe: an AppContainer cannot open Discord's pipe.
                var transport = new WebSocketTransport(WidgetConfig.ClientId);
                _session = new DiscordRpcSession(
                    WidgetConfig.ClientId,
                    new VaultTokenProvider(WidgetConfig.ClientId),
                    transport);

                _session.StateChanged += OnStateChanged;
                _session.SpeakingChanged += OnSpeakingChanged;
                _session.VoiceChannelChanged += OnVoiceChannelChanged;

                await _session.ConnectAsync(_lifetime.Token);

                await RunOnUiAsync(() =>
                {
                    CanControlVoice = _session.Capabilities.HasFlag(SessionCapabilities.SetVoiceState);
                    CanNavigate = _session.Capabilities.HasFlag(SessionCapabilities.ChannelNavigation);
                });

                var settings = await _session.GetVoiceSettingsAsync(_lifetime.Token);
                await RunOnUiAsync(() =>
                {
                    IsMuted = settings.IsMuted;
                    IsDeafened = settings.IsDeafened;
                });
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
                switch (e.State)
                {
                    case SessionState.Connected:
                        Status = string.Empty;
                        break;
                    case SessionState.Unauthorized:
                        Status = "Not authorized for RPC on this account.";
                        break;
                    case SessionState.Disconnected:
                        Status = "Discord disconnected.";
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
                action();
                return;
            }

            await _dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => action());
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
                _session.Dispose();
                _session = null;
            }

            _lifetime.Dispose();
        }
    }
}
