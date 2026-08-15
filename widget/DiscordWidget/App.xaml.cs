using System;
using Microsoft.Gaming.XboxGameBar;
using Windows.ApplicationModel.Activation;
using Windows.ApplicationModel.AppService;
using Windows.ApplicationModel.Background;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace DiscordWidget
{
    public sealed partial class App : Application
    {
        /// <summary>
        /// Owned by the app rather than the page: the bridge connects back through
        /// OnBackgroundActivated, which is an app-level entry point, and that can happen
        /// before or after the widget page finishes navigating.
        /// </summary>
        public static AppServiceSession Session { get; } = new AppServiceSession();

        private BackgroundTaskDeferral _appServiceDeferral;

        /// <summary>
        /// Must outlive the activation call: the object owns the private channel between
        /// this widget and Game Bar that drives focus and input transitions. Letting it
        /// go out of scope tears that channel down.
        /// </summary>
        private XboxGameBarWidget _widget;

        /// <summary>
        /// Held separately from the main widget. Game Bar activates the settings widget as
        /// its own activation with its own window, and both objects must outlive it.
        /// </summary>
        private XboxGameBarWidget _settingsWidget;

        private const string VoiceWidgetId = "DiscordVoice";
        private const string SettingsWidgetId = "DiscordVoiceSettings";

        public App()
        {
            InitializeComponent();

            // Game Bar shows a generic load failure when a widget crashes, and WinRT
            // exceptions surface with no usable stack, so record them before they are lost.
            UnhandledException += (_, e) =>
            {
                WidgetLog.Write("Unhandled exception", e.Exception);
            };
        }

        /// <summary>
        /// Game Bar launches widgets by protocol activation, not OnLaunched, so the normal
        /// UWP launch path is never taken when running inside Game Bar.
        /// </summary>
        protected override void OnActivated(IActivatedEventArgs args)
        {
            XboxGameBarWidgetActivatedEventArgs widgetArgs = null;

            if (args.Kind == ActivationKind.Protocol
                && args is IProtocolActivatedEventArgs protocolArgs
                && string.Equals(protocolArgs.Uri.Scheme, "ms-gamebarwidget", StringComparison.OrdinalIgnoreCase))
            {
                widgetArgs = args as XboxGameBarWidgetActivatedEventArgs;
            }

            if (widgetArgs == null)
            {
                // Launched outside Game Bar. There is no useful standalone experience,
                // and AppListEntry="none" means this should not normally be reachable.
                return;
            }

            // Repeat activations must reuse the existing widget object; constructing a
            // second one would replace the live channel to Game Bar.
            if (!widgetArgs.IsLaunchActivation)
            {
                return;
            }

            var rootFrame = new Frame();
            rootFrame.NavigationFailed += OnNavigationFailed;
            Window.Current.Content = rootFrame;

            // AppExtensionId distinguishes the two widgets. Game Bar should never send an id
            // we did not declare, but an unrecognised one must not silently show the wrong UI.
            switch (widgetArgs.AppExtensionId)
            {
                case VoiceWidgetId:
                    _widget = new XboxGameBarWidget(widgetArgs, Window.Current.CoreWindow, rootFrame);
                    Window.Current.Closed += OnWidgetWindowClosed;
                    rootFrame.Navigate(typeof(VoiceWidget), _widget);
                    break;

                case SettingsWidgetId:
                    _settingsWidget = new XboxGameBarWidget(widgetArgs, Window.Current.CoreWindow, rootFrame);
                    Window.Current.Closed += OnSettingsWindowClosed;
                    rootFrame.Navigate(typeof(SettingsWidget), _settingsWidget);
                    break;

                default:
                    WidgetLog.Write($"Unknown AppExtensionId '{widgetArgs.AppExtensionId}'; ignoring activation.");
                    return;
            }

            Window.Current.Activate();
        }

        /// <summary>
        /// The full-trust bridge opening its AppServiceConnection lands here, because the
        /// windows.appService extension declares no EntryPoint and is therefore in-process.
        /// </summary>
        protected override void OnBackgroundActivated(BackgroundActivatedEventArgs args)
        {
            base.OnBackgroundActivated(args);

            if (!(args.TaskInstance.TriggerDetails is AppServiceTriggerDetails details)) return;
            if (details.Name != Discord.Rpc.Bridge.BridgeProtocol.AppServiceName) return;

            // Held for as long as the bridge connection lives; completing it early tells
            // Windows the service is finished and tears the connection down.
            _appServiceDeferral = args.TaskInstance.GetDeferral();
            args.TaskInstance.Canceled += (_, __) => ReleaseAppServiceDeferral();
            details.AppServiceConnection.ServiceClosed += (_, __) => ReleaseAppServiceDeferral();

            Session.AttachBridge(details.AppServiceConnection);
        }

        private void ReleaseAppServiceDeferral()
        {
            _appServiceDeferral?.Complete();
            _appServiceDeferral = null;
        }

        private void OnWidgetWindowClosed(object sender, Windows.UI.Core.CoreWindowEventArgs e)
        {
            _widget = null;
            Window.Current.Closed -= OnWidgetWindowClosed;
        }

        private void OnSettingsWindowClosed(object sender, Windows.UI.Core.CoreWindowEventArgs e)
        {
            _settingsWidget = null;
            Window.Current.Closed -= OnSettingsWindowClosed;
        }

        private void OnNavigationFailed(object sender, NavigationFailedEventArgs e)
        {
            throw new InvalidOperationException($"Failed to load {e.SourcePageType.FullName}.");
        }
    }
}
