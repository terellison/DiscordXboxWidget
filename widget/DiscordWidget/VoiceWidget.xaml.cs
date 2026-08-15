using System;
using Microsoft.Gaming.XboxGameBar;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace DiscordWidget
{
    public sealed partial class VoiceWidget : Page
    {
        private const string VoiceActivityId = "DiscordVoiceSession";

        private XboxGameBarWidget _widget;

        /// <summary>
        /// Held while the user is in a voice channel. Game Bar idle-shuts-down widgets that
        /// look inactive, which would drop the connection mid-call; an open activity is what
        /// tells it this widget is still doing something the user cares about.
        /// </summary>
        private XboxGameBarWidgetActivity _voiceActivity;

        public WidgetViewModel ViewModel { get; private set; }

        public VoiceWidget()
        {
            InitializeComponent();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            _widget = e.Parameter as XboxGameBarWidget;

            if (_widget != null)
            {
                // Game Bar owns the gear in the title bar and only tells us it was pressed;
                // opening the settings widget is our job.
                _widget.SettingsClicked += OnSettingsClicked;
            }

            ViewModel = new WidgetViewModel(Dispatcher, App.Session);
            ViewModel.VoicePresenceChanged += OnVoicePresenceChanged;
            Bindings.Update();

            await ViewModel.ConnectAsync();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);

            if (ViewModel != null)
            {
                ViewModel.VoicePresenceChanged -= OnVoicePresenceChanged;
                ViewModel.Dispose();
                ViewModel = null;
            }

            CompleteVoiceActivity();

            if (_widget != null)
            {
                _widget.SettingsClicked -= OnSettingsClicked;
                _widget = null;
            }
        }

        private async void OnSettingsClicked(XboxGameBarWidget sender, object args)
        {
            try
            {
                await sender.ActivateSettingsAsync();
            }
            catch (Exception ex)
            {
                // Raised from a Game Bar callback, so an escape here would crash the widget.
                WidgetLog.Write("Could not open the settings widget", ex);
            }
        }

        private void OnVoicePresenceChanged(object sender, bool inVoice)
        {
            if (_widget == null) return;

            if (inVoice)
            {
                if (_voiceActivity != null) return;

                try
                {
                    _voiceActivity = new XboxGameBarWidgetActivity(_widget, VoiceActivityId);
                }
                catch (Exception ex)
                {
                    // Documented to throw when an activity with this id already exists,
                    // which a fast leave-then-join can produce before Game Bar has released
                    // the previous one. The activity only suppresses idle shutdown, so
                    // losing it degrades behaviour rather than breaking the widget --
                    // letting it escape a dispatcher callback would take the whole app down.
                    _voiceActivity = null;
                    WidgetLog.Write("Could not start Game Bar voice activity", ex);
                }
            }
            else
            {
                CompleteVoiceActivity();
            }
        }

        private void CompleteVoiceActivity()
        {
            if (_voiceActivity == null) return;

            try
            {
                _voiceActivity.Complete();
            }
            catch (Exception ex)
            {
                WidgetLog.Write("Could not complete Game Bar voice activity", ex);
            }
            finally
            {
                // Cleared even on failure: retaining a dead activity would block every
                // later attempt to start a new one.
                _voiceActivity = null;
            }
        }

        private async void OnToggleMute(object sender, RoutedEventArgs e)
        {
            if (ViewModel != null) await ViewModel.ToggleMuteAsync();
        }

        private async void OnToggleDeafen(object sender, RoutedEventArgs e)
        {
            if (ViewModel != null) await ViewModel.ToggleDeafenAsync();
        }

        private async void OnLeave(object sender, RoutedEventArgs e)
        {
            if (ViewModel != null) await ViewModel.LeaveAsync();
        }

        private async void OnReload(object sender, RoutedEventArgs e)
        {
            if (ViewModel != null) await ViewModel.ReconnectAsync();
        }

        private async void OnBrowse(object sender, RoutedEventArgs e)
        {
            if (ViewModel != null) await ViewModel.BrowseServersAsync();
        }

        private void OnCancelBrowse(object sender, RoutedEventArgs e) => ViewModel?.CancelBrowsing();

        private async void OnPickerItemClick(object sender, ItemClickEventArgs e)
        {
            if (ViewModel != null && e.ClickedItem is PickerItem item)
                await ViewModel.SelectPickerItemAsync(item);
        }

        // x:Bind function bindings, used instead of converter classes.
        // Must be instance methods: the generated binding code calls them off the page.
        public Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

        public Visibility HasText(string value) =>
            string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;

        public string MuteLabel(bool muted) => muted ? "Unmute" : "Mute";

        public string DeafenLabel(bool deafened) => deafened ? "Undeafen" : "Deafen";
    }
}
