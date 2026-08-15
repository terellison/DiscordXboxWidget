
## Installing

Download every file attached to this release into the same folder.

This package is signed with a self-signed certificate, so Windows must be told to trust it
before it will install. In an **elevated** PowerShell:

```powershell
Import-Certificate -FilePath .\DiscordXboxWidget.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople
```

Then install, passing the framework packages alongside it — Windows cannot fetch them from
the Store for a sideloaded app, and without them installation fails with `0x80073CF3`:

```powershell
Add-AppxPackage -Path .\DiscordWidget_0.1.0.0_x64.msix -DependencyPath `
  .\Microsoft.NET.Native.Framework.2.2.appx, `
  .\Microsoft.NET.Native.Runtime.2.2.appx, `
  .\Microsoft.VCLibs.x64.14.00.appx
```

Then press **Win+G** and pick **Discord Voice** from the widget menu. The Discord desktop
client must be running.

Full instructions, including what to do if Discord refuses the built-in application, are in
the [README](https://github.com/terellison/DiscordXboxWidget#install).
