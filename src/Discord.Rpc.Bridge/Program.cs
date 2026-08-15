using System.Runtime.InteropServices;

namespace Discord.Rpc.Bridge;

internal static class Program
{
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);

    private const int AttachParentProcess = -1;

    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        // There is no built-in application id, and there deliberately cannot be one:
        // Discord's Developer Terms name the Application ID as a developer credential and
        // forbid embedding credentials in open source projects. Each user supplies theirs.
        var clientId = ReadOption(args, "--client-id") ?? BridgeConfig.Load().ClientId;
        var selfTest = args.Contains("--selftest", StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(clientId) && selfTest)
        {
            AttachConsole(AttachParentProcess);
            Console.WriteLine(BridgeConfig.NotConfiguredMessage);
            return 1;
        }

        // An unconfigured bridge still runs the full AppService. It reports the problem as
        // session state, and keeps serving setConfig so the settings widget can fix it —
        // which it could not do if the bridge refused to start without configuration.
        using var cts = new CancellationTokenSource();
        using var host = new BridgeHost(clientId);

        if (selfTest)
        {
            // WinExe has no console of its own; borrow the launching shell's.
            AttachConsole(AttachParentProcess);
            Console.WriteLine($"client id {clientId}");
            return await SelfTestAsync(host, cts.Token);
        }

        try
        {
            Log($"bridge starting (clientId={clientId})");
            using var bridge = new AppServiceBridge(host);
            await bridge.RunAsync(cts.Token);
            Log("bridge exited normally");
            return 0;
        }
        catch (Exception ex)
        {
            // No console and no UI in this mode. Without a log a bridge failure is
            // indistinguishable from the widget simply hanging.
            Log($"bridge failed: {ex}");
            return 1;
        }
    }

    /// <summary>
    /// Exercises the full command surface against a live Discord client without AppService
    /// or packaging involved, so a failure here is unambiguously Discord-facing.
    /// </summary>
    private static async Task<int> SelfTestAsync(BridgeHost host, CancellationToken cancellationToken)
    {
        host.EventRaised += (evt, payload) =>
        {
            var trimmed = payload.Length > 240 ? payload[..240] + "..." : payload;
            Console.WriteLine($"[event] {evt}: {trimmed}");
        };

        try
        {
            Console.WriteLine("connecting to Discord over named pipe...");
            await host.ConnectAsync(cancellationToken);
            Console.WriteLine("[ok] connected");

            var channel = await host.ExecuteAsync(BridgeProtocol.CmdGetChannel, null, false, cancellationToken);
            Console.WriteLine($"[getChannel] {channel}");

            var settings = await host.ExecuteAsync(BridgeProtocol.CmdGetVoiceSettings, null, false, cancellationToken);
            Console.WriteLine($"[getVoiceSettings] {settings}");

            // Round-trip a write, then restore, mirroring the harness toggle test.
            var before = BridgePayloads.ReadVoiceSettings(settings);
            await host.ExecuteAsync(BridgeProtocol.CmdSetMuted, null, !before.IsMuted, cancellationToken);
            var afterJson = await host.ExecuteAsync(BridgeProtocol.CmdGetVoiceSettings, null, false, cancellationToken);
            var after = BridgePayloads.ReadVoiceSettings(afterJson);
            Console.WriteLine(after.IsMuted == !before.IsMuted
                ? "[ok] setMuted round-trip verified"
                : "[FAIL] setMuted did not take effect");
            await host.ExecuteAsync(BridgeProtocol.CmdSetMuted, null, before.IsMuted, cancellationToken);
            Console.WriteLine($"[restore] muted={before.IsMuted}");

            var guildsJson = await host.ExecuteAsync(BridgeProtocol.CmdGetGuilds, null, false, cancellationToken);
            var guilds = BridgePayloads.ReadGuilds(guildsJson);
            Console.WriteLine($"[getGuilds] {guilds.Count} server(s)");

            var current = BridgePayloads.ReadChannel(channel);

            // Read-only, so it runs whether or not the user is currently in voice.
            var probeGuild = current?.GuildId ?? (guilds.Count > 0 ? guilds[0].Id : null);
            if (probeGuild != null)
            {
                var channelsJson = await host.ExecuteAsync(
                    BridgeProtocol.CmdGetVoiceChannels, probeGuild, false, cancellationToken);
                var voiceChannels = BridgePayloads.ReadVoiceChannels(channelsJson);
                Console.WriteLine($"[getVoiceChannels] {voiceChannels.Count} voice channel(s) in server {probeGuild}");
            }

            if (current?.GuildId != null)
            {
                // Joins the channel the user is already in. That exercises
                // SELECT_VOICE_CHANNEL for real without dragging them out of a call.
                await host.ExecuteAsync(BridgeProtocol.CmdJoinChannel, current.Id, false, cancellationToken);
                var rejoined = BridgePayloads.ReadChannel(
                    await host.ExecuteAsync(BridgeProtocol.CmdGetChannel, null, false, cancellationToken));
                Console.WriteLine(rejoined?.Id == current.Id
                    ? "[ok] joinChannel round-trip verified (re-joined current channel)"
                    : $"[FAIL] joinChannel left us in {rejoined?.Name ?? "<none>"}");
            }
            else
            {
                Console.WriteLine("[skip] not in a voice channel; joinChannel not exercised");
            }

            // Config surface, which the settings widget drives. setConfig writes the same id
            // back, so this exercises the write and the reconnect without changing anything.
            var cfg = await host.ExecuteAsync(BridgeProtocol.CmdGetConfig, null, false, cancellationToken);
            Console.WriteLine($"[getConfig] {cfg}");

            using (var doc = System.Text.Json.JsonDocument.Parse(cfg))
            {
                var configuredId = doc.RootElement.GetProperty("clientId").GetString();
                if (!string.IsNullOrEmpty(configuredId))
                {
                    await host.ExecuteAsync(BridgeProtocol.CmdSetConfig, configuredId, false, cancellationToken);
                    Console.WriteLine("[ok] setConfig round-trip (same id, reconnected)");
                }
            }

            await host.ExecuteAsync(BridgeProtocol.CmdReconnect, null, false, cancellationToken);
            var reconnected = BridgePayloads.ReadVoiceSettings(
                await host.ExecuteAsync(BridgeProtocol.CmdGetVoiceSettings, null, false, cancellationToken));
            Console.WriteLine($"[ok] reconnect, session usable afterwards (muted={reconnected.IsMuted})");

            Console.WriteLine("self-test complete");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FAIL] {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static string? ReadOption(string[] args, string name)
    {
        var index = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    /// <summary>
    /// Appends to %LOCALAPPDATA%\DiscordXboxWidget\bridge.log. The packaged bridge has no
    /// console and no window, so this is the only diagnostic channel it has.
    /// </summary>
    internal static void Log(string message)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DiscordXboxWidget");
            Directory.CreateDirectory(dir);
            File.AppendAllText(
                Path.Combine(dir, "bridge.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never be the reason the bridge crashes.
        }
    }
}
