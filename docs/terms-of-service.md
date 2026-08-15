# Terms of Service

**Effective 15 August 2026**

DiscordXboxWidget is a free, open-source hobby project. These terms describe what you can
expect from it, which is deliberately modest.

## What this is

A program you install and run on your own computer. There is no service behind it — no
accounts, no servers, nothing hosted by the author. "Terms of Service" is the label Discord's
developer portal asks for; in practice this is a statement about a program you run yourself.

## Licence

The software is licensed under the **GNU General Public License v3.0**. The full text is in
[LICENSE](../LICENSE) and it governs your rights to use, modify and redistribute the code.
Where these terms and the GPL disagree, the GPL wins.

## No warranty

The software is provided **as is, without warranty of any kind**, express or implied,
including but not limited to warranties of merchantability, fitness for a particular purpose
and non-infringement. This restates sections 15 and 16 of the GPL, which are the operative
text.

In plain terms: this is a hobby project maintained in spare time. It may break, it may stop
working when Discord changes something, and there is no guarantee it will be updated. Do not
rely on it for anything important.

## No affiliation

This project is **not affiliated with, endorsed by, or sponsored by Discord Inc. or Microsoft
Corporation.**

"Discord" is a trademark of Discord Inc. "Xbox", "Xbox Game Bar" and "Windows" are trademarks
of Microsoft Corporation. They are used here only to describe what the software works with.

## Your responsibilities

By using this software you agree that:

- You will comply with [Discord's Terms of Service](https://discord.com/terms) and
  [Developer Terms](https://discord.com/developers/docs/policies-and-agreements/developer-terms-of-service).
  This application uses Discord's local RPC interface with permissions you grant explicitly.
- If you register your own Discord application to use with it, that application is yours.
  You are responsible for it and for its compliance with Discord's policies.
- You are responsible for your own use of it, including anything you do in a voice channel
  through it.

## Installation and signing

Releases are distributed as sideloaded packages signed with a self-signed certificate, not
one issued by a certificate authority. Installing requires you to add that certificate to
your machine's trusted store.

You should understand what that means before doing it. Trusting a certificate is a real
decision, and you are free to inspect the certificate, build from source instead, or not
install it at all. The reasons this project cannot be distributed through the Microsoft
Store are documented in [the architecture notes](architecture.md#distribution).

## Availability and support

There is no service to be available or unavailable, and no support commitment. Bug reports
and pull requests are welcome at
[the repository](https://github.com/terellison/DiscordXboxWidget/issues), and will be looked
at when time allows.

## Changes

Any change to these terms will be committed to the repository, so their full history is
public and auditable.

## Contact

Open an issue at
[github.com/terellison/DiscordXboxWidget/issues](https://github.com/terellison/DiscordXboxWidget/issues).
