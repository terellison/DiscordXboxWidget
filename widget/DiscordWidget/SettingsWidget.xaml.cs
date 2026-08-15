using System;
using System.Linq;
using System.Threading;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace DiscordWidget
{
    /// <summary>
    /// Lets the user set the Discord application id without hand-editing config.json.
    /// </summary>
    /// <remarks>
    /// The widget cannot write the file itself — its AppContainer has no access to the
    /// bridge's data directory — so saving goes through the AppService and the bridge
    /// performs the write. That also lets the bridge reconnect immediately afterwards.
    /// </remarks>
    public sealed partial class SettingsWidget : Page
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

        private string _originalClientId = string.Empty;

        public SettingsWidget()
        {
            InitializeComponent();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            try
            {
                _originalClientId = await App.Session.GetClientIdAsync(Bounded());
                ClientIdBox.Text = _originalClientId;

                StatusText.Text = string.IsNullOrEmpty(_originalClientId)
                    ? "No application configured yet."
                    : string.Empty;
            }
            catch (Exception ex)
            {
                // The bridge may not have attached yet, which is not the user's problem to
                // solve — they can still type an id and save.
                WidgetLog.Write("Settings could not read current config", ex);
                StatusText.Text = "Could not read the current setting. You can still save a new one.";
            }
        }

        private void OnClientIdChanged(object sender, TextChangedEventArgs e)
        {
            var text = ClientIdBox.Text.Trim();

            // Enabled only for something that could actually be an id, so the obvious
            // mistakes are caught here rather than by Discord several seconds later.
            SaveButton.IsEnabled =
                text.Length > 0 &&
                text != _originalClientId &&
                text.All(char.IsDigit);
        }

        private async void OnSave(object sender, RoutedEventArgs e)
        {
            var clientId = ClientIdBox.Text.Trim();

            SaveButton.IsEnabled = false;
            Busy.IsActive = true;
            StatusText.Text = "Saving and connecting...";

            try
            {
                // The bridge writes the file and reconnects; a success here means Discord
                // accepted the application, not merely that the file was written.
                await App.Session.SetClientIdAsync(clientId, Bounded());

                _originalClientId = clientId;
                StatusText.Text = "Saved. Discord may ask you to authorize the application.";
            }
            catch (Exception ex)
            {
                WidgetLog.Write("Saving the application id failed", ex);
                StatusText.Text = ex.Message;
                SaveButton.IsEnabled = true;
            }
            finally
            {
                Busy.IsActive = false;
            }
        }

        /// <summary>
        /// Saving triggers a reconnect, which includes Discord's consent dialog on a new
        /// application, so this allows far longer than an ordinary command.
        /// </summary>
        private static CancellationToken Bounded() => new CancellationTokenSource(Timeout).Token;
    }
}
