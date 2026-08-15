# Xbox Game Bar widget notes

Things that cost time while building this, kept separate because they apply to any Game Bar
widget rather than to this project specifically. Several contradict the official
documentation.

## The manifest

**The extension name is `microsoft.gameBarUIExtension`**, not `microsoft.gameBar`. Getting it
wrong produces a package that installs cleanly and never appears in the widget menu.

**The proxy/stub registration block goes outside `<Applications>`**, as a sibling of it, not
inside the `<Application>` element with the widget extension. Without it the Game Bar private
interfaces fail to marshal and the widget errors on load rather than at build.

**`AppListEntry="none"`** keeps a widget-only app out of the Start menu.

## Activation

Game Bar launches widgets by **protocol activation** with the `ms-gamebarwidget:` scheme.
`OnLaunched` is never called — all the work happens in `OnActivated`.

In C#, `IProtocolActivatedEventArgs.Uri` is a `System.Uri`, so the scheme is `.Scheme`. The
documentation's `SchemeName` is the C++/WinRT projection and does not compile in C#.

The `XboxGameBarWidget` object **must be held for the app's lifetime** — it owns the private
channel to Game Bar that drives focus and input transitions. Repeat activations must *not*
construct a second one; guard on `IsLaunchActivation`.

## Idle shutdown during voice

`XboxGameBarWidgetActivity` suppresses Game Bar's idle shutdown, which otherwise kills the
widget mid-call. The docs name voice chat as its motivating case.

**Its constructor throws if an activity with the same id already exists.** A fast
leave-then-join produces exactly that, because `Complete()` on the previous activity has not
taken effect yet. Catch it — the activity only degrades behaviour, whereas letting it escape
takes down the widget.

Clear the field even when `Complete()` fails, or a dead activity blocks every later attempt.

## Exceptions

Anything thrown inside a dispatcher callback has no caller to catch it and becomes an
app-level crash. That includes handlers reached indirectly through property-change
notifications.

WinRT exceptions arrive with the stack already unwound. A debugger will show
`ArgumentException 0x80070057`, *"The parameter is incorrect"*, with `<Cannot evaluate the
exception source>` and no stack trace. Log the exception yourself at every boundary, or these
are effectively undiagnosable.

## Reaching anything outside the AppContainer

A widget is an AppContainer, which rules out most local IPC:

- **Named pipes do not work**, despite the docs listing them. See
  [XboxGameBarSamples#44](https://github.com/microsoft/XboxGameBarSamples/issues/44).
- **Loopback is blocked by default**, exemptable for sideloaded packages via
  `CheckNetIsolation LoopbackExempt` but never for Store-distributed ones.
- **`AppServiceConnection` to an in-package full-trust process works** and needs no
  exemption. Declare `windows.appService` with *no* `EntryPoint` for in-process activation
  (it then arrives at `Application.OnBackgroundActivated`), plus a
  `desktop:Extension` of category `windows.fullTrustProcess` and the `runFullTrust`
  restricted capability.

`FullTrustProcessLauncher` comes from the Windows Desktop Extensions SDK, which a UWP project
needs an explicit `SDKReference` for. `LaunchFullTrustProcessForCurrentAppWithParametersAsync`
does not exist — parameters must be declared as a manifest `ParameterGroup`.

## Build and packaging traps

**Solution platform matters.** A UWP project has no `Any CPU` configuration. If the solution's
active platform is `Any CPU` and the widget has no Build/Deploy entry for it, F5 silently
deploys nothing and reports success.

**The dotnet CLI cannot evaluate a UWP project at all** — it has no WindowsXaml targets, so
even `dotnet sln add` fails. Use full MSBuild.

**Content packaged by path is fragile.** If you package another project's output directory,
pin that project's `OutputPath`: building as part of a solution with `Platform=x64` inserts an
extra `x64\` path segment, and the package then ships without the content, with no error.

**Runtime directives must be package content.** `Microsoft.NetNative.targets` collects
`.rd.xml` files from `@(AppxPackagePayload)`, so a `Default.rd.xml` declared as `<None>` — or
as `<RdXmlFile>` — is silently ignored and Release warns `ILT0027` that no directives exist.
Declare it as `<Content>`.

**Incremental builds skip .NET Native entirely.** An incremental Release build emits no
`ILT0027` regardless, because `ILTransform` never runs. Verifying anything about runtime
directives requires `-t:Rebuild` and confirming `Processing application code` and
`Generating native code` appear in the log.

## x:Bind

Function bindings are invoked as **instance** methods on the page. Declaring them `static`
produces `CS0176` from generated code rather than from anything you wrote.
