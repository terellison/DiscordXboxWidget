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

**Terms.** Discord's Developer Terms §2(d) classify the Application ID as a developer
credential and forbid embedding developer credentials in open source projects, so a built-in
id would not be permissible even if the scope were granted.

**Scale.** An unapproved application grants `rpc` only to accounts on its tester list, and
[the documentation](https://docs.discord.com/developers/topics/rpc) caps that at 50:

> We currently do not allow access to RPC for unapproved apps without being on the game's
> list of testers.
>
> We grant 50 testing spots, which should be ample for development. After approval, this
> restriction is removed.

Fifty named accounts is not distribution. Each user registering their own application is,
and it has the side benefit that people grant permissions to an application they own.

### The ownership asymmetry, and a correction

This project previously recorded that team-owned applications cannot use `rpc` at all, and
that verification therefore excluded RPC. **That was wrong**, and the error is worth keeping
because the measurement behind it looked convincing.

What was measured, same account, same machine, minutes apart:

| Application | `identify` | `rpc` |
|---|---|---|
| Personally owned | granted | granted |
| Team-owned, no testers added | granted | **refused, `invalid_scope`** |
| Team-owned, authorizing account added as a tester | granted | granted |

The third row was missing originally, and without it the refusal looked like a property of
team ownership. It is not. The real behaviour is an asymmetry the documentation does not
mention:

- A **personally-owned** application grants `rpc` to its owner implicitly, with no tester
  entry.
- A **team-owned** application grants nobody implicit access. Every account, including team
  members, must be on the tester list.

So verification and RPC access are **not** mutually exclusive. A Team is fine, provided
every user is explicitly added as a tester — which keeps the 50-account ceiling above, so it
changes nothing about distribution.

The lesson is about the shape of the evidence rather than Discord: two runs differing in one
variable still only isolate that variable if the documented precondition is satisfied in
both. It was not. `Discord.Rpc.Harness scopetest <clientId>` reproduces any row above in
about a minute.

### What would actually change this

RPC approval — the "after approval, this restriction is removed" in the quote above.
Discord's documentation states that approval exists but describes no way to request it. That
single unanswered question is now the only thing between this and general distribution.
