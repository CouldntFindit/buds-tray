# BudsTray

A small Windows tray icon that shows the battery of Samsung Galaxy Buds.

No installer, no account, no cloud: one `.exe` plus a `data` folder next to it.

## Features

- Per-bud and case battery, including charging state.
- Works with every Galaxy Buds generation (see below).
- Refreshes every 15 seconds and immediately after bud events.
- Reconnects automatically.
- Right-click menu with battery levels and the sound modes your model has.
- Coexists with Samsung's official Galaxy Buds app.

## Supported models

Galaxy Buds (2019), Buds+, Buds Live, Buds Pro, Buds2, Buds2 Pro, Buds FE,
Buds Core, Buds3, Buds3 Pro, Buds3 FE, Buds4, Buds4 Pro.

The protocol tables come from GalaxyBudsClient. Only Buds FE was tested on real
hardware here; the other models use the same protocol with their documented
per-model battery, charging and noise-control layouts.

## Requirements

- Windows 10 2004 (build 19041) or newer.
- .NET 10 desktop runtime for the published exe, or the .NET 10 SDK to build.
- Buds paired in **Settings → Bluetooth & devices**.

## Build and run

```powershell
dotnet build .\src\BudsTray\BudsTray.csproj -c Release
dotnet run --project .\src\BudsTray\BudsTray.csproj -c Release
```

Publish a single file:

```powershell
dotnet publish .\src\BudsTray\BudsTray.csproj -c Release -o .\publish `
  --self-contained false -p:PublishSingleFile=true `
  -p:DebugType=None -p:DebugSymbols=false
```

Run the tests:

```powershell
dotnet run --project .\tests\BudsTray.Tests\BudsTray.Tests.csproj -c Release
```

## Using it

Hover the icon for something like `L 80% · R 75% · Case 40%`. Parts that are
unknown are left out, and `charging` is appended when a level is charging.

Right-click for:

- Left / Right / Case levels (`--` when the case has not reported yet).
- Sound modes the connected model supports.
- **Exit**.

The icon turns red at 15 % or lower, shows a single bud when only one is out of
the case, and shows a slash while disconnected.

## The official Galaxy Buds app

The Buds accept one Bluetooth connection at a time, so both apps cannot read the
battery at the same time. BudsTray handles this by itself:

- **Official app window open** → BudsTray pauses and the tooltip says
  `Galaxy Buds app is open`. It reconnects when you close that window.
- **Official app only in the background** → BudsTray closes its background
  processes and takes the connection back.

To let the official app keep the connection instead, exit BudsTray, set
`"TakeOverFromGalaxyBudsApp": false` in `data\config.json`, then start it again.

## Settings

`data\config.json` is created next to the executable:

| Key | Meaning |
|---|---|
| `TakeOverFromGalaxyBudsApp` | Close Samsung's background processes when they hold the connection (default `true`). |
| `ShowPercentage` | Draw the percentage as text inside the tray icon. |
| `Theme` | `System`, `Light` or `Dark` for the tray menu. |

`data\budstray.log` holds connection diagnostics.

## Troubleshooting

| Problem | Fix |
|---|---|
| Slash on the icon | The Buds are off, out of range, or the official app is connected. |
| Case shows `--` | The case only reports a level while it is awake and nearby. |
| No sound-mode rows | They appear after the Buds report their model. Adaptive is only available on Buds3 Pro, Buds4 and Buds4 Pro. |
| No icon after login | Make a shortcut to `BudsTray.exe` in the `shell:startup` folder. |
| Buds were renamed | Nothing to do: detection uses the SPP service, not the name. |

## Credits

The Samsung SPP protocol was documented by
[GalaxyBudsClient](https://github.com/timschneeb/GalaxyBudsClient) (GPL-3.0).
BudsTray is released under GPL-3.0; see [LICENSE](LICENSE).
