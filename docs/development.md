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

To produce an installable package, build the packaging project. It builds the widget and the
bridge itself, as project references, so there is nothing to build first and no ordering to
get right:

```bash
msbuild packaging/DiscordXboxWidget/DiscordXboxWidget.wapproj -t:Restore -p:Configuration=Release -p:Platform=x64
```

```bash
msbuild packaging/DiscordXboxWidget/DiscordXboxWidget.wapproj -p:Configuration=Release -p:Platform=x64
```

The msix lands in `packaging/DiscordXboxWidget/AppPackages/`, unsigned unless you pass a
certificate — see [Signing a package locally](#signing-a-package-locally).

> **In Visual Studio, set the solution platform to x64 and make `DiscordXboxWidget` the
> startup project before F5.** `Any CPU` has no Build entry for the widget, so it silently
> builds nothing and reports success. The packaging project is the only one with a Deploy
> entry: deploying the widget project alone would install a package with no bridge in it.

> **Release only:** the widget compiles through .NET Native, which is the only configuration
> where `Properties/Default.rd.xml` is consulted. A Debug package proves the layout and the
> manifest; it does not prove the runtime directives. When checking a change to those, verify
> the ILC step actually ran — `widget/DiscordWidget/obj/x64/Release/ilc/ilclog.csv` should be
> freshly written and mention `Default.rd.xml`. An incremental build that skips ILC reports
> success and emits no warning either way.

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
- **Build package** — full MSBuild, installs the UWP workload if the runner lacks it, then
  opens the produced msix and asserts three things: the manifest's `fullTrustProcess` path
  resolves to a file actually in the package, the widget executable is present, and the
  package identity is still `DiscordXboxWidget`. All three are load-bearing. A package
  missing the bridge installs cleanly and cannot reach Discord; a changed identity turns an
  upgrade into a second, unrelated install.

`release.yml` triggers on a `v*` tag, or manually via `workflow_dispatch` to exercise
packaging without publishing. It signs with `SIGNING_CERTIFICATE_BASE64` and
`SIGNING_CERTIFICATE_PASSWORD` when configured, publishes the public certificate alongside
the package (a self-signed MSIX is not installable without it), and is explicit in the
release notes when producing an unsigned build.

## Cutting a release

The package filename contains the version, and two files quote it literally. Both describe
the *published* release, so they change when the release is cut, not before:

1. `packaging/DiscordXboxWidget/Package.appxmanifest` — bump `Version`
2. `README.md` — the `Add-AppxPackage` command in **Install**
3. `.github/release-install-notes.md` — the same command

The artifact is named after the packaging project, so it is
`DiscordXboxWidget_<version>_x64.msix`. Releases up to v0.1.2 were built by the widget
project and named `DiscordWidget_<version>_x64.msix`; links to those still resolve.

Then tag with `--cleanup=verbatim`, or git strips every `#` heading out of the annotation and
the release notes arrive as a wall of unformatted text.

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
