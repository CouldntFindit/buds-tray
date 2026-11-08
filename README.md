# BudsTray

A little tray app for Windows that shows the battery of Samsung Galaxy Buds.

It lives in the system tray and shows the left, right and case levels in the
tooltip and the right-click menu. It reconnects on its own when the buds come
back.

## Which buds it works with

Galaxy Buds (2019), Buds+, Buds Live, Buds Pro, Buds2, Buds2 Pro, Buds FE,
Buds Core, Buds3, Buds3 Pro, Buds3 FE, Buds4 and Buds4 Pro.

I only had a Buds FE to test with. The other models follow GalaxyBudsClient's
protocol tables, so they should work but haven't been tried on real buds.

## What you need

Windows 10 build 19041 or newer, and the buds paired in **Settings → Bluetooth
& devices**. The published exe needs the .NET 10 Desktop Runtime; building from
source needs the .NET 10 SDK.

## Build and run

```powershell
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

Hover the icon to see the battery, for example `L 80% · R 75% · Case 40%`.
Right-click for the levels, the sound modes your model supports, and **Exit**.

At 15% or lower the icon turns red. If your buds report placement and only one
is out of the case, the icon shows just that bud. A slash means disconnected.

## Samsung's own app

The buds only allow one connection at a time, so BudsTray and the official
Galaxy Buds app can't read them at the same moment.

If you open the official app's window, BudsTray steps aside and the tooltip says
`Galaxy Buds app is open`. Close the window and it reconnects. If the official
app is only running in the background, BudsTray closes it and takes the
connection back. You can turn that off in the config file.

## Settings

The app creates `data\config.json` next to the exe. The useful bits:

- `TakeOverFromGalaxyBudsApp` – close Samsung's background processes when they
  hold the connection (default `true`).
- `ShowPercentage` – put the percentage in the tray icon.
- `Theme` – `System`, `Light` or `Dark` for the menu.

Logs go to `data\budstray.log`. Exit the app before editing the file, then
start it again.

## Problems

- **Slash on the icon:** the buds are off, out of range, or the official app has
  the connection.
- **Case shows `--`:** the case only reports while it's awake and nearby.
- **No sound modes:** they show up once the buds report their model. Adaptive is
  only on Buds3 Pro, Buds4 and Buds4 Pro.
- **No icon after login:** make a shortcut to `BudsTray.exe` in the startup
  folder (press Win+R, type `shell:startup`, and put it in the folder that
  opens).
- **Renamed buds:** fine, detection doesn't use the name.

## Credits

The protocol details come from the
[GalaxyBudsClient](https://github.com/timschneeb/GalaxyBudsClient) project
(GPL-3.0). BudsTray is GPL-3.0 too; see [LICENSE](LICENSE).
