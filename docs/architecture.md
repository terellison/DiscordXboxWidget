# Architecture

```
Game Bar
  └── DiscordWidget (UWP, AppContainer)
        └── AppServiceSession : IDiscordSession
              │  AppServiceConnection
              └── Discord.Rpc.Bridge (full trust, in-package)
                    └── DiscordRpcSession over \\.\pipe\discord-ipc-N
                          └── Discord desktop client
```

| Project | Target | Role |
|---|---|---|
| `Discord.Rpc` | netstandard2.0 | Protocol, transports, session, bridge payloads |
| `Discord.Rpc.Bridge` | net9.0-windows | Full-trust host that actually talks to Discord |
| `Discord.Rpc.Harness` | net9.0 | Console rig for probing Discord directly |
| `DiscordWidget` | UWP | The Game Bar widget itself |

## Why there is a bridge at all

The widget cannot reach Discord from inside its AppContainer. Both available routes are
closed, and neither is closed in a way the documentation admits.

**Named pipes are blocked.** An AppContainer can only open a pipe whose DACL grants
`ALL APPLICATION PACKAGES`. Discord owns `\\.\pipe\discord-ipc-N`, so that ACL cannot be
changed from outside, and `Connect()` fails with *"This functionality is not supported in the
context of an app container"*. The Game Bar documentation lists named pipes as a supported
IPC option; [XboxGameBarSamples#44](https://github.com/microsoft/XboxGameBarSamples/issues/44)
records that they are not, and is still open.

**The RPC WebSocket is rejected.** Discord also listens on `ws://127.0.0.1:6463-6472`, which
an AppContainer *can* reach given a loopback exemption. It closes the connection with
**4001 Invalid Origin**, validating the `Origin` header against the application's
`rpc_origins` allowlist — a field the developer portal no longer exposes. With that list
empty no header value succeeds, including sending none. Verified against no `Origin`,
`https://discord.com` and `https://localhost`.

So a full-trust process outside the container performs the RPC, and the widget talks to it
over `AppServiceConnection`.

### Why AppService and not a local socket

The widget demonstrably *can* open a loopback socket from inside its container — that is how
the 4001 was observed. A small local server in the bridge would have been less work than the
AppService plumbing.

It was rejected deliberately. An unauthenticated listener able to mute, deafen and move the
user's Discord would be reachable by any process on the machine, including sandboxed browser
content. That is precisely the hole Discord's origin allowlist exists to close, so building a
proxy around it after being blocked by it would be the wrong answer. AppService is scoped to
the package.

It also means no loopback exemption is required at install time.

## The IDiscordSession seam

`IDiscordSession` is stated in terms of what the widget needs — current channel, participants,
speaking, mute state — rather than how RPC delivers it. That is what made the bridge
affordable: swapping the direct RPC session for an out-of-process one left the ViewModel and
the XAML essentially untouched, one constructor argument aside.

`SessionCapabilities` is derived from the scopes `AUTHENTICATE` actually granted, never from
the ones requested. A cached token can predate a scope change, and trusting the request would
leave the UI offering controls the token cannot drive.

### A contract worth knowing

**`ConnectAsync` completing does not mean the session is usable.** Over the bridge it means
only that the bridge process attached; Discord authentication happens afterwards. Drive off
`StateChanged` reaching `Connected`.

Reading `Capabilities` immediately after `ConnectAsync` is what once left every button in the
widget permanently disabled — correct against the direct session, wrong against the bridge,
and silent in both.

### netstandard2.0 is deliberate

`Discord.Rpc` targets netstandard2.0 because UWP cannot consume net5.0+ libraries. Do not
raise it. The bridge and harness are free to target current .NET because neither is loaded
into the widget's process.

## Discord integration

### Scopes

Requests **`rpc`** and **`identify`**.

`SELECT_VOICE_CHANNEL` and `GET_GUILDS` require full `rpc` and have no granular equivalent, so
channel switching forces the broad grant. Since `rpc` already encompasses `rpc.voice.read` and
`rpc.voice.write`, requesting those as well would only widen the consent dialog without
granting anything. `identify` is what puts a `user` object in the `AUTHENTICATE` response,
which is how the local participant is identified in the list.

Dropping to `rpc.voice.read` + `rpc.voice.write` alone is possible only by giving up channel
navigation — the case `SessionCapabilities.ChannelNavigation` isolates.

> **The application must be personally owned, not team-owned.** A team-owned application is
> refused the `rpc` scope outright: `AUTHORIZE` returns `invalid_scope`, while `identify`
> from the same account on the same application succeeds. Since app verification requires a
> Team, verification and RPC access cannot both be had. This is undocumented; see
> [the roadmap](roadmap.md#settled-users-supply-their-own-application-id) for the measurements.

### No client secret

The token exchange uses **PKCE**. `AUTHORIZE` carries a `code_challenge` and the exchange
carries the matching `code_verifier`; Discord requires `client_secret` only when the verifier
is absent.

This is deliberate rather than convenient. A secret inside a sideloaded package is
extractable by anyone holding the package, so there is no safe place to put one — the design
removes the requirement instead of hiding it.

It requires the **`PUBLIC_OAUTH2_CLIENT`** flag on the application (Developer Portal → OAuth2
→ Public Client). Without it the exchange fails with `invalid_client`.

Tokens are cached DPAPI-protected under `%LOCALAPPDATA%\DiscordXboxWidget\`. DPAPI rather
than `PasswordVault` because the latter requires package identity, which the bridge only has
when Game Bar launches it — a store that worked in one of the two modes would mean the tested
path was not the shipped path.

### Which application id is used

Solely `clientId` in `%LOCALAPPDATA%\DiscordXboxWidget\config.json`. Nothing is compiled in,
for two independent reasons that happen to agree:

- Discord's [Developer Terms](https://support-dev.discord.com/hc/en-us/articles/8562894815383)
  §2(d) name the Application ID as a developer credential and state that developer
  credentials **may not be embedded in open source projects**. A built-in default would
  breach that regardless of whether it functioned.
- It would mostly not function anyway. The `rpc` scope is restricted to an application's
  owner plus a 50-slot tester allowlist unless approved for general RPC access, and Discord
  publishes no way to request that approval.

The bridge writes a template on first run and reports the reason through the AppService, so
an unconfigured install explains itself in the widget rather than failing silently.

## Distribution

Sideload only, for two independent reasons:

- Discord's `rpc` scope restriction, above
- `runFullTrust` is a restricted capability requiring Microsoft Store onboarding review

`APPX0006` at build time is Microsoft advising that a Windows Application Packaging Project
should produce real sideload packages. See [the roadmap](roadmap.md).

Packages are signed with a self-signed certificate whose subject must match the manifest
`Publisher` exactly, as Windows renders it. Generate the certificate first and copy its
rendered subject into the manifest, not the other way round — Windows canonicalises
distinguished names, and a mismatch produces a package that builds and signs but will not
install.
