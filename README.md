# Nimbus

A lightweight Windows tray app that keeps an eye on your remote-desktop dev VMs and surfaces, at a glance, **what each one is doing** — including the live title of the coding-assistant chat running inside the session.

Nimbus sits in the system tray as a cloud icon. Click it to pop a flyout (anchored to the tray, styled to match your Windows light/dark theme) listing every monitored VM window with its current activity status and the most recent chat titles read from the screen.

## Why

A remote desktop session (`msrdc` / the Windows App) only ever exposes **pixels** — there's no UI tree for the inner application. So the only way to know "what is my assistant doing in that VM right now" is to read the screen. Nimbus captures each VM window off-screen and uses the **on-device Windows OCR engine** to extract the chat title, then watches the conversation region for changes to flag activity — all without ever bringing the window to the foreground.

## Features

- **Tray flyout** anchored to the taskbar corner, themed to follow the system light/dark setting live.
- **Per-VM cards** showing the window name, the current chat title, and the last two previous titles (faded), so you can see how the conversation has moved.
- **Activity status** per VM — Active / Idle / Captured / Viewing / Minimized — derived from cheap change-detection signatures so OCR only runs when something actually changed.
- **Color-changing tray icon** that turns green when any tracked VM becomes active.
- **Click to focus** — click a card to bring that VM window to the foreground.
- **Pin** important VMs to the top.
- **Inline rename** — give any card a custom title (the pencil turns blue when a custom name is set; a clear button reverts to the detected title).
- **Configurable cadences** — separate intervals for status scans and title refreshes.
- **Start with Windows** toggle (per-user `Run` key).
- **Single-file executable**, no installer.

## Requirements

- Windows 10 (1903 / build 18362) or newer, 64-bit.
- An OCR language pack for your display language (Windows ships most by default). If OCR is unavailable, Nimbus still tracks windows and activity — it just can't read chat titles.
- For the small download: the [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0). The self-contained build needs nothing pre-installed.

## Download & run

Grab `Nimbus.exe`, double-click it, and look for the cloud icon in your tray. There's no install step — it runs from anywhere.

- **Framework-dependent** (~24 MB) — requires the .NET 9 Desktop Runtime.
- **Self-contained** (~186 MB) — bundles the runtime, runs on any 64-bit Windows.

## Settings

Open the flyout and click the gear:

| Setting | Default | Meaning |
| --- | --- | --- |
| Filter | `msrdc` | Match window title / process name (blank = all windows). |
| Update status every | `60s` | How often window states are scanned. |
| Refresh title every | `10s` | How often chat titles are re-read via OCR. |
| Start with Windows | off | Launch Nimbus automatically at sign-in. |

## Building from source

```powershell
git clone https://github.com/apsm100/Nimbus.git
cd Nimbus
dotnet build -c Release
```

Run the result from `bin\Release\net9.0-windows10.0.19041.0\Nimbus.exe`.

### Publishing

```powershell
# Smaller, needs .NET 9 Desktop Runtime on the target machine
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o .\publish-fd

# Larger, fully standalone
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

The app icon is generated from [`tools/make-icon.ps1`](tools/make-icon.ps1) into `app.ico`.

## How it works

1. **Enumerate** top-level windows matching the filter.
2. **Capture** each window to an off-screen bitmap (no foregrounding required).
3. **Signature** the conversation region — a cheap downscaled-grayscale hash that tolerates remote-desktop compression noise — to decide whether anything changed.
4. **OCR** the header band with the Windows on-device OCR engine to read the chat title, only when needed.
5. **Render** the results in the themed flyout and recolor the tray icon.

## Project layout

| File | Purpose |
| --- | --- |
| `MainWindow.xaml(.cs)` | The tray flyout UI, theming, positioning, timers. |
| `MonitoredWindow.cs` | View-model for a single tracked VM card (status, titles, history). |
| `WindowCapture.cs` | Window enumeration and off-screen bitmap capture. |
| `ScreenText.cs` | On-device OCR + activity-change detection. |
| `NativeMethods.cs` | Win32 / DWM interop. |
| `App.xaml(.cs)` | App bootstrap and the light/dark theme manager. |
| `tools/make-icon.ps1` | Generates the multi-resolution cloud `app.ico`. |

## License

[MIT](LICENSE) © apsm
