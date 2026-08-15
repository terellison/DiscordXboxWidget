# Roadmap

## Known gaps

**Windows Application Packaging Project.** `runFullTrust` builds emit `APPX0006` advising
that packaging should go through one. Current releases package directly from the UWP project,
which works but is not the supported route for sideload packages.

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
- A settings widget (Game Bar supports `Type="Settings"`) for the application id, replacing
  hand-editing `config.json`
- Remember recent channels for faster switching than browsing the full server list

## Settled: users supply their own application id

This was briefly an open question — whether Discord's app verification would unlock general
RPC access, letting a shipped application id serve everyone.

It is moot. Discord's Developer Terms §2(d) classify the Application ID as a developer
credential and forbid embedding developer credentials in open source projects, so a built-in
id would not be permissible even if verification granted the scope. Registration is a
one-time two-minute step, and it means users authorize an application they control.

Verification remains worth completing for its own sake, but it does not change the install.
