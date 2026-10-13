
# Windows S0 Power Override

A lightweight Windows tray utility that hibernates the laptop after a configurable period without keyboard/mouse input, intended for Windows systems using Modern Standby (S0).

## Features

- Configurable inactivity timeout: 1 minute through 24 hours, with common presets.
- Default timeout: 60 minutes.
- Resets the inactivity baseline on Windows resume, logon, and unlock to avoid an immediate second hibernation due to stale idle time.
- Saves settings under `%LOCALAPPDATA%\WindowsS0PowerOverride\settings.json`.
- Optional startup through the current user's Run registry key; no Task Scheduler.
- Optional execution-state request to prevent automatic system sleep while monitoring. Enabled by default because Windows may suspend ordinary user-session monitoring during Modern Standby. The display can still turn off. This can increase battery consumption.

## Build

Requires the .NET 8 SDK.

Open PowerShell in this folder and run:

```powershell
dotnet publish .\WindowsS0PowerOverride.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

Output:

```text
bin\Release\net8.0-windows\win-x64\publish\WindowsS0PowerOverride.exe
```

Copy the executable to a permanent folder before enabling startup. Run it and right-click its tray icon to configure the timeout and startup behavior.

## Test

1. Set the timeout to 1 or 2 minutes from the tray menu.
2. Leave the laptop untouched and verify hibernation.
3. Resume and log in/unlock, then leave it idle again.
4. Confirm that it waits the full configured interval again.
5. Set the desired timeout.

Save all work before testing. The application invokes `shutdown.exe /h`; firmware, power policy, or Windows configuration can still prevent hibernation.

## Notes

- This is a user-session tray application, not a Windows service. `GetLastInputInfo` reports input for the calling interactive session.
- It starts a fresh inactivity interval on resume and on logon/unlock.
- While the application itself is suspended, it cannot actively count time. It re-establishes a fresh baseline when Windows reports resume.
- The code has not been compiled or validated on every Modern Standby device. Test with a short timeout before relying on it.

## Remove

Uncheck **Start with Windows**, exit from the tray menu, then delete the application folder. Settings remain in `%LOCALAPPDATA%\WindowsS0PowerOverride` until manually deleted.
