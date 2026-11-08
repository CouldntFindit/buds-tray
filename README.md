# BudsTray

A tiny Windows notification-area (system tray) battery indicator for **Samsung Galaxy Buds FE** (SM-R400N), and most other Galaxy Buds models that speak Samsung's SPP configuration protocol.

![status](https://img.shields.io/badge/platform-Windows%2010%2F11-blue) ![.NET](https://img.shields.io/badge/.NET-10-512BD4)

## What it does

- Lives in the tray with a glyph styled after the **Windows taskbar battery**,
  using the variant that matches the running OS: the rounded Windows 11 shell
  glyph on Windows 11 and the flatter, squarer Windows 10 glyph on Windows 10.
  The shell outline and nub are always white on a dark taskbar and near-black on
  a light one. The interior shows the **lowest earbud level** and turns Windows'
  low-battery red at **15 % or below**. While charging, the interior is **green
  up to the remaining level** (50 % charging fills half the shell) with a
  lightning bolt on top.
- **Disconnected:** the shell has a diagonal strike through it, so "no
  connection" is clearly different from a real empty (0 %) battery.
- When left and right differ by **more than 5 %**, the interior splits into two
  rows (top = left bud, bottom = right bud) so an imbalance is visible at a
  glance.
- **Single-bud mode:** when one bud is in the case or disconnected, the icon
  always describes the bud that is actually in use. It never borrows the other
  bud's (stale) percentage, even before the in-use bud has reported.
- Tooltip shows only what is connected, separated by middle dots
  (`Buds · L 78% · R 72% · Case 6% charging`): a bud that is in the case and a
  case that is not reporting are left out (the case shows `--` in the menu too).
- **Right-click** the tray icon for a small themed menu: **Left**, **Right** and
  **Case** percentages, the sound modes, and **Exit**. It follows the app theme
  (dark or light) and the numbers turn red at **15 % or below** and green while
  charging, mirroring the tray icon.
- **Sound modes:** the menu lists only the modes the connected model supports,
  with a check mark on the active one. **ANC**, **Ambient sound**, **Adaptive**
  and **Off** are available as follows:
  - Galaxy Buds, Buds+: Ambient sound + Off.
  - Galaxy Buds Live: ANC + Off (it has no traditional Ambient sound mode).
  - Galaxy Buds Pro, Buds2, Buds2 Pro, Buds FE, Buds Core, Buds3, Buds3 FE:
    ANC + Ambient sound + Off.
  - Galaxy Buds3 Pro, Buds4, Buds4 Pro: ANC + Ambient sound + Adaptive + Off.
    The regular Buds3 and Buds3 FE do **not** have the selectable Adaptive mode.
  Clicking a row switches the buds immediately; the current mode is read from
  status frames, so changes made on the phone or by touch controls show up in
  the menu too.
- The full **Galaxy Buds–style card** is parked for a later update: the code is
  still compiled in, but the tray icon no longer opens it. It has a monochrome
  product render of the connected model, model name, ring gauges and
  Reconnect/Settings/theme actions, and can be reattached from
  `TrayApplicationContext` when it returns.
- Percentages refresh **every 15 seconds** by sending `DEBUG_GET_ALL_DATA`
  (`id 38`), the same query GalaxyBudsClient uses for its debug page. On the
  Buds FE it answers with the live left/right state of charge. Pushed status
  frames are still applied immediately, so changes show up as soon as the Buds
  report them.
- Reconnects automatically with backoff when the Buds come into range or wake up.
- **Detects the Buds by Samsung's SPP service, not by name**, so a renamed pair
  (for example `TOYOTA Corolla`) still works.
- **Coexists with Samsung's official Galaxy Buds app.** The SPP channel is
  exclusive, so only one app can read the Buds at a time; they cannot be
  connected simultaneously. BudsTray hands the channel over instead of fighting:
  while the official app's window is open it pauses (tooltip
  `Galaxy Buds app is open`) and reconnects when the window closes. If only
  Samsung's background `QuickControls.exe` is squatting on the channel, BudsTray
  closes it and takes over. Set `"TakeOverFromGalaxyBudsApp": false` in
  `data\config.json` to never close it.
- Optional percentage-text icon.
- Optional "Start with Windows".
- No background service, no installer, no account, no cloud. Everything it writes
  stays in a `data` folder next to the executable.

The glyph is drawn at the exact tray size for the current DPI (16 px at 100 %,
24 px at 150 %) instead of being scaled up by Windows, and follows the taskbar
theme so it sits next to the system battery icon without standing out.

> This is the **notification area** ("system tray"), which is where Windows puts small status icons. It is not a Windows 11 taskbar deskband — those are no longer supported by the OS.

## Requirements

- Windows 10 2004+ or Windows 11.
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download) for the
  framework-dependent build (the SDK works too). Build self-contained if you
  need it on a machine without .NET.
- Galaxy Buds paired in **Settings → Bluetooth & devices**.
- The Buds' SPP channel is exclusive, so only one app can be connected at a
  time. Samsung's official **Galaxy Buds** app and **GalaxyBudsClient** can stay
  installed: BudsTray pauses while the official window is open and resumes when
  it closes. When only Samsung's background processes hold the channel, BudsTray
  closes them and connects (see
  [Samsung Galaxy Buds app conflict](#samsung-galaxy-buds-app-conflict)).

## Run the published build

If `publish\BudsTray.exe` already exists, just start it:

```powershell
.\publish\BudsTray.exe
```

The icon appears in the notification area within a few seconds. Windows may hide
new tray icons in the overflow flyout (`^`); drag it onto the taskbar to pin it.
Then right-click it for the battery menu.

## Build from source

```powershell
dotnet build .\src\BudsTray\BudsTray.csproj -c Release
dotnet run --project .\src\BudsTray\BudsTray.csproj
```

If the Buds are not connected, the tooltip says `disconnected` and the app
retries every 2–30 seconds.

## Publish

```powershell
dotnet publish .\src\BudsTray\BudsTray.csproj -c Release -o .\publish `
  --self-contained false -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false
```

This produces a single `publish\BudsTray.exe` (~25 MB, mostly the WinRT
projection). The **Start with Windows** option lives on the parked card's
Settings page, so it is temporarily unavailable; the existing registry entry
still works if it was enabled before.

## Data and logs

By default everything stays next to the exe:

```
publish\
  BudsTray.exe
  data\
    config.json
    budstray.log
```

If the exe folder is read-only, the app falls back to `%APPDATA%\BudsTray`.
To force a location, set `BUDS_TRAY_DATA` before launching:

```powershell
$env:BUDS_TRAY_DATA = "C:\path\to\data"
.\publish\BudsTray.exe
```

The log also opens from the card (**Settings → Open log file**).

## Tests

The harness checks the frame codec against a real Galaxy Buds FE frame and the
documented CRC vector:

```powershell
dotnet run --project .\tests\BudsTray.Tests\BudsTray.Tests.csproj -c Release
```

Extra modes:

| Command (append after `--`) | What it does |
|---|---|
| `--devices` | Lists paired Bluetooth devices and whether each exposes the SPP service. |
| `--connect` | Attempts a live connection and prints the first battery frame. |
| `--noise` | Connects and sends ANC then Off, printing the buds' replies (wear one bud; in the case the buds force Off). |
| `--icons` | Renders `artifacts\icons\icon-preview.png` plus raw 16/24 px PNGs for both OS styles. |
| `--popup` | Renders `artifacts\popup\popup-preview.png` (dark/light × main/settings, plus single-bud). |
| `--menu` | Renders the right-click menu in dark/light, with and without a reported case, plus the Adaptive variant. |

Example:

```powershell
dotnet run --project .\tests\BudsTray.Tests\BudsTray.Tests.csproj -c Release -- --devices
```

## How it works

The Buds expose a Bluetooth Classic **SPP/RFCOMM** service with UUID
`2e73a4ad-332d-41fc-90e2-16bef06523f2` (Samsung's manager channel, RFCOMM
channel 27). The app:

1. Enumerates paired Bluetooth devices via WinRT (`Windows.Devices.Bluetooth`).
2. Identifies the Buds by asking each device for that SPP service — the name is
   irrelevant. A remembered address is tried first, then connected devices.
3. Opens the RFCOMM service with `StreamSocket`.
4. Sends the `MANAGER_INFO` handshake (`id 136`, payload `01 01 22`) on connect,
   then polls `DEBUG_GET_ALL_DATA` (`id 38`) every 15 seconds. On the Buds FE
   that reply is 108 bytes with the live earbud SOC at offsets 42 (left) and
   48 (right); the case level and charging flags are carried over from the last
   status frame because the debug reply does not include them. The first reply
   after connecting is only partially initialised (an earbud record can still be
   zero), so a zero byte is treated as "no reading yet" and the last known level
   for that earbud is kept instead of flashing 0 %.
5. Parses pushed `STATUS_UPDATED` (96) and `EXTENDED_STATUS_UPDATED` (97) frames.
   On Buds FE the extended frame carries battery at payload offsets 2/3 (L/R)
   and 7 (case), and the charging bitfield at offset 43. Payload byte 6 encodes
   each bud's placement as a nibble (high = left, low = right: `0` disconnected,
   `1` wearing, `2` idle/out of case, `3` in case, `4` case closed). A bud counts
   as present only when it is worn or idle, which drives single-bud mode.
6. Tracks the sound mode in the same extended frame: payload byte 12 is the mode
   on Buds Pro and newer, the ambient flag is at byte 7 (Buds) or 8 (Buds+), and
   byte 12 is the ANC flag on Buds Live. Asynchronous changes arrive as
   `NOISE_CONTROLS_UPDATE` (119), `AMBIENT_MODE_UPDATED` (129) and
   `NOISE_REDUCTION_MODE_UPDATE` (155). Selecting a mode sends
   `NOISE_CONTROLS` (120, one mode byte: `1` ANC, `2` Ambient, `3` Adaptive,
   `0` Off) on Buds Pro and newer, `SET_AMBIENT_MODE` (128, boolean) on
   Buds/Buds+, and `SET_NOISE_REDUCTION` (152, boolean) on Buds Live.

Frame layout (little-endian):

```
FD | size_lo size_hi | id | payload... | crc_lo crc_hi | DD
```

- `size = 1 (id) + payload + 2 (crc)`
- header bits 0–9: size; bit 12: response flag
- CRC-16/XMODEM (poly `0x1021`, init `0`, no reflection) over `id + payload`

## Samsung Galaxy Buds app conflict

Samsung's official app (AppX `SAMSUNGELECTRONICSCO.LTD.GalaxyBuds`) ships two
processes that fight over the exclusive SPP channel:

| Process | Role |
|---|---|
| `QuickControls\QuickControls.exe` | Full-trust startup task (`Galaxy Buds Quick Controls`). It holds the Bluetooth connection and is started with Windows. |
| `BudsManager.exe` | UWP front end. The package's `com.samsung.PairedDeviceIdProvider` app service restarts it when QuickControls asks. |

Killing only `BudsManager.exe` is useless: it comes back in about 0.2 s. A
simultaneous connection is impossible because the earbuds accept **one RFCOMM
client** on the manager channel. BudsTray therefore shares it in two ways:

1. **Window open → BudsTray yields.** As soon as the official app's visible
   window appears, BudsTray closes its socket and stops retrying, so the
   official app gets the channel. The tooltip reads
   `Galaxy Buds app is open`. When the window closes, BudsTray waits 1.5 s and
   reconnects.
2. **Window closed → background squatter is closed.** If a connect attempt fails
   with `0x80072740` ("address already in use") while no official window is
   open, `TakeOverFromGalaxyBudsApp` (default `true`) stops `QuickControls.exe`
   first and then `BudsManager.exe`, and retries one second later. The takeover is
   skipped for executables outside the GalaxyBuds package folder.

Nothing is uninstalled or disabled permanently. The official app can be opened
again at any time; BudsTray simply pauses while it is in use.

To let the official app keep the channel even when it is only running in the
background, exit BudsTray, then edit `data\config.json` and start BudsTray again
(the file is rewritten when the paired device is remembered, so do not edit it
while the app is running):

```json
{
  "TakeOverFromGalaxyBudsApp": false
}
```

BudsTray then only displays the conflict and retries every 5 seconds.

The takeover is a fallback. For a quieter setup, disable the official app's
background start once in **Settings → Apps → Startup** and turn off
**Galaxy Buds Quick Controls**. The official app and the Store package stay
installed; BudsTray can launch it again from the Start menu when needed.

## Troubleshooting

| Symptom | Fix |
|---|---|
| `disconnected` forever | Pair the Buds in Windows first. Open the case so they are awake. |
| "Galaxy Buds app is open" | Normal handoff: BudsTray released the channel to the official app. Close the Galaxy Buds window and the tray reconnects. |
| "The Galaxy Buds app is using the connection" | Nothing to do by default: no official window is open, so BudsTray closes Samsung's `QuickControls.exe`/`BudsManager.exe` and reconnects. If you set `TakeOverFromGalaxyBudsApp` to `false`, close the official app yourself. |
| `The Buds' SPP server is unavailable` | Put both buds in the case, close it, reopen, then **Reconnect now**. |
| Icon is blank/gray | No status frame received yet. Hover for the tooltip, or use **Reconnect now**. |
| Case shows `--` | The case only reports a level while it is nearby/awake, so `0` is treated as "not reported" instead of showing a misleading `0 %`. |
| Sound-mode rows are missing | The mode list appears after the buds report their model in an extended status frame. Adaptive is only offered on Buds3 Pro, Buds4 and Buds4 Pro. |
| Autostart does nothing | Enable it from the published `BudsTray.exe`, not from `dotnet run`. |
| Buds were renamed | Nothing to do: detection uses the SPP UUID, not the name. Use **Forget remembered device** if you swapped pairs. |

## Credits and license

Protocol details were derived from the open-source
[GalaxyBudsClient](https://github.com/timschneeb/GalaxyBudsClient) project
(GPL-3.0), which documents and implements the Samsung SPP protocol. This project
is therefore released under **GPL-3.0** as well; the full text is in
[LICENSE](LICENSE).
