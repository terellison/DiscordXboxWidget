
## Installing

This package is signed with a self-signed certificate, so Windows must be told to trust it
before it will install. In an **elevated** PowerShell:

```powershell
Import-Certificate -FilePath .\DiscordXboxWidget.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople
Add-AppxPackage .\DiscordXboxWidget_0.1.0.0_x64.msix
```

Then press **Win+G** and pick **Discord Voice** from the widget menu. The Discord desktop
client must be running.

Full instructions, including what to do if Discord refuses the built-in application, are in
the [README](https://github.com/terellison/DiscordXboxWidget#install).
