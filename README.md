# DiscordXboxWidget

A Discord voice widget for the Xbox Game Bar: see who's in your channel, who's talking,
and mute/deafen/hop channels without leaving the game.

## Status

| Component | State |
|---|---|
| `Discord.Rpc` — IPC transport, frame codec, session | Builds; framing verified against a live Discord client |
| `Discord.Rpc.Harness` — console test rig | Builds; `probe` mode passing |
| UWP Game Bar widget | Not started — blocked on the Visual Studio UWP workload |

## Architecture

`IDiscordSession` is the seam. It's stated in terms of what the widget needs (current
channel, participants, speaking, mute state) rather than how RPC delivers it, so a
degraded WebView2-backed implementation can be swapped in if this ever targets Store
distribution. Consumers check `SessionCapabilities` before invoking an operation;
`DiscordRpcSession` reports `Full`, a WebView2 session would report `None`.

`Discord.Rpc` targets **netstandard2.0** deliberately — UWP cannot consume net5.0+
libraries. Do not raise that target.

## The distribution constraint

The app requests two scopes: **`rpc`** and **`identify`**.

`SELECT_VOICE_CHANNEL` and `GET_GUILDS` require the full `rpc` scope and have no granular
equivalent, so channel navigation forces the broad grant. Because `rpc` already encompasses
`rpc.voice.read` and `rpc.voice.write`, requesting those too would widen the consent dialog
without granting anything extra. `identify` is what puts a `user` object in the AUTHENTICATE
response, which is how the widget identifies the local user among the participants.

Dropping to `rpc.voice.read` + `rpc.voice.write` alone is possible only by giving up channel
navigation — the case `SessionCapabilities.ChannelNavigation` isolates.

Discord restricts the `rpc` scope to the **application owner plus 50 whitelisted testers**
unless the app is approved for general RPC access, and approvals are rare. Outside the
whitelist, commands fail with code 4006 and the session reports `SessionState.Unauthorized`.

That is fine for a personal build. It is a hard blocker for a Microsoft Store release,
which is why the WebView2 fallback path is kept open at the interface level.

There is a second, smaller wart: the OAuth code-for-token exchange needs the application's
client secret, and a secret shipped inside a locally-installed widget is extractable.
`IOAuthTokenProvider` pushes that decision to the host rather than baking it into the library.

## Running the harness

Requires the Discord desktop client to be running.

```bash
dotnet run --project src/Discord.Rpc.Harness -- probe
```

`probe` needs no registered application — it sends a deliberately invalid client ID and
confirms Discord parses the frame and replies with a structured error. Expected output:

```
[ok]  connected to discord-ipc-0 in 16ms
[ok]  received Close frame, 43 bytes
      {"code":4000,"message":"Invalid Client ID"}
```

The `handshake` and `watch` modes need a real application from the
[Discord developer portal](https://discord.com/developers/applications), a redirect URI of
`http://localhost` registered on it, and `DISCORD_CLIENT_SECRET` set in the environment.

```bash
dotnet run --project src/Discord.Rpc.Harness -- watch <clientId>
```

## Next: the widget shell

Blocked until the UWP workload is installed:

```bash
"C:\Program Files (x86)\Microsoft Visual Studio\Installer\vs_installer.exe" modify --installPath "C:\Program Files\Microsoft Visual Studio\2022\Community" --add Microsoft.VisualStudio.Workload.Universal --includeRecommended --passive
```

Then a UWP XAML project referencing `Microsoft.Gaming.XboxGameBar`, with a
`windows.appExtension` of type `microsoft.gameBar` in the manifest. Two SDK APIs matter
disproportionately here:

- **`XboxGameBarWidgetActivity`** — without it Game Bar idle-shuts-down the widget mid-call.
- **`XboxGameBarHotkeyWatcher`** — push-to-talk and mute while a game holds focus.
