# Privacy Policy

**Effective 15 August 2026**

DiscordXboxWidget is a program that runs entirely on your own computer. It has no backend,
no accounts, and no analytics. The author operates no servers and receives no data from it —
there is nowhere for your information to be sent.

## What is not collected

- No personal information is transmitted to the author or to any third party other than
  Discord itself
- No analytics, telemetry, crash reporting, or usage statistics
- No advertising or tracking identifiers
- No message content — the application never requests Discord's message scopes and cannot
  read your conversations
- **No audio.** The application never accesses your microphone. Muting works by asking the
  Discord client to mute itself; audio is handled entirely by Discord.

## Data the application handles

All of this stays on your computer.

**Held in memory while the widget is open, never written to disk:** the name of your current
voice channel, and the display names, nicknames, avatars, user IDs, speaking status and
mute/deafen state of people in it. This is read live from your local Discord client and
discarded when the widget closes.

**Stored on your computer:**

| Location | Contents |
|---|---|
| `%LOCALAPPDATA%\DiscordXboxWidget\token-*.bin` | Your Discord access token, encrypted with Windows DPAPI and readable only by your Windows user account |
| `%LOCALAPPDATA%\DiscordXboxWidget\config.json` | A Discord application ID. Not personal information. |
| `%LOCALAPPDATA%\DiscordXboxWidget\bridge.log` | Diagnostic messages: start and stop events, and error details |
| `%LOCALAPPDATA%\Packages\DiscordXboxWidget_*\LocalState\widget.log` | Diagnostic messages: unhandled errors in the widget |

The logs record errors and lifecycle events, not activity. They are not designed to contain
usernames or channel contents, though error text returned by Discord may occasionally include
identifiers. Nothing reads or uploads them; they exist so that you can diagnose a problem, or
paste one into a bug report if you choose to.

You can delete any of these files at any time. Deleting the token file simply means Discord
will ask you to authorize again.

## Network connections

The application contacts exactly three things:

1. **Your local Discord client**, over a named pipe on your own machine. This never leaves
   your computer.
2. **`discord.com`**, once, to exchange an authorization code for an access token when you
   first authorize it.
3. **`cdn.discordapp.com`**, to download the avatar images of people in your voice channel.

Requests 2 and 3 go to Discord, and are subject to
[Discord's Privacy Policy](https://discord.com/privacy). As with any web request, Discord can
see that a request was made from your IP address. There are no other network destinations.

## Your Discord data

This application acts on your behalf against your own Discord account, using permissions you
grant through Discord's own consent dialog. It requests two scopes:

- **`rpc`** — to read your current voice channel and control your mute and deafen state
- **`identify`** — to know which participant in the list is you

You can revoke access at any time from Discord under **User Settings → Authorized Apps**.

Any data shown in the widget belongs to Discord and is governed by their policies, not this
one.

## Children

This application is not directed at children. Discord requires its users to be at least 13,
or older where local law requires.

## Changes

Any change to this policy will be committed to
[this repository](https://github.com/terellison/DiscordXboxWidget), so its full history is
public and auditable.

## Contact

Open an issue at
[github.com/terellison/DiscordXboxWidget/issues](https://github.com/terellison/DiscordXboxWidget/issues).
