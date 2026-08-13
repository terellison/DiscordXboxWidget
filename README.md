# DiscordXboxWidget

A Discord voice widget for the Xbox Game Bar: see who's in your channel, who's talking,
and mute/deafen/hop channels without leaving the game.

## Status

| Component | State |
|---|---|
| `Discord.Rpc` — transports, frame codec, session | Builds; both transports verified against a live Discord client |
| `Discord.Rpc.Harness` — console test rig | Builds; `probe` and `wsprobe` passing |
| `DiscordWidget` — UWP Game Bar widget | Builds and packages; **not yet run inside Game Bar** |

## Architecture

`IDiscordSession` is the seam. It's stated in terms of what the widget needs (current
channel, participants, speaking, mute state) rather than how RPC delivers it, so a
degraded WebView2-backed implementation can be swapped in if this ever targets Store
distribution. Consumers check `SessionCapabilities` before invoking an operation, and
those capabilities are derived from the scopes `AUTHENTICATE` actually granted — not from
the ones requested, since a cached token can predate a scope change.

`Discord.Rpc` targets **netstandard2.0** deliberately — UWP cannot consume net5.0+
libraries. Do not raise that target.

### Two transports, and why

| Transport | Used by | Why |
|---|---|---|
| `NamedPipeTransport` | Console harness | `\\.\pipe\discord-ipc-0..9`, the standard desktop RPC path |
| `WebSocketTransport` | UWP widget | `ws://127.0.0.1:6463-6472`, the only path an AppContainer can take |

**The UWP widget cannot use named pipes.** An AppContainer process can only open a pipe
whose DACL grants `ALL APPLICATION PACKAGES`, and Discord owns that pipe, so the ACL can't
be changed from our side — `Connect()` fails with *"This functionality is not supported in
the context of an app container"*. The Game Bar docs list named pipes as a supported IPC
option; [microsoft/XboxGameBarSamples#44](https://github.com/microsoft/XboxGameBarSamples/issues/44)
records that they are not, and is still open.

The WebSocket path differs on the wire — the client ID travels in the query string instead
of a `HANDSHAKE` frame, and messages are bare JSON with no length header. Both differences
are hidden behind `IDiscordTransport.RequiresHandshakeFrame`.

## Scopes

The app requests **`rpc`** and **`identify`**.

`SELECT_VOICE_CHANNEL` and `GET_GUILDS` require the full `rpc` scope and have no granular
equivalent, so channel navigation forces the broad grant. Because `rpc` already encompasses
`rpc.voice.read` and `rpc.voice.write`, requesting those too would widen the consent dialog
without granting anything extra. `identify` is what puts a `user` object in the
`AUTHENTICATE` response, which is how the widget identifies the local user.

Dropping to `rpc.voice.read` + `rpc.voice.write` alone is possible only by giving up channel
navigation — the case `SessionCapabilities.ChannelNavigation` isolates.

## The distribution constraint

Discord restricts the `rpc` scope to the **application owner plus 50 whitelisted testers**
unless the app is approved for general RPC access, and approvals are rare. Outside the
whitelist, commands fail with code 4006 and the session reports `SessionState.Unauthorized`.

That's fine for a personal build and a hard blocker for a Microsoft Store release. The
loopback exemption below points the same way: it is available to sideloaded packages and
not to Store-distributed ones. Two independent constraints, same conclusion — this is a
sideload-only design, which is why the WebView2 fallback stays open at the interface level.

## No client secret

There isn't one, by design. The token exchange uses **PKCE**: `AUTHORIZE` carries a
`code_challenge`, the exchange carries the matching `code_verifier`, and Discord requires
`client_secret` only when `code_verifier` is absent.

This matters because a secret inside a sideloaded package is extractable by anyone holding
the package — there is no safe place to put one, so the design removes the requirement
instead of hiding it.

**This requires the `PUBLIC_OAUTH2_CLIENT` flag on your Discord application.** Without it
the exchange fails with `invalid_client`.

Known Discord bug: refresh tokens still demand a secret even under PKCE
([discord-api-docs#5531](https://github.com/discord/discord-api-docs/issues/5531)). Not hit
here — the token is cached and the authorize flow re-runs on expiry rather than refreshing.

## Building

The library and harness build with the dotnet CLI:

```bash
dotnet build
```

The UWP widget needs full MSBuild — the dotnet CLI has no WindowsXaml targets and cannot
even evaluate the project. It's mapped in the solution so `Any CPU` skips it.

```bash
msbuild DiscordXboxWidget.sln -p:Configuration=Debug -p:Platform=x64
```

Requires the Visual Studio **Universal Windows Platform development** workload.

## Running the harness

Requires the Discord desktop client to be running.

```bash
dotnet run --project src/Discord.Rpc.Harness -- probe
```

`probe` (pipe) and `wsprobe <clientId>` (WebSocket) need no registered application — they
send a deliberately invalid client ID and confirm Discord parses the frame and replies with
a structured error. Expected:

```
[ok]  connected to discord-ipc-0 in 16ms
[ok]  received Close frame, 43 bytes
      {"code":4000,"message":"Invalid Client ID"}
```

`handshake` and `watch` need a real application with `http://localhost` registered as a
redirect URI and the `PUBLIC_OAUTH2_CLIENT` flag set. No secret and no environment variable.
`handshake` prints the granted capabilities, which is the real confirmation that the scopes
came through. Discord shows a consent dialog on first run; the token is then cached.

## Widget setup

Three things before the widget can connect:

1. **Client ID** — set `WidgetConfig.ClientId` in `widget/DiscordWidget/WidgetConfig.cs`.
2. **`PUBLIC_OAUTH2_CLIENT` flag** on the application, so the PKCE exchange works without a
   secret. The access token is cached in the Windows credential locker.
3. **Loopback exemption** — packaged apps are blocked from localhost by default, so the
   WebSocket transport cannot reach Discord without this:

```bash
CheckNetIsolation.exe LoopbackExempt -a -n=<PackageFamilyName>
```

Visual Studio adds the exemption automatically while debugging, so this only bites on a
sideloaded install.

## Notes on the widget

- Extension name is `microsoft.gameBarUIExtension` — not `microsoft.gameBar`.
- Game Bar activates widgets by **protocol activation** (`ms-gamebarwidget:`), so `OnLaunched`
  is never called; the work happens in `OnActivated`.
- The `XboxGameBarWidget` object must be held for the app's lifetime — it owns the private
  channel to Game Bar that drives focus and input.
- Repeat activations must **not** construct a second `XboxGameBarWidget`; guard on
  `IsLaunchActivation`.
- `XboxGameBarWidgetActivity` is opened while the user is in voice and completed when they
  leave. Without it Game Bar idle-shuts-down the widget mid-call.
- The manifest needs the proxy/stub registration block outside `<Applications>`, or the
  Game Bar private interfaces fail to marshal.

Tile art in `Assets/` and `GameBar/` is generated placeholder — flat blurple with a white
circle. Replace before any real use.

## Not done yet

- Never launched inside Game Bar. Building and packaging is not the same as working.
- `XboxGameBarHotkeyWatcher` for push-to-talk / mute while a game holds focus.
- Channel switching UI — `JoinVoiceChannelAsync` exists but nothing calls it, since
  listing available channels needs `GET_GUILDS` / `GET_CHANNELS` plumbing.
- No tests.
