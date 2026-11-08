# BudsTray

A small Windows tray application that displays the battery level of Samsung
Galaxy Buds.

BudsTray sits in the notification area and shows left, right and case levels in
the tooltip and the right-click menu. It reconnects automatically when the buds
return.

## Download

Download `BudsTray.exe` from the
[latest release](https://github.com/CouldntFindit/buds-tray/releases/latest) and
run it. The .NET 10 Desktop Runtime is required. Settings and the log live in a
`data` folder next to the executable.

## Supported models

Galaxy Buds (2019), Buds+, Buds Live, Buds Pro, Buds2, Buds2 Pro, Buds FE,
Buds Core, Buds3, Buds3 Pro, Buds3 FE, Buds4 and Buds4 Pro.

Only the Galaxy Buds FE has been tested on real hardware. The remaining models
are implemented from GalaxyBudsClient's protocol tables and have not been
verified on physical devices.

## Requirements

Windows 10 build 19041 or later, with the buds paired in **Settings → Bluetooth
& devices**. The published executable requires the .NET 10 Desktop Runtime;
building from source requires the .NET 10 SDK.

## Build and run

```powershell
dotnet run --project .\src\BudsTray\BudsTray.csproj -c Release
```

Publish as a single file:

```powershell
dotnet publish .\src\BudsTray\BudsTray.csproj -c Release -o .\publish `
  --self-contained false -p:PublishSingleFile=true `
  -p:DebugType=None -p:DebugSymbols=false
```

Run the tests:

```powershell
dotnet run --project .\tests\BudsTray.Tests\BudsTray.Tests.csproj -c Release
```

## Usage

Hover over the icon to see the battery levels, for example
`L 80% · R 75% · Case 40%`. Right-click for the levels, the sound modes the
connected model supports, and **Exit**.

The icon turns red at 15% or below. When the buds report placement and only one
is out of the case, only that bud is shown. A slash indicates a disconnected
state.

## Samsung Galaxy Buds app

The buds accept only one connection at a time, so BudsTray and Samsung's
official Galaxy Buds app cannot read the battery simultaneously.

Opening the official app's window causes BudsTray to release the connection and
show `Galaxy Buds app is open` in the tooltip; it reconnects when the window is
closed. If the official app is running only in the background, BudsTray closes
its background processes and takes the connection. This behaviour can be
disabled in the configuration file.

## Settings

The application creates `data\config.json` next to the executable. Available
options:

- `TakeOverFromGalaxyBudsApp` – close Samsung's background processes when they
  hold the connection (default `true`).
- `ShowPercentage` – show the battery percentage inside the tray icon.
- `Theme` – `System`, `Light` or `Dark` menu theme.

Logs are written to `data\budstray.log`.

## Troubleshooting

- **Slash on the icon:** the buds are switched off, out of range, or the official
  app holds the connection.
- **Case shows `--`:** the case reports a level only while it is awake and
  nearby.
- **No sound modes:** they appear once the buds report their model. Adaptive is
  available only on Buds3 Pro, Buds4 and Buds4 Pro.
- **No icon after login:** create a shortcut to `BudsTray.exe` in the startup
  folder (`Win+R` → `shell:startup`).
- **Renamed buds:** no action needed; detection uses the Bluetooth service, not
  the device name.

## Credits

The protocol details are based on the
[GalaxyBudsClient](https://github.com/timschneeb/GalaxyBudsClient) project
(GPL-3.0). BudsTray is licensed under GPL-3.0; see [LICENSE](LICENSE).
