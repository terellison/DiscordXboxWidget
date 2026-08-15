# DiscordXboxWidget

A Discord voice widget for the Xbox Game Bar: see who's in your channel, who's talking,
mute/deafen, and switch channels without leaving the game.

## Status

Working in Game Bar: channel name, participant list with avatars, live speaking rings,
mute/deafen, and a server/channel picker.

| Component | State |
|---|---|
| `Discord.Rpc` — protocol, transports, session | Verified against a live client |
| `Discord.Rpc.Bridge` — full-trust host | Verified via `--selftest` |
| `Discord.Rpc.Harness` — console test rig | All modes passing |
| `DiscordWidget` — UWP Game Bar widget | Runs in Game Bar |

Not verified: `joinChannel` end to end (the self-test skips it rather than dragging you
into a channel), and the default-avatar fallback visually.

## Architecture

```
Game Bar
  └── DiscordWidget (UWP, AppContainer)
        └── AppServiceSession : IDiscordSession
              │  AppServiceConnection
              └── Discord.Rpc.Bridge (full trust, in-package)
                    └── DiscordRpcSession over \\.\pipe\discord-ipc-N
                          └── Discord desktop client
```

**Why the bridge exists.** The widget cannot reach Discord itself:

- **Named pipes are blocked.** An AppContainer can only open a pipe whose DACL grants
  `ALL APPLICATION PACKAGES`, and Discord owns that pipe. The Game Bar docs list named pipes
  as supported; [XboxGameBarSamples#44](https://github.com/microsoft/XboxGameBarSamples/issues/44)
  records that they are not.
- **The RPC WebSocket is rejected.** Discord closes it with 4001 Invalid Origin, validating
  `Origin` against the application's `rpc_origins` allowlist — which the developer portal no
  longer exposes. No header value works while that list is empty.

So a full-trust process outside the container does the RPC, and the widget talks to it over
`AppServiceConnection`. Deliberately not a loopback socket: an unauthenticated local listener
able to mute, deafen and move the user's Discord would be reachable by any process on the
machine, recreating the hole the origin allowlist exists to close.

`IDiscordSession` is the seam that made this affordable — swapping the direct RPC session for
the bridge left the ViewModel and XAML essentially untouched.

`Discord.Rpc` targets **netstandard2.0** deliberately: UWP cannot consume net5.0+. Do not
raise it.

### One contract worth knowing

`IDiscordSession.ConnectAsync` completing does **not** mean the session is usable. Over the
bridge it means only that the bridge process attached; Discord authentication happens after.
Drive off `StateChanged` reaching `Connected`. Reading `Capabilities` right after
`ConnectAsync` is what silently disabled every button once.

## Scopes

Requests **`rpc`** and **`identify`**.

`SELECT_VOICE_CHANNEL` and `GET_GUILDS` need full `rpc` and have no granular equivalent, so
channel navigation forces the broad grant. `rpc` already encompasses `rpc.voice.read`/`write`,
so requesting those too would only widen the consent dialog. `identify` is what puts a `user`
object in the `AUTHENTICATE` response, used to mark the local participant.

## No client secret

The token exchange uses **PKCE**, so there is no secret to store — deliberate, because a
secret inside a sideloaded package is extractable by anyone holding it.

Requires the **`PUBLIC_OAUTH2_CLIENT`** flag on the application (Developer Portal → OAuth2 →
Public Client). Without it the exchange fails with `invalid_client`.

The token is cached DPAPI-protected under `%LOCALAPPDATA%\DiscordXboxWidget\`.

## Distribution

Sideload only, for two independent reasons: Discord restricts the `rpc` scope to the app owner
plus 50 whitelisted testers unless approved for general RPC access, and `runFullTrust` requires
Store onboarding review. `APPX0006` at build time is Microsoft advising that a Windows
Application Packaging Project should produce real sideload packages — still to do.

## Building

Library, bridge and harness:

```bash
dotnet build
```

The widget needs full MSBuild — the dotnet CLI has no WindowsXaml targets and cannot evaluate
a UWP project. It is mapped so `Any CPU` skips it.

```bash
msbuild DiscordXboxWidget.sln -p:Configuration=Debug -p:Platform=x64
```

**In Visual Studio, set the solution platform to x64.** `Any CPU` has no Build or Deploy entry
for the widget, so F5 silently deploys nothing.

Requires the **Universal Windows Platform development** workload.

## Harness

Needs the Discord desktop client running. `probe` and `wsprobe` need no registered app.

| Mode | Purpose |
|---|---|
| `probe` | Named pipe framing |
| `wsprobe <id>` | WebSocket framing (this path is a dead end — see above) |
| `handshake <id>` | Auth; prints granted capabilities |
| `watch <id>` | Live channel, participants, speaking events |
| `toggle <id>` | `SET_VOICE_SETTINGS` round trip, restores after |
| `channels <id>` | Guilds and voice channels, raw beside parsed |
| `rawchannel <id>` | Unparsed channel payload, muted and unmuted |
| `mutediag <id>` | Every non-speaking dispatch frame around a mute |

`rawchannel` and `mutediag` are the fastest way to answer "is Discord actually sending this"
before changing any parsing code.

The bridge has its own end-to-end check that needs no packaging:

```bash
src/Discord.Rpc.Bridge/bin/Debug/net9.0-windows10.0.19041.0/win-x64/Discord.Rpc.Bridge.exe --selftest
```

## Logs

Two, because the widget's AppContainer cannot write outside its own storage:

- Bridge: `%LOCALAPPDATA%\DiscordXboxWidget\bridge.log`
- Widget: under the package's own local state. The family-name suffix is derived from
  `Publisher`, so it changes whenever that does — find it rather than hardcoding it:

```bash
Get-ChildItem "$env:LOCALAPPDATA\Packages\DiscordXboxWidget_*\LocalState\widget.log"
```

Both matter because a Game Bar widget has no console and WinRT exceptions surface with no
usable stack — "The parameter is incorrect" with no source is otherwise all you get.

## Widget notes

- Extension name is `microsoft.gameBarUIExtension`, not `microsoft.gameBar`.
- Activation is **protocol** activation (`ms-gamebarwidget:`); `OnLaunched` is never called.
- `XboxGameBarWidget` must be held for the app's lifetime, and repeat activations must not
  construct a second one — guard on `IsLaunchActivation`.
- `XboxGameBarWidgetActivity` suppresses idle shutdown during calls. Its constructor throws if
  an activity with the same id exists, which a fast leave-then-join produces; it is caught and
  logged rather than allowed to crash the widget.
- Anything thrown inside a dispatcher callback has no caller to catch it and becomes an
  app-level crash. `WidgetViewModel.Guarded` exists for that reason.
- The manifest needs the proxy/stub block outside `<Applications>` or the Game Bar private
  interfaces fail to marshal.
- The bridge's `OutputPath` is pinned: building with `Platform=x64` otherwise inserts an extra
  `x64\` segment, and the widget would package with no bridge in it and no error.

## Left to do

- Windows Application Packaging Project for real sideload packages
- No tests
- Tile art in `Assets/` and `GameBar/` is generated placeholder
