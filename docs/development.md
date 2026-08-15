# Development

## Prerequisites

- Visual Studio 2022 or later with the **Universal Windows Platform development** workload
- .NET 9 SDK
- Windows 10 SDK 10.0.18362 or later
- The Discord desktop client, for anything that talks to Discord

## Building

Library, bridge, harness and tests build with the dotnet CLI:

```bash
dotnet build
```

The widget needs full MSBuild. The dotnet CLI has no WindowsXaml targets and cannot even
*evaluate* a UWP project, so it is mapped in the solution such that `Any CPU` skips it —
which is why the line above works at all.

```bash
msbuild DiscordXboxWidget.sln -p:Configuration=Debug -p:Platform=x64
```

> **In Visual Studio, set the solution platform to x64 before F5.**
> `Any CPU` has no Build or Deploy entry for the widget, so F5 silently deploys nothing and
> reports success.

## Tests

```bash
dotnet test
```

49 tests covering the frame codec, PKCE generation, CDN avatar URLs, bridge payload round
trips, and `DiscordRpcSession` driven against a fake transport. The fake answers commands by
echoing nonces, so request correlation is exercised rather than bypassed.

They are weighted towards bugs this project actually hit rather than towards coverage:
capabilities derived from granted rather than requested scopes, the `VOICE_STATE_*`
subscriptions being present, `self_mute` counting as muted, `GET_CHANNELS` filtering to
type 2, and the default-avatar rules for both account systems.

## Talking to Discord directly

The harness connects to the local Discord client without any packaging or Game Bar involved.
`probe` and `wsprobe` need no registered application; the rest need an application id.

```bash
dotnet run --project src/Discord.Rpc.Harness -- watch <clientId>
```

| Mode | Purpose |
|---|---|
| `probe` | Named pipe framing |
| `wsprobe <id>` | WebSocket framing — a dead end for the widget, see [architecture](architecture.md) |
| `handshake <id>` | Auth; prints granted capabilities |
| `watch <id>` | Live channel, participants, speaking events |
| `toggle <id>` | `SET_VOICE_SETTINGS` round trip, restores afterwards |
| `channels <id>` | Guilds and voice channels, raw payload beside parsed result |
| `rawchannel <id>` | Unparsed channel payload, captured muted and unmuted |
| `mutediag <id>` | Every non-speaking dispatch frame around a mute |

`rawchannel` and `mutediag` are the fastest way to answer *"is Discord actually sending
this?"* before changing any parsing code. Both exist because parsing written from
documentation turned out to disagree with the wire in ways only a dump revealed.

## Bridge self-test

The bridge exercises its whole command surface against a live Discord client with no
packaging and no AppService, so a Discord-facing failure stays distinguishable from a
packaging one:

```bash
src/Discord.Rpc.Bridge/bin/Debug/net9.0-windows10.0.19041.0/win-x64/Discord.Rpc.Bridge.exe --selftest
```

It prints which application id it used and where that came from, connects, reads the current
channel and voice settings, round-trips a mute (restoring it), and lists guilds and voice
channels. `joinChannel` is only exercised when you are already in a channel — it re-joins the
one you are in rather than dragging you somewhere else.

## Logs

Two, because the widget's AppContainer cannot write outside its own storage:

```bash
type "$env:LOCALAPPDATA\DiscordXboxWidget\bridge.log"
```

```bash
Get-ChildItem "$env:LOCALAPPDATA\Packages\DiscordXboxWidget_*\LocalState\widget.log"
```

The package family-name suffix is derived from the manifest `Publisher`, so it changes
whenever that does — glob for it rather than hardcoding.

Both matter more than usual here: a Game Bar widget has no console, and WinRT exceptions
surface with the stack already unwound. Without a log, a crash is *"The parameter is
incorrect"* with no source and no stack trace.

## CI/CD

`ci.yml` runs on push and pull request, split into two jobs so a UWP toolchain problem cannot
mask a test failure:

- **Build and test** — needs only the .NET SDK
- **Build widget** — full MSBuild, installs the UWP workload if the runner lacks it, and
  asserts the bridge was actually packaged. A widget packaged without it builds and deploys
  cleanly and simply cannot reach Discord, so that check is load-bearing.

`release.yml` triggers on a `v*` tag, or manually via `workflow_dispatch` to exercise
packaging without publishing. It signs with `SIGNING_CERTIFICATE_BASE64` and
`SIGNING_CERTIFICATE_PASSWORD` when configured, publishes the public certificate alongside
the package (a self-signed MSIX is not installable without it), and is explicit in the
release notes when producing an unsigned build.

## Signing a package locally

The certificate subject must match the manifest `Publisher` exactly. Create the certificate
first, then read back its rendered subject:

```bash
New-SelfSignedCertificate -Type CodeSigningCert -Subject "CN=Your Name" -KeyAlgorithm RSA -KeyLength 2048 -CertStoreLocation "Cert:\CurrentUser\My" -NotAfter (Get-Date).AddYears(3) -FriendlyName "DiscordXboxWidget signing"
```

Export by thumbprint rather than by subject — subject matching needs the attribute order and
spacing to agree byte for byte, and returns nothing rather than erroring when it does not:

```bash
$pw = Read-Host "PFX password" -AsSecureString; Export-PfxCertificate -Cert Cert:\CurrentUser\My\<THUMBPRINT> -FilePath "$env:USERPROFILE\signing.pfx" -Password $pw
```

For CI, base64 the PFX into `SIGNING_CERTIFICATE_BASE64` and its password into
`SIGNING_CERTIFICATE_PASSWORD`.
