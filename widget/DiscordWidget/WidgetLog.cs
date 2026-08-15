using System;
using System.IO;
using Windows.Storage;

namespace DiscordWidget
{
    /// <summary>
    /// Appends diagnostics to widget.log in the app's local state folder.
    /// </summary>
    /// <remarks>
    /// A Game Bar widget has no console, and exceptions crossing a WinRT boundary arrive
    /// with the stack already unwound — the debugger shows only "The parameter is incorrect"
    /// with no source. Writing the full exception here is the only way such a failure is
    /// identifiable after the fact.
    ///
    /// Cannot share the bridge's log: the widget runs in an AppContainer and can only write
    /// inside its own local state.
    /// </remarks>
    public static class WidgetLog
    {
        public static void Write(string message)
        {
            try
            {
                var path = Path.Combine(ApplicationData.Current.LocalFolder.Path, "widget.log");
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
            catch
            {
                // Logging must never be the reason the widget fails.
            }
        }

        public static void Write(string context, Exception ex) =>
            Write($"{context}: {ex.GetType().FullName} 0x{ex.HResult:X8} {ex.Message}{Environment.NewLine}{ex.StackTrace}");
    }
}
