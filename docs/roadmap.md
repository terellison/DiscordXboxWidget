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

## Open question

Whether Discord's app verification unlocks general RPC access. If it does, the built-in
application id works for everyone and step 4 of the install disappears. If it does not, users
continue registering their own application.

The test is unambiguous: have someone who is **not** on the tester allowlist install a
release and try it without configuring anything. A 4006 means verification did not grant RPC
access. See [architecture](architecture.md#which-application-id-is-used).
