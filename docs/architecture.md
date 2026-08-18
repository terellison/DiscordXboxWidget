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

Both halves ship in one msix, built by the packaging project:

```
DiscordXboxWidget.msix
  ├── DiscordWidget.exe            the AppContainer half (entry point)
  ├── AppxManifest.xml             identity, Game Bar widgets, app service, capabilities
  └── Discord.Rpc.Bridge\          the full-trust half, self-contained .NET
```

| Project | Target | Role |
|---|---|---|
| `Discord.Rpc` | netstandard2.0 | Protocol, named-pipe transport, session, bridge payloads |
| `Discord.Rpc.Bridge` | net9.0-windows | Full-trust host that actually talks to Discord |
| `Discord.Rpc.Harness` | net9.0 | Console rig for probing Discord directly |
| `DiscordWidget` | UWP | The Game Bar widget itself |
| `DiscordXboxWidget` | wapproj | Packaging project: combines the two halves into the msix |

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
`https://discord.com` and `https://localhost`. The WebSocket transport that established this
has since been deleted — the finding is recorded here, not kept alive as unused code.

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

> **A team-owned application grants nobody `rpc` implicitly.** A personally-owned
> application grants it to its owner with no tester entry; a team-owned one refuses every
> account — team members included — with `invalid_scope` until that account is added to the
> application's tester list. Neither half of that asymmetry is documented. A Team is
> otherwise fine, so verification and RPC access can be held together; see
> [the roadmap](roadmap.md#the-ownership-asymmetry-and-a-correction) for the measurements.

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

## Packaging

The msix is built by `packaging/DiscordXboxWidget`, a Windows Application Packaging Project
that references the widget and the bridge. Before it existed the widget project produced the
package itself and copied the bridge in through a glob, which worked but emitted `APPX0006`:
a project declaring `runFullTrust` is expected to be packaged this way.

Three consequences worth knowing, because none of them are obvious from the project file:

**There are two manifests, and only one of them ships.** `packaging/DiscordXboxWidget/Package.appxmanifest`
is the real one. `widget/DiscordWidget/Package.appxmanifest` exists only because a UWP
project cannot build without one, and is deliberately minimal — a package feature added
there is silently absent from the product. Its `Identity` is intentionally different so that
deploying the widget project alone cannot replace a real install with a bridge-less copy.

**The bridge's path inside the package is derived, not chosen.** A referenced .NET project is
published into a folder named after the project, so the bridge lives at
`Discord.Rpc.Bridge\Discord.Rpc.Bridge.exe` and the `windows.fullTrustProcess` extension has
to match. Renaming the bridge project moves it, and the failure is a widget that installs
perfectly and never connects. CI asserts the declared path resolves to a file in the package.

**The bridge is published self-contained**, because the packaging project imposes that on
`.NETCoreApp` references. That is the right answer here anyway: nothing in a sideloaded
install can resolve a missing shared runtime, and a bridge that cannot start looks exactly
like a bridge that cannot reach Discord. It costs roughly 33 MB of package size.

Package identity — `Name` and `Publisher` — is unchanged from when the widget project built
the package. That is what keeps an install an upgrade rather than a second, unrelated app,
and keeps `widget.log` where it was.

## Distribution

Sideload only. The reason is Discord's `rpc` scope restriction, above — not anything about
the packaging.

Packages are signed with a self-signed certificate whose subject must match the manifest
`Publisher` exactly, as Windows renders it. Generate the certificate first and copy its
rendered subject into the manifest, not the other way round — Windows canonicalises
distinguished names, and a mismatch produces a package that builds and signs but will not
install.

An unpackaged registration blocks a packaged install of the same identity:
`Add-AppxPackage` fails with `0x80073CFB`, *"the current user has already installed an
unpackaged version of this app"*. Anyone who has deployed the widget project from Visual
Studio has to `Remove-AppxPackage` before a release will install over it.

### Microsoft Store

Not viable, and the obstacle is not the one it first appears to be.

**`runFullTrust` is not the blocker.** It is a restricted capability and needs justifying at
submission, but launching a full-trust companion process is the standard Desktop Bridge
pattern and is routinely approved.

**The `rpc` scope is the blocker.** It is limited to the application owner plus a 50-slot
tester allowlist. A Store customer authorizing a shipped application id is neither, so
`AUTHORIZE` refuses them — the `4006 / not authorized` row in the README's troubleshooting
table. A Store build with an id compiled in would work perfectly for the developer and fail
for every single customer, which is the worst shape a defect can take: it passes local
testing by construction.

Injecting the id from a CI secret does not change this. It also does not make the id secret
— it moves it from the repository into the shipped binary, where `strings` recovers it. The
same reasoning already applied to the client secret applies here.

**Reserving a Store name reassigns package identity.** Partner Center issues an
`Identity/Name` such as `12345Publisher.AppName` and a `Publisher` of `CN=<GUID>`, and
associating the project rewrites the manifest with them. That changes the
PackageFamilyName, so a Store build and a sideload build are two unrelated apps: both can be
installed at once, and Game Bar lists both widgets. If this is ever revisited, the Store
identity belongs in a separate configuration rather than replacing the identity above.
`Properties/DisplayName` must also match the reserved name exactly.

**Naming.** A name that leads with someone else's trademark — *Xbox*, *Discord* — invites
rejection and contradicts [the affiliation disclaimer](terms-of-service.md). A trailing
descriptive "for X" is the conventional construction.

What would unblock the Store is Discord approving general RPC access, which lifts the
50-tester ceiling. The documentation says approval exists and describes no way to request
it. Verification is a separate programme and would not help: it requires a Team, which is
[compatible with RPC](roadmap.md#the-ownership-asymmetry-and-a-correction) but does nothing
about the ceiling.
