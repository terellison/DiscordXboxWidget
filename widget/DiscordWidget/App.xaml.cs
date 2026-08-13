using System;
using Microsoft.Gaming.XboxGameBar;
using Windows.ApplicationModel.Activation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace DiscordWidget
{
    public sealed partial class App : Application
    {
        /// <summary>
        /// Must outlive the activation call: the object owns the private channel between
        /// this widget and Game Bar that drives focus and input transitions. Letting it
        /// go out of scope tears that channel down.
        /// </summary>
        private XboxGameBarWidget _widget;

        public App()
        {
            InitializeComponent();
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

            _widget = new XboxGameBarWidget(
                widgetArgs,
                Window.Current.CoreWindow,
                rootFrame);

            Window.Current.Closed += OnWidgetWindowClosed;

            rootFrame.Navigate(typeof(VoiceWidget), _widget);
            Window.Current.Activate();
        }

        private void OnWidgetWindowClosed(object sender, Windows.UI.Core.CoreWindowEventArgs e)
        {
            _widget = null;
            Window.Current.Closed -= OnWidgetWindowClosed;
        }

        private void OnNavigationFailed(object sender, NavigationFailedEventArgs e)
        {
            throw new InvalidOperationException($"Failed to load {e.SourcePageType.FullName}.");
        }
    }
}
