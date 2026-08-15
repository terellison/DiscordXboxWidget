# Roadmap

## Known gaps

**Placeholder tile art.** Everything in `widget/DiscordWidget/Assets/` and `GameBar/` is
generated flat blurple with a white circle. Functional, not designed.

**`joinChannel` has no automated coverage.** It is exercised manually and by the bridge
self-test only when you are already in a voice channel — the self-test re-joins your current
channel rather than moving you somewhere else without asking.

**Default avatars are unverified visually.** The URL construction is covered by tests and by
HTTP HEAD checks against the CDN, but a user with no avatar has not been seen rendered in the
widget.

## Not planned

Push-to-talk. Discord already provides global keybinds that work while a game has focus, and
routing PTT through this architecture would be worse: every key down and key up becomes an
IPC round trip through the bridge, clipping the start of speech. Discord's own PTT hooks
input directly.

A **mute toggle** hotkey via `XboxGameBarHotkeyWatcher` would be reasonable if someone wants
one — it is a single action rather than a per-syllable round trip — but it duplicates a
Discord keybind, so it has not been built.

## Ideas

- Per-user volume control (`SET_USER_VOICE_SETTINGS` supports it)
- Deafen state and server-mute shown distinctly, rather than folded into one icon
- Remember recent channels for faster switching than browsing the full server list
- Ship the packaging project's generated `Install.ps1`, which trusts the certificate and
  installs the dependencies in one step, instead of the manual sequence in the README

## Settled: the package carries its own .NET runtime

Moving to a packaging project made the bridge self-contained, taking the msix from roughly
9 MB to 43 MB. Kept deliberately rather than worked around.

A sideloaded package has no Store behind it, so a missing shared runtime cannot be resolved
at install time and would have to become a documented prerequisite. It was already an
undocumented one: releases up to and including v0.1.2 shipped a framework-dependent bridge
and listed no .NET requirement, so on a machine without the .NET 9 Desktop Runtime the
bridge could not start — indistinguishable, from the widget, from Discord not running.

Size is a one-time download. The alternative was a prerequisite most people would discover
by having the widget not work.

## Settled: users supply their own application id

This was briefly an open question — whether Discord's app verification would unlock general
RPC access, letting a shipped application id serve everyone.

Two independent findings closed it.

**Terms.** Discord's Developer Terms §2(d) classify the Application ID as a developer
credential and forbid embedding developer credentials in open source projects, so a built-in
id would not be permissible even if verification granted the scope.

**Verification actively breaks RPC.** App verification requires the application to belong to
a Team, and a team-owned application cannot request the `rpc` scope at all — `AUTHORIZE`
fails with `invalid_scope`. Measured directly, same Discord account, same machine, minutes
apart:

| Application | `identify` | `rpc` |
|---|---|---|
| Team-owned (verification candidate) | allowed | **refused, `invalid_scope`** |
| Personally owned | allowed | allowed |

The documented precondition is that the `rpc` scope is available to "the application owner"
plus a tester allowlist. Team ownership evidently stops the authorizing user counting as
that owner. Nothing in Discord's documentation mentions the interaction.

So verification and RPC access are mutually exclusive for an app like this. **Do not move
the application to a Team.** `Discord.Rpc.Harness scopetest <clientId>` reproduces the
result in about a minute if this ever needs re-checking.
